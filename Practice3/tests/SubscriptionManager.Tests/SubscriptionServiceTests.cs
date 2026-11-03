using Microsoft.VisualStudio.TestTools.UnitTesting;
using SubscriptionManager.Modules.Subscriptions.Application;
using SubscriptionManager.Modules.Subscriptions.Contracts;
using SubscriptionManager.Modules.Subscriptions.Domain;
using SubscriptionManager.Shared;
using SubscriptionManager.Tests.Support;

namespace SubscriptionManager.Tests;

[TestClass]
public class SubscriptionServiceTests
{
    private static readonly Guid Owner = Guid.NewGuid();
    private static readonly Guid Other = Guid.NewGuid();
    private static readonly DateOnly Start = new(2026, 1, 15);

    private FakeSubscriptionRepository _repo = null!;
    private SubscriptionService _sut = null!;

    [TestInitialize]
    public void SetUp()
    {
        _repo = new FakeSubscriptionRepository();
        _sut = new SubscriptionService(_repo);
    }

    private static async Task<ValidationFailedException> Invalid(Func<Task> action) =>
        await Assert.ThrowsExceptionAsync<ValidationFailedException>(action);

    // ---------- FR-04 создание, FR-08 валидация ----------

    [TestMethod]
    public async Task Create_Valid_NormalizesValues_AndStoresActiveSubscriptionOfCaller()
    {
        var dto = await _sut.CreateAsync(Owner, new CreateSubscriptionRequest("  Netflix ", 9.99m, "usd", "monthly", Start), default);

        Assert.AreEqual("Netflix", dto.Name);
        Assert.AreEqual("USD", dto.Currency);
        Assert.AreEqual("Monthly", dto.Period);
        Assert.AreEqual("Active", dto.Status);
        Assert.AreEqual(9.99m, dto.Amount);
        var stored = _repo.Items.Single();
        Assert.AreEqual(Owner, stored.UserId);
        Assert.AreEqual(Start, stored.StartDate);
    }

    [TestMethod]
    [DataRow("Weekly")]
    [DataRow("QUARTERLY")]
    [DataRow("yearly")]
    public async Task Create_AllBillingPeriods_AreAcceptedCaseInsensitively(string period)
    {
        var dto = await _sut.CreateAsync(Owner, new CreateSubscriptionRequest("X", 0m, "EUR", period, Start), default);

        Assert.AreEqual(period, dto.Period, ignoreCase: true); // сумма 0 допустима: «не отрицательная»
    }

    [TestMethod]
    [DataRow("", 1.0, "USD", "Monthly", "name")]
    [DataRow("Ok", -0.01, "USD", "Monthly", "amount")]
    [DataRow("Ok", 2000000000.0, "USD", "Monthly", "amount")]
    [DataRow("Ok", 1.0, "US", "Monthly", "currency")]
    [DataRow("Ok", 1.0, "U$D", "Monthly", "currency")]
    [DataRow("Ok", 1.0, "USD", "Daily", "period")]
    [DataRow("Ok", 1.0, "USD", "1", "period")]
    public async Task Create_InvalidValues_Throw422_AndStoreNothing(string name, double amount, string currency, string period, string field)
    {
        var ex = await Invalid(() => _sut.CreateAsync(Owner,
            new CreateSubscriptionRequest(name, (decimal)amount, currency, period, Start), default));

        Assert.IsTrue(ex.Errors.ContainsKey(field));
        Assert.AreEqual(0, _repo.Items.Count);
    }

    [TestMethod]
    public async Task Create_TooLongName_Throws422()
    {
        var ex = await Invalid(() => _sut.CreateAsync(Owner,
            new CreateSubscriptionRequest(new string('x', 101), 1m, "USD", "Monthly", Start), default));

        Assert.IsTrue(ex.Errors.ContainsKey("name"));
    }

    [TestMethod]
    public async Task Create_EmptyRequest_ReportsEveryRequiredField()
    {
        var ex = await Invalid(() => _sut.CreateAsync(Owner, new CreateSubscriptionRequest(null, null, null, null, null), default));

        CollectionAssert.AreEquivalent(new[] { "name", "amount", "currency", "period", "startDate" }, ex.Errors.Keys.ToArray());
    }

    // ---------- список, пагинация, фильтр ----------

    private void SeedList()
    {
        _repo.Items.Add(Seed.Sub(Owner, "B-active", status: SubscriptionStatus.Active, startDay: 2));
        _repo.Items.Add(Seed.Sub(Owner, "A-paused", status: SubscriptionStatus.Paused, startDay: 1));
        _repo.Items.Add(Seed.Sub(Owner, "C-deleted", status: SubscriptionStatus.Deleted, startDay: 3));
        _repo.Items.Add(Seed.Sub(Other, "foreign"));
    }

    [TestMethod]
    public async Task List_Defaults_ReturnsOnlyOwnNotDeleted_WithDefaultPaging()
    {
        SeedList();

        var page = await _sut.ListAsync(Owner, null, null, null, default);

        CollectionAssert.AreEqual(new[] { "A-paused", "B-active" }, page.Items.Select(i => i.Name).ToArray());
        Assert.AreEqual(2, page.Total);
        Assert.AreEqual(1, page.Page);
        Assert.AreEqual(20, page.PageSize);
    }

    [TestMethod]
    public async Task List_StatusFilter_IsCaseInsensitive()
    {
        SeedList();

        var page = await _sut.ListAsync(Owner, "paused", null, null, default);

        Assert.AreEqual("A-paused", page.Items.Single().Name);
        Assert.AreEqual(1, page.Total);
    }

    [TestMethod]
    public async Task List_Paging_ReturnsRequestedPageAndFullTotal()
    {
        SeedList();

        var page = await _sut.ListAsync(Owner, null, 2, 1, default);

        Assert.AreEqual("B-active", page.Items.Single().Name);
        Assert.AreEqual(2, page.Total);
    }

    [TestMethod]
    [DataRow(0, 10, null, "page")]
    [DataRow(1, 0, null, "pageSize")]
    [DataRow(1, 101, null, "pageSize")]
    [DataRow(1, 10, "Deleted", "status")]
    [DataRow(1, 10, "bogus", "status")]
    public async Task List_InvalidQuery_Throws422(int page, int pageSize, string? status, string field)
    {
        var ex = await Invalid(() => _sut.ListAsync(Owner, status, page, pageSize, default));

        Assert.IsTrue(ex.Errors.ContainsKey(field));
    }

    // ---------- получение и FR-07 ----------

    [TestMethod]
    public async Task Get_OwnSubscription_ReturnsDto()
    {
        var sub = Seed.Sub(Owner);
        _repo.Items.Add(sub);

        var dto = await _sut.GetAsync(Owner, sub.Id, default);

        Assert.AreEqual(sub.Id, dto.Id);
        Assert.AreEqual("Netflix", dto.Name);
    }

    [TestMethod]
    public async Task Get_AnotherUsersSubscription_ThrowsForbidden()
    {
        var sub = Seed.Sub(Other);
        _repo.Items.Add(sub);

        await Assert.ThrowsExceptionAsync<ForbiddenException>(() => _sut.GetAsync(Owner, sub.Id, default));
    }

    [TestMethod]
    public async Task Get_UnknownOrDeleted_ThrowsNotFound()
    {
        var deleted = Seed.Sub(Owner, status: SubscriptionStatus.Deleted);
        _repo.Items.Add(deleted);

        await Assert.ThrowsExceptionAsync<NotFoundException>(() => _sut.GetAsync(Owner, Guid.NewGuid(), default));
        await Assert.ThrowsExceptionAsync<NotFoundException>(() => _sut.GetAsync(Owner, deleted.Id, default));
    }

    // ---------- изменение ----------

    [TestMethod]
    public async Task Update_OnlyProvidedFieldsChange()
    {
        var sub = Seed.Sub(Owner);
        _repo.Items.Add(sub);

        var dto = await _sut.UpdateAsync(Owner, sub.Id, new PatchSubscriptionRequest(null, 12.5m, null, null, null, null), default);

        Assert.AreEqual(12.5m, dto.Amount);
        Assert.AreEqual("Netflix", dto.Name);
        Assert.AreEqual("USD", dto.Currency);
        Assert.AreEqual(1, _repo.UpdateCalls);
    }

    [TestMethod]
    public async Task Update_AllFields_AreApplied()
    {
        var sub = Seed.Sub(Owner);
        _repo.Items.Add(sub);
        var newStart = new DateOnly(2026, 3, 3);

        var dto = await _sut.UpdateAsync(Owner, sub.Id,
            new PatchSubscriptionRequest(" Spotify ", 5m, "eur", "Yearly", newStart, "paused"), default);

        Assert.AreEqual("Spotify", dto.Name);
        Assert.AreEqual(5m, dto.Amount);
        Assert.AreEqual("EUR", dto.Currency);
        Assert.AreEqual("Yearly", dto.Period);
        Assert.AreEqual(newStart, dto.StartDate);
        Assert.AreEqual("Paused", dto.Status);
    }

    [TestMethod]
    public async Task Update_NoFields_Throws422()
    {
        var sub = Seed.Sub(Owner);
        _repo.Items.Add(sub);

        var ex = await Invalid(() => _sut.UpdateAsync(Owner, sub.Id, new PatchSubscriptionRequest(null, null, null, null, null, null), default));

        Assert.IsTrue(ex.Errors.ContainsKey("body"));
        Assert.AreEqual(0, _repo.UpdateCalls);
    }

    [TestMethod]
    [DataRow("Deleted")] // удалять можно только через DELETE
    [DataRow("Unknown")]
    public async Task Update_InvalidStatus_Throws422_AndDoesNotSave(string status)
    {
        var sub = Seed.Sub(Owner);
        _repo.Items.Add(sub);

        var ex = await Invalid(() => _sut.UpdateAsync(Owner, sub.Id, new PatchSubscriptionRequest(null, null, null, null, null, status), default));

        Assert.IsTrue(ex.Errors.ContainsKey("status"));
        Assert.AreEqual(SubscriptionStatus.Active, sub.Status);
        Assert.AreEqual(0, _repo.UpdateCalls);
    }

    [TestMethod]
    public async Task Update_AnotherUsersSubscription_ThrowsForbidden_AndChangesNothing()
    {
        var sub = Seed.Sub(Other);
        _repo.Items.Add(sub);

        await Assert.ThrowsExceptionAsync<ForbiddenException>(() =>
            _sut.UpdateAsync(Owner, sub.Id, new PatchSubscriptionRequest("Hacked", null, null, null, null, null), default));
        Assert.AreEqual("Netflix", sub.Name);
    }

    // ---------- удаление (мягкое) ----------

    [TestMethod]
    public async Task Delete_MarksAsDeleted_AndKeepsRecord()
    {
        var sub = Seed.Sub(Owner);
        _repo.Items.Add(sub);

        var result = await _sut.DeleteAsync(Owner, sub.Id, default);

        Assert.AreEqual("Deleted", result.Status);
        Assert.AreEqual(SubscriptionStatus.Deleted, sub.Status);
        Assert.AreEqual(1, _repo.Items.Count, "запись остаётся — платежи ссылаются на неё");
    }

    [TestMethod]
    public async Task Delete_Twice_SecondTimeNotFound()
    {
        var sub = Seed.Sub(Owner);
        _repo.Items.Add(sub);
        await _sut.DeleteAsync(Owner, sub.Id, default);

        await Assert.ThrowsExceptionAsync<NotFoundException>(() => _sut.DeleteAsync(Owner, sub.Id, default));
    }

    [TestMethod]
    public async Task Delete_AnotherUsersSubscription_ThrowsForbidden()
    {
        var sub = Seed.Sub(Other);
        _repo.Items.Add(sub);

        await Assert.ThrowsExceptionAsync<ForbiddenException>(() => _sut.DeleteAsync(Owner, sub.Id, default));
        Assert.AreEqual(SubscriptionStatus.Active, sub.Status);
    }

    // ---------- контракт для других модулей ----------

    [TestMethod]
    public async Task Contract_GetOwned_RespectsIncludeDeletedFlag()
    {
        var deleted = Seed.Sub(Owner, status: SubscriptionStatus.Deleted);
        _repo.Items.Add(deleted);
        ISubscriptionsApi api = _sut;

        var info = await api.GetOwnedAsync(Owner, deleted.Id, includeDeleted: true, default);

        Assert.AreEqual("Deleted", info.Status);
        await Assert.ThrowsExceptionAsync<NotFoundException>(() => api.GetOwnedAsync(Owner, deleted.Id, false, default));
    }

    [TestMethod]
    public async Task Contract_ListAllOfUser_IncludesDeleted_ButNotForeign()
    {
        SeedList();
        ISubscriptionsApi api = _sut;

        var all = await api.ListAllOfUserAsync(Owner, default);

        Assert.AreEqual(3, all.Count);
        Assert.IsTrue(all.All(s => s.UserId == Owner));
    }
}
