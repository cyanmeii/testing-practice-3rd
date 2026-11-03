using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Shop.Orders.Tests;

/// <summary>
/// Часть 1, задание 2: юнит-тесты блока 5 «Reserve inventory» метода OrderProcessingService.PlaceOrderAsync.
/// Блок 5: последовательно резервирует позиции; при первой неудаче освобождает уже зарезервированные
/// (ReleaseAllAsync, токен CancellationToken.None, ошибки освобождения логируются и не прерывают откат)
/// и возвращает Fail(OutOfStock, "SKU {sku} is out of stock.").
/// Остальные блоки изолированы заглушками: скидок нет, налог 0, заказ далеко ниже порога ручной проверки.
/// </summary>
[TestClass]
public class ReserveInventoryBlockTests
{
    private Mock<IOrderRepository> _orders = null!;
    private Mock<IInventoryService> _inventory = null!;
    private Mock<IPaymentGateway> _payments = null!;
    private Mock<IUnitOfWork> _uow = null!;
    private Mock<ILogger<OrderProcessingService>> _logger = null!;
    private OrderProcessingService _sut = null!;

    private readonly Customer _customer = new(Guid.NewGuid(), "jane@example.com", CustomerTier.Standard, "FI");
    private List<string> _calls = null!;           // хронология вызовов склада: "reserve:A x2", "release:A x2"
    private HashSet<string> _outOfStock = null!;   // SKU, для которых TryReserveAsync вернёт false
    private HashSet<string> _releaseFails = null!; // SKU, для которых ReleaseAsync бросит исключение

    [TestInitialize]
    public void SetUp()
    {
        _calls = new List<string>();
        _outOfStock = new HashSet<string>();
        _releaseFails = new HashSet<string>();

        _orders = new Mock<IOrderRepository>();
        _orders.Setup(r => r.GetByIdempotencyKeyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync((Order?)null);
        _orders.Setup(r => r.SaveAsync(It.IsAny<Order>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        _inventory = new Mock<IInventoryService>();
        _inventory.Setup(i => i.TryReserveAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                  .Returns<string, int, CancellationToken>((sku, qty, _) =>
                  {
                      _calls.Add($"reserve:{sku} x{qty}");
                      return Task.FromResult(!_outOfStock.Contains(sku));
                  });
        _inventory.Setup(i => i.ReleaseAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                  .Returns<string, int, CancellationToken>((sku, qty, _) =>
                  {
                      _calls.Add($"release:{sku} x{qty}");
                      if (_releaseFails.Contains(sku)) throw new InvalidOperationException("release failed");
                      return Task.CompletedTask;
                  });

        _payments = new Mock<IPaymentGateway>();
        _payments.Setup(p => p.ChargeAsync(It.IsAny<PaymentRequest>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new PaymentResult(true, false, "tx-1", null));

        var discounts = new Mock<IDiscountEngine>();
        discounts.Setup(d => d.GetApplicableAsync(It.IsAny<Customer>(), It.IsAny<IReadOnlyList<OrderLine>>(),
                                                  It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync((IReadOnlyList<Discount>)new List<Discount>());

        var tax = new Mock<ITaxCalculator>();
        tax.Setup(t => t.Calculate(It.IsAny<string>(), It.IsAny<decimal>())).Returns(0m);

        var notifications = new Mock<INotificationService>();
        notifications.Setup(n => n.SendConfirmationAsync(It.IsAny<Order>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                     .Returns(Task.CompletedTask);

        _uow = new Mock<IUnitOfWork>();
        _uow.Setup(u => u.ExecuteAsync(It.IsAny<Func<Task<bool>>>(), It.IsAny<CancellationToken>()))
            .Returns<Func<Task<bool>>, CancellationToken>((work, _) => work());

        var clock = new Mock<IClock>();
        clock.SetupGet(c => c.UtcNow).Returns(new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero)); // среда, не happy hour

        var delayer = new Mock<IDelayer>();
        delayer.Setup(d => d.DelayAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        _logger = new Mock<ILogger<OrderProcessingService>>();

        _sut = new OrderProcessingService(_orders.Object, _inventory.Object, _payments.Object, discounts.Object,
            tax.Object, notifications.Object, _uow.Object, clock.Object, delayer.Object, new OrderOptions(), _logger.Object);
    }

    private PlaceOrderRequest Request(params (string Sku, int Qty)[] lines) =>
        new("key-1", _customer, lines.Select(l => new OrderLine(l.Sku, l.Qty, 10m)).ToList(), "tok");

    private void VerifyLogged(LogLevel level, Times times) =>
        _logger.Verify(l => l.Log(level, It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(), It.IsAny<Exception?>(),
            (Func<It.IsAnyType, Exception?, string>)It.IsAny<object>()), times);

    // ---------- успешный путь блока ----------

    [TestMethod]
    public async Task AllLinesAvailable_ReservesEveryLineInOrderWithItsQuantity()
    {
        var result = await _sut.PlaceOrderAsync(Request(("A", 1), ("B", 2), ("C", 3)));

        Assert.IsTrue(result.IsSuccess);
        CollectionAssert.AreEqual(new[] { "reserve:A x1", "reserve:B x2", "reserve:C x3" }, _calls);
    }

    [TestMethod]
    public async Task AllLinesAvailable_NothingIsReleased_AndFlowContinuesToPayment()
    {
        var result = await _sut.PlaceOrderAsync(Request(("A", 1), ("B", 2)));

        Assert.AreEqual(OrderStatus.Confirmed, result.Order!.Status);
        _inventory.Verify(i => i.ReleaseAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never());
        _payments.Verify(p => p.ChargeAsync(It.IsAny<PaymentRequest>(), It.IsAny<CancellationToken>()), Times.Once());
    }

    [TestMethod]
    public async Task Reserve_ForwardsCallerCancellationToken()
    {
        using var cts = new CancellationTokenSource();

        await _sut.PlaceOrderAsync(Request(("A", 1)), cts.Token);

        _inventory.Verify(i => i.TryReserveAsync("A", 1, cts.Token), Times.Once());
    }

    // ---------- нехватка товара ----------

    [TestMethod]
    public async Task FirstLineOutOfStock_ReturnsOutOfStockFailure_WithSkuInMessage()
    {
        _outOfStock.Add("A");

        var result = await _sut.PlaceOrderAsync(Request(("A", 1), ("B", 1)));

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(FailureReason.OutOfStock, result.Failure);
        Assert.IsNull(result.Order);
        Assert.AreEqual("SKU A is out of stock.", result.Message);
    }

    [TestMethod]
    public async Task FirstLineOutOfStock_NothingToRollBack_StopsBeforeNextLinePaymentAndSave()
    {
        _outOfStock.Add("A");

        await _sut.PlaceOrderAsync(Request(("A", 1), ("B", 1)));

        CollectionAssert.AreEqual(new[] { "reserve:A x1" }, _calls); // ни release, ни reserve:B
        _payments.Verify(p => p.ChargeAsync(It.IsAny<PaymentRequest>(), It.IsAny<CancellationToken>()), Times.Never());
        _uow.Verify(u => u.ExecuteAsync(It.IsAny<Func<Task<bool>>>(), It.IsAny<CancellationToken>()), Times.Never());
    }

    [TestMethod]
    public async Task MiddleLineOutOfStock_ReleasesOnlyPreviouslyReservedLines_InOrder_AndStops()
    {
        _outOfStock.Add("C");

        var result = await _sut.PlaceOrderAsync(Request(("A", 1), ("B", 2), ("C", 3), ("D", 4)));

        Assert.AreEqual(FailureReason.OutOfStock, result.Failure);
        Assert.AreEqual("SKU C is out of stock.", result.Message);
        CollectionAssert.AreEqual(
            new[] { "reserve:A x1", "reserve:B x2", "reserve:C x3", "release:A x1", "release:B x2" }, _calls);
    }

    [TestMethod]
    public async Task LastLineOutOfStock_ReleasesAllPreviousLines()
    {
        _outOfStock.Add("C");

        await _sut.PlaceOrderAsync(Request(("A", 1), ("B", 2), ("C", 3)));

        CollectionAssert.AreEqual(
            new[] { "reserve:A x1", "reserve:B x2", "reserve:C x3", "release:A x1", "release:B x2" }, _calls);
    }

    [TestMethod]
    public async Task Rollback_UsesCancellationTokenNone_EvenWhenCallerPassesOwnToken()
    {
        using var cts = new CancellationTokenSource();
        _outOfStock.Add("B");

        await _sut.PlaceOrderAsync(Request(("A", 1), ("B", 1)), cts.Token);

        _inventory.Verify(i => i.ReleaseAsync("A", 1, CancellationToken.None), Times.Once());
        _inventory.Verify(i => i.ReleaseAsync(It.IsAny<string>(), It.IsAny<int>(), cts.Token), Times.Never());
    }

    [TestMethod]
    public async Task SameSkuInTwoLines_SecondFails_ReleasesOnlyTheFirstLineQuantity()
    {
        // Первый резерв по A проходит, затем A «заканчивается».
        var reserveCount = 0;
        _inventory.Setup(i => i.TryReserveAsync("A", It.IsAny<int>(), It.IsAny<CancellationToken>()))
                  .Returns<string, int, CancellationToken>((sku, qty, _) =>
                  {
                      _calls.Add($"reserve:{sku} x{qty}");
                      return Task.FromResult(++reserveCount == 1);
                  });

        var result = await _sut.PlaceOrderAsync(Request(("A", 1), ("A", 5)));

        Assert.AreEqual(FailureReason.OutOfStock, result.Failure);
        CollectionAssert.AreEqual(new[] { "reserve:A x1", "reserve:A x5", "release:A x1" }, _calls);
    }

    // ---------- сбои при откате ----------

    [TestMethod]
    public async Task ReleaseThrowsDuringRollback_RemainingLinesStillReleased_ErrorLogged_ResultStillOutOfStock()
    {
        _outOfStock.Add("C");
        _releaseFails.Add("A");

        var result = await _sut.PlaceOrderAsync(Request(("A", 1), ("B", 2), ("C", 3)));

        Assert.AreEqual(FailureReason.OutOfStock, result.Failure);
        CollectionAssert.Contains(_calls, "release:A x1");
        CollectionAssert.Contains(_calls, "release:B x2");
        VerifyLogged(LogLevel.Error, Times.Once());
    }

    [TestMethod]
    public async Task TryReserveThrows_ExceptionPropagates_AndAlreadyReservedLinesAreNotReleased_KnownDefect()
    {
        // Блок 5 не оборачивает резервирование в try/catch: при исключении (в т.ч. отмена токена)
        // позиция A остаётся зарезервированной -> утечка резерва. Тест фиксирует текущее поведение.
        _inventory.Setup(i => i.TryReserveAsync("B", It.IsAny<int>(), It.IsAny<CancellationToken>()))
                  .Returns<string, int, CancellationToken>((_, _, _) => throw new OperationCanceledException());

        await Assert.ThrowsExceptionAsync<OperationCanceledException>(
            () => _sut.PlaceOrderAsync(Request(("A", 1), ("B", 1))));

        CollectionAssert.AreEqual(new[] { "reserve:A x1" }, _calls);
    }

    // ---------- граница с блоком 4 ----------

    [TestMethod]
    public async Task HighValueOrder_GoesToManualReview_WithoutReservingInventory()
    {
        var request = new PlaceOrderRequest("key-1", _customer, new List<OrderLine> { new("A", 1, 10_000m) }, "tok");

        var result = await _sut.PlaceOrderAsync(request);

        Assert.AreEqual(OrderStatus.PendingReview, result.Order!.Status);
        _inventory.Verify(i => i.TryReserveAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never());
    }
}
