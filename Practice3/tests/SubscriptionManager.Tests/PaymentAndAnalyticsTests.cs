using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using SubscriptionManager.Modules.Analytics.Application;
using SubscriptionManager.Modules.Payments.Application;
using SubscriptionManager.Modules.Payments.Contracts;
using SubscriptionManager.Modules.Payments.Domain;
using SubscriptionManager.Modules.Subscriptions.Application;
using SubscriptionManager.Modules.Subscriptions.Contracts;
using SubscriptionManager.Modules.Subscriptions.Domain;
using SubscriptionManager.Shared;
using SubscriptionManager.Tests.Support;

namespace SubscriptionManager.Tests;

[TestClass]
public class PaymentServiceTests
{
    private static readonly Guid Owner = Guid.NewGuid();
    private static readonly Guid Other = Guid.NewGuid();
    private static readonly DateOnly Feb15 = new(2026, 2, 15);

    private FakeSubscriptionRepository _subs = null!;
    private FakePaymentRepository _payments = null!;
    private PaymentService _sut = null!;
    private Subscription _netflix = null!;

    [TestInitialize]
    public void SetUp()
    {
        _subs = new FakeSubscriptionRepository();
        _payments = new FakePaymentRepository();
        _sut = new PaymentService(_payments, new SubscriptionService(_subs));
        _netflix = Seed.Sub(Owner);
        _subs.Items.Add(_netflix);
    }

    private static CreatePaymentRequest Req(string externalId = "ext-1", decimal amount = 10m, string? status = null) =>
        new(amount, "usd", Feb15, status, externalId);

    private static async Task<ValidationFailedException> Invalid(Func<Task> action) =>
        await Assert.ThrowsExceptionAsync<ValidationFailedException>(action);

    // ---------- запись платежа ----------

    [TestMethod]
    public async Task Record_Valid_CreatesSucceededPaymentByDefault()
    {
        var result = await _sut.RecordAsync(Owner, _netflix.Id, Req(), default);

        Assert.IsTrue(result.Created);
        Assert.AreEqual("Succeeded", result.Payment.Status);
        Assert.AreEqual("USD", result.Payment.Currency);
        Assert.AreEqual(_netflix.Id, result.Payment.SubscriptionId);
        Assert.AreEqual(1, _payments.Items.Count);
    }

    [TestMethod]
    [DataRow("pending", "Pending")]
    [DataRow("FAILED", "Failed")]
    public async Task Record_ExplicitStatus_IsParsedCaseInsensitively(string status, string expected)
    {
        var result = await _sut.RecordAsync(Owner, _netflix.Id, Req(status: status), default);

        Assert.AreEqual(expected, result.Payment.Status);
    }

    [TestMethod]
    public async Task Record_SameExternalIdTwice_DoesNotCreateDuplicate()
    {
        var first = await _sut.RecordAsync(Owner, _netflix.Id, Req(), default);
        var replay = await _sut.RecordAsync(Owner, _netflix.Id, Req(amount: 999m), default);

        Assert.IsFalse(replay.Created);
        Assert.AreEqual(first.Payment.Id, replay.Payment.Id);
        Assert.AreEqual(10m, replay.Payment.Amount, "повтор не перезаписывает сохранённый платёж");
        Assert.AreEqual(1, _payments.Items.Count);
    }

    [TestMethod]
    public async Task Record_ExternalIdUsedByAnotherSubscription_ThrowsConflict()
    {
        var spotify = Seed.Sub(Owner, "Spotify");
        _subs.Items.Add(spotify);
        await _sut.RecordAsync(Owner, _netflix.Id, Req(), default);

        await Assert.ThrowsExceptionAsync<ConflictException>(() => _sut.RecordAsync(Owner, spotify.Id, Req(), default));
    }

    [TestMethod]
    public async Task Record_ConcurrentDuplicate_ReturnsWinnerInsteadOfCreatingSecondPayment()
    {
        var winner = Seed.Pay(_netflix.Id, "ext-1");
        _payments.RaceWinner = winner;

        var result = await _sut.RecordAsync(Owner, _netflix.Id, Req(), default);

        Assert.IsFalse(result.Created);
        Assert.AreEqual(winner.Id, result.Payment.Id);
        Assert.AreEqual(1, _payments.Items.Count);
    }

    [TestMethod]
    public async Task Record_StorageRejectsButNothingFound_ThrowsConflict()
    {
        _payments.FailAddWithoutWinner = true;

        await Assert.ThrowsExceptionAsync<ConflictException>(() => _sut.RecordAsync(Owner, _netflix.Id, Req(), default));
    }

    [TestMethod]
    public async Task Record_EmptyRequest_ReportsAllRequiredFields()
    {
        var ex = await Invalid(() => _sut.RecordAsync(Owner, _netflix.Id, new CreatePaymentRequest(null, null, null, null, null), default));

        CollectionAssert.AreEquivalent(new[] { "amount", "currency", "paymentDate", "externalId" }, ex.Errors.Keys.ToArray());
    }

    [TestMethod]
    [DataRow(-1.0, "USD", "Succeeded", "ext", "amount")]
    [DataRow(5000000000.0, "USD", "Succeeded", "ext", "amount")]
    [DataRow(1.0, "US", "Succeeded", "ext", "currency")]
    [DataRow(1.0, "U5D", "Succeeded", "ext", "currency")]
    [DataRow(1.0, "USD", "Done", "ext", "status")]
    [DataRow(1.0, "USD", "1", "ext", "status")]
    [DataRow(1.0, "USD", "Succeeded", "  ", "externalId")]
    public async Task Record_InvalidValues_Throw422_AndStoreNothing(double amount, string currency, string status, string externalId, string field)
    {
        var request = new CreatePaymentRequest((decimal)amount, currency, Feb15, status, externalId);

        var ex = await Invalid(() => _sut.RecordAsync(Owner, _netflix.Id, request, default));

        Assert.IsTrue(ex.Errors.ContainsKey(field));
        Assert.AreEqual(0, _payments.Items.Count);
    }

    [TestMethod]
    public async Task Record_TooLongExternalId_Throws422()
    {
        var ex = await Invalid(() => _sut.RecordAsync(Owner, _netflix.Id, Req(externalId: new string('e', 101)), default));

        Assert.IsTrue(ex.Errors.ContainsKey("externalId"));
    }

    [TestMethod]
    public async Task Record_ForAnotherUsersSubscription_ThrowsForbidden()
    {
        await Assert.ThrowsExceptionAsync<ForbiddenException>(() => _sut.RecordAsync(Other, _netflix.Id, Req(), default));
        Assert.AreEqual(0, _payments.Items.Count);
    }

    [TestMethod]
    public async Task Record_ForDeletedOrUnknownSubscription_ThrowsNotFound()
    {
        var deleted = Seed.Sub(Owner, "Old", status: SubscriptionStatus.Deleted);
        _subs.Items.Add(deleted);

        await Assert.ThrowsExceptionAsync<NotFoundException>(() => _sut.RecordAsync(Owner, deleted.Id, Req(), default));
        await Assert.ThrowsExceptionAsync<NotFoundException>(() => _sut.RecordAsync(Owner, Guid.NewGuid(), Req(), default));
    }

    // ---------- история платежей ----------

    private void SeedHistory()
    {
        _payments.Items.Add(Seed.Pay(_netflix.Id, "a", date: "2026-01-10"));
        _payments.Items.Add(Seed.Pay(_netflix.Id, "b", date: "2026-02-10"));
        _payments.Items.Add(Seed.Pay(_netflix.Id, "c", date: "2026-03-10"));
        var foreign = Seed.Sub(Other);
        _subs.Items.Add(foreign);
        _payments.Items.Add(Seed.Pay(foreign.Id, "foreign"));
    }

    [TestMethod]
    public async Task List_WithoutFilters_ReturnsOnlyOwnPayments_NewestFirst()
    {
        SeedHistory();

        var list = await _sut.ListAsync(Owner, null, null, null, default);

        CollectionAssert.AreEqual(new[] { "c", "b", "a" }, list.Select(p => p.ExternalId).ToArray());
    }

    [TestMethod]
    public async Task List_DateRange_IsInclusiveOnBothEnds()
    {
        SeedHistory();

        var list = await _sut.ListAsync(Owner, null, new DateOnly(2026, 1, 10), new DateOnly(2026, 2, 10), default);

        CollectionAssert.AreEquivalent(new[] { "a", "b" }, list.Select(p => p.ExternalId).ToArray());
    }

    [TestMethod]
    public async Task List_BySubscription_ReturnsItsPayments()
    {
        SeedHistory();

        var list = await _sut.ListAsync(Owner, _netflix.Id, null, null, default);

        Assert.AreEqual(3, list.Count);
    }

    [TestMethod]
    public async Task List_UserWithoutSubscriptions_ReturnsEmpty()
    {
        SeedHistory();

        var list = await _sut.ListAsync(Guid.NewGuid(), null, null, null, default);

        Assert.AreEqual(0, list.Count);
    }

    [TestMethod]
    public async Task List_ForeignSubscriptionFilter_ThrowsForbidden()
    {
        SeedHistory();
        var foreignId = _subs.Items.Single(s => s.UserId == Other).Id;

        await Assert.ThrowsExceptionAsync<ForbiddenException>(() => _sut.ListAsync(Owner, foreignId, null, null, default));
    }

    [TestMethod]
    public async Task List_ReversedRange_Throws422()
    {
        var ex = await Invalid(() => _sut.ListAsync(Owner, null, new DateOnly(2026, 3, 1), new DateOnly(2026, 1, 1), default));

        Assert.IsTrue(ex.Errors.ContainsKey("dateFrom"));
    }

    [TestMethod]
    public async Task List_PaymentsOfDeletedSubscription_AreStillAvailable()
    {
        SeedHistory();
        _netflix.Status = SubscriptionStatus.Deleted; // «Удаление подписки не удаляет историю платежей»

        var all = await _sut.ListAsync(Owner, null, null, null, default);
        var bySub = await _sut.ListAsync(Owner, _netflix.Id, null, null, default);

        Assert.AreEqual(3, all.Count);
        Assert.AreEqual(3, bySub.Count);
    }

    [TestMethod]
    public async Task Contract_ListSucceeded_FiltersOutNonSucceeded()
    {
        _payments.Items.Add(Seed.Pay(_netflix.Id, "ok"));
        _payments.Items.Add(Seed.Pay(_netflix.Id, "pending", status: PaymentStatus.Pending));
        _payments.Items.Add(Seed.Pay(_netflix.Id, "failed", status: PaymentStatus.Failed));
        IPaymentsApi api = _sut;

        var list = await api.ListSucceededAsync(new[] { _netflix.Id }, new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), default);

        Assert.AreEqual(1, list.Count);
    }
}

[TestClass]
public class AnalyticsServiceTests
{
    private static readonly Guid Owner = Guid.NewGuid();
    private static readonly Guid Other = Guid.NewGuid();
    private static readonly DateOnly From = new(2026, 1, 1);
    private static readonly DateOnly To = new(2026, 3, 31);

    private FakeSubscriptionRepository _subs = null!;
    private FakePaymentRepository _payments = null!;
    private AnalyticsService _sut = null!;

    [TestInitialize]
    public void SetUp()
    {
        _subs = new FakeSubscriptionRepository();
        _payments = new FakePaymentRepository();
        _sut = new AnalyticsService(new SubscriptionService(_subs), new PaymentService(_payments, new SubscriptionService(_subs)));
    }

    [TestMethod]
    public async Task Expenses_MissingDates_Throws422ForBoth()
    {
        var ex = await Assert.ThrowsExceptionAsync<ValidationFailedException>(() => _sut.GetExpensesAsync(Owner, null, null, default));

        Assert.IsTrue(ex.Errors.ContainsKey("dateFrom"));
        Assert.IsTrue(ex.Errors.ContainsKey("dateTo"));
    }

    [TestMethod]
    public async Task Expenses_ReversedRange_Throws422()
    {
        await Assert.ThrowsExceptionAsync<ValidationFailedException>(() => _sut.GetExpensesAsync(Owner, To, From, default));
    }

    [TestMethod]
    public async Task Expenses_UserWithoutSubscriptions_ReturnsEmptyReport_WithoutQueryingPayments()
    {
        var subscriptions = new Mock<ISubscriptionsApi>();
        subscriptions.Setup(s => s.ListAllOfUserAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                     .ReturnsAsync(Array.Empty<SubscriptionInfo>());
        var payments = new Mock<IPaymentsApi>();
        var sut = new AnalyticsService(subscriptions.Object, payments.Object);

        var report = await sut.GetExpensesAsync(Owner, From, To, default);

        Assert.AreEqual(0, report.PaymentsCount);
        Assert.AreEqual(0, report.Totals.Count);
        Assert.AreEqual(0, report.BySubscription.Count);
        payments.Verify(p => p.ListSucceededAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<DateOnly>(),
            It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()), Times.Never());
    }

    [TestMethod]
    public async Task Expenses_AggregatesPerCurrencyAndSubscription_OnlySucceeded_InRange_OnlyOwn()
    {
        var netflix = Seed.Sub(Owner, "Netflix", "USD");
        var spotify = Seed.Sub(Owner, "Spotify", "EUR");
        var old = Seed.Sub(Owner, "Old", "USD", SubscriptionStatus.Deleted); // удалена, но история учитывается
        var foreign = Seed.Sub(Other, "Foreign", "USD");
        _subs.Items.AddRange(new[] { netflix, spotify, old, foreign });

        _payments.Items.Add(Seed.Pay(netflix.Id, "n1", 10m, "USD", "2026-01-01"));                       // граница From — входит
        _payments.Items.Add(Seed.Pay(netflix.Id, "n2", 10m, "USD", "2026-03-31"));                       // граница To — входит
        _payments.Items.Add(Seed.Pay(netflix.Id, "n3", 5m, "USD", "2026-02-01", PaymentStatus.Failed));  // не Succeeded
        _payments.Items.Add(Seed.Pay(netflix.Id, "n4", 99m, "USD", "2026-04-01"));                       // вне периода
        _payments.Items.Add(Seed.Pay(spotify.Id, "s1", 7m, "EUR", "2026-02-02"));
        _payments.Items.Add(Seed.Pay(old.Id, "o1", 3m, "USD", "2026-02-03"));
        _payments.Items.Add(Seed.Pay(foreign.Id, "f1", 1000m, "USD", "2026-02-04"));                     // чужой

        var report = await _sut.GetExpensesAsync(Owner, From, To, default);

        Assert.AreEqual(4, report.PaymentsCount);
        CollectionAssert.AreEqual(new[] { "EUR", "USD" }, report.Totals.Select(t => t.Currency).ToArray());
        Assert.AreEqual(7m, report.Totals.Single(t => t.Currency == "EUR").Total);
        Assert.AreEqual(23m, report.Totals.Single(t => t.Currency == "USD").Total);
        CollectionAssert.AreEqual(new[] { "Netflix", "Spotify", "Old" }, report.BySubscription.Select(s => s.Name).ToArray());
        Assert.AreEqual(2, report.BySubscription[0].PaymentsCount);
        Assert.AreEqual(20m, report.BySubscription[0].Total);
        Assert.AreEqual(From, report.DateFrom);
        Assert.AreEqual(To, report.DateTo);
    }

    [TestMethod]
    public async Task Expenses_NoPaymentsInRange_ReturnsZeroTotals()
    {
        _subs.Items.Add(Seed.Sub(Owner));

        var report = await _sut.GetExpensesAsync(Owner, From, To, default);

        Assert.AreEqual(0, report.PaymentsCount);
        Assert.AreEqual(0, report.Totals.Count);
    }
}
