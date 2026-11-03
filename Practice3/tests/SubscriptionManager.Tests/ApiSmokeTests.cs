using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SubscriptionManager.Shared.Persistence;

namespace SubscriptionManager.Tests;

/// <summary>Приложение целиком на временной SQLite-БД.</summary>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"sm-test-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder.ConfigureServices(services =>
        {
            services.Remove(services.Single(d => d.ServiceType == typeof(DbContextOptions<AppDbContext>)));
            services.AddDbContext<AppDbContext>(o => o.UseSqlite($"Data Source={_dbPath}"));
        });

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch (IOException) { }
    }
}

/// <summary>Автоматизированный аналог ручных смоук-тестов (curl): сквозные сценарии по HTTP.</summary>
[TestClass]
public class ApiSmokeTests
{
    private const string Password = "Password1!";
    private static ApiFactory _factory = null!;

    [ClassInitialize] public static void Init(TestContext _) => _factory = new ApiFactory();
    [ClassCleanup] public static void Cleanup() => _factory.Dispose();

    private static string NewEmail() => $"u{Guid.NewGuid():N}@example.com";

    private static async Task<HttpClient> LoggedInClientAsync(string? email = null)
    {
        email ??= NewEmail();
        var anon = _factory.CreateClient();
        Assert.AreEqual(HttpStatusCode.Created, (await anon.PostAsJsonAsync("/api/auth/register", new { email, password = Password })).StatusCode);
        var login = await anon.PostAsJsonAsync("/api/auth/login", new { email, password = Password });
        Assert.AreEqual(HttpStatusCode.OK, login.StatusCode);
        var token = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!;
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static Task<HttpResponseMessage> Patch(HttpClient c, string url, object body) =>
        c.SendAsync(new HttpRequestMessage(HttpMethod.Patch, url) { Content = JsonContent.Create(body) });

    private static async Task<string> CreateSubscriptionAsync(HttpClient c, string currency = "USD")
    {
        var r = await c.PostAsJsonAsync("/api/subscriptions",
            new { name = "Netflix", amount = 9.99m, currency, period = "Monthly", startDate = "2026-01-15" });
        Assert.AreEqual(HttpStatusCode.Created, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    // ---------- FR-01 / FR-02 / FR-08 / коды ошибок ----------

    [TestMethod]
    public async Task Register_DuplicateEmail_409_InvalidEmail_422_MalformedJson_400()
    {
        var c = _factory.CreateClient();
        var email = NewEmail();
        Assert.AreEqual(HttpStatusCode.Created, (await c.PostAsJsonAsync("/api/auth/register", new { email, password = Password })).StatusCode);

        Assert.AreEqual(HttpStatusCode.Conflict, (await c.PostAsJsonAsync("/api/auth/register", new { email = email.ToUpper(), password = Password })).StatusCode);
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, (await c.PostAsJsonAsync("/api/auth/register", new { email = "bad", password = Password })).StatusCode);
        var broken = await c.PostAsync("/api/auth/register", new StringContent("{bad json", Encoding.UTF8, "application/json"));
        Assert.AreEqual(HttpStatusCode.BadRequest, broken.StatusCode);
    }

    [TestMethod]
    public async Task Login_WrongPassword_401_AndProtectedEndpointsRequireToken()
    {
        var c = _factory.CreateClient();
        var email = NewEmail();
        await c.PostAsJsonAsync("/api/auth/register", new { email, password = Password });

        Assert.AreEqual(HttpStatusCode.Unauthorized, (await c.PostAsJsonAsync("/api/auth/login", new { email, password = "Wrong-pass1" })).StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/subscriptions")).StatusCode);

        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "garbage");
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/users/me")).StatusCode);
    }

    [TestMethod]
    public async Task Profile_GetAndPatchEmail()
    {
        var email = NewEmail();
        var c = await LoggedInClientAsync(email);

        var me = await (await c.GetAsync("/api/users/me")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.AreEqual(email, me.GetProperty("email").GetString());

        var newEmail = NewEmail();
        var patched = await Patch(c, "/api/users/me", new { email = newEmail });
        Assert.AreEqual(HttpStatusCode.OK, patched.StatusCode);
        Assert.AreEqual(newEmail, (await patched.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("email").GetString());
    }

    // ---------- FR-04 / FR-07 ----------

    [TestMethod]
    public async Task Subscriptions_FullCrudFlow()
    {
        var c = await LoggedInClientAsync();
        var id = await CreateSubscriptionAsync(c);

        Assert.AreEqual(HttpStatusCode.OK, (await c.GetAsync($"/api/subscriptions/{id}")).StatusCode);
        var patched = await Patch(c, $"/api/subscriptions/{id}", new { amount = 12.5m, status = "Paused" });
        Assert.AreEqual(HttpStatusCode.OK, patched.StatusCode);
        Assert.AreEqual(12.5m, (await patched.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("amount").GetDecimal());

        var list = await (await c.GetAsync("/api/subscriptions")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.AreEqual(1, list.GetProperty("total").GetInt32());

        Assert.AreEqual(HttpStatusCode.OK, (await c.DeleteAsync($"/api/subscriptions/{id}")).StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, (await c.GetAsync($"/api/subscriptions/{id}")).StatusCode);
        var after = await (await c.GetAsync("/api/subscriptions")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.AreEqual(0, after.GetProperty("total").GetInt32());
    }

    [TestMethod]
    public async Task Subscriptions_InvalidBody_422_AndUnknownId_404()
    {
        var c = await LoggedInClientAsync();

        var bad = await c.PostAsJsonAsync("/api/subscriptions",
            new { name = "X", amount = -5, currency = "USD", period = "Monthly", startDate = "2026-01-15" });
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, bad.StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, (await c.GetAsync($"/api/subscriptions/{Guid.NewGuid()}")).StatusCode);
    }

    [TestMethod]
    public async Task AnotherUsersResources_AreForbidden_403()
    {
        var alice = await LoggedInClientAsync();
        var bob = await LoggedInClientAsync();
        var id = await CreateSubscriptionAsync(alice);

        Assert.AreEqual(HttpStatusCode.Forbidden, (await bob.GetAsync($"/api/subscriptions/{id}")).StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await Patch(bob, $"/api/subscriptions/{id}", new { name = "Hacked" })).StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await bob.DeleteAsync($"/api/subscriptions/{id}")).StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await bob.GetAsync($"/api/payments?subscriptionId={id}")).StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await bob.PostAsJsonAsync($"/api/subscriptions/{id}/payments",
            new { amount = 1, currency = "USD", paymentDate = "2026-02-15", externalId = "x" })).StatusCode);
    }

    // ---------- FR-05 / FR-06 / NFR-03 ----------

    [TestMethod]
    public async Task Payments_ReplayOfSameExternalId_IsIdempotent_201_then_200()
    {
        var c = await LoggedInClientAsync();
        var id = await CreateSubscriptionAsync(c);
        var body = new { amount = 9.99m, currency = "USD", paymentDate = "2026-02-15", externalId = "ext-123" };

        Assert.AreEqual(HttpStatusCode.Created, (await c.PostAsJsonAsync($"/api/subscriptions/{id}/payments", body)).StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, (await c.PostAsJsonAsync($"/api/subscriptions/{id}/payments", body)).StatusCode);

        var list = await (await c.GetAsync("/api/payments")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.AreEqual(1, list.GetArrayLength());
    }

    [TestMethod]
    public async Task Analytics_SumsPerCurrency_AndKeepsHistoryAfterSubscriptionDeletion()
    {
        var c = await LoggedInClientAsync();
        var id = await CreateSubscriptionAsync(c);
        foreach (var (ext, date) in new[] { ("a", "2026-01-15"), ("b", "2026-02-15"), ("c", "2026-06-15") })
            await c.PostAsJsonAsync($"/api/subscriptions/{id}/payments", new { amount = 10m, currency = "USD", paymentDate = date, externalId = ext });
        await c.DeleteAsync($"/api/subscriptions/{id}");

        var report = await (await c.GetAsync("/api/analytics/expenses?dateFrom=2026-01-01&dateTo=2026-03-31")).Content.ReadFromJsonAsync<JsonElement>();

        Assert.AreEqual(20m, report.GetProperty("totals")[0].GetProperty("total").GetDecimal());
        Assert.AreEqual("USD", report.GetProperty("totals")[0].GetProperty("currency").GetString());
        Assert.AreEqual(2, report.GetProperty("paymentsCount").GetInt32());
        var history = await c.GetAsync($"/api/payments?subscriptionId={id}");
        Assert.AreEqual(HttpStatusCode.OK, history.StatusCode);
        Assert.AreEqual(3, (await history.Content.ReadFromJsonAsync<JsonElement>()).GetArrayLength());
    }

    [TestMethod]
    public async Task Analytics_MissingOrReversedDates_422()
    {
        var c = await LoggedInClientAsync();

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, (await c.GetAsync("/api/analytics/expenses")).StatusCode);
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, (await c.GetAsync("/api/analytics/expenses?dateFrom=2026-03-01&dateTo=2026-01-01")).StatusCode);
    }

    // ---------- NFR-06 документация ----------

    [TestMethod]
    public async Task Swagger_DocumentIsPublished_AndListsMainEndpoints()
    {
        var doc = await _factory.CreateClient().GetStringAsync("/swagger/v1/swagger.json");

        foreach (var path in new[] { "/api/auth/register", "/api/subscriptions/{id}", "/api/payments", "/api/analytics/expenses" })
            StringAssert.Contains(doc, path);
    }
}
