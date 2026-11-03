using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Shop.Orders;

// ---------- Models ----------
public enum OrderStatus { Confirmed, PendingReview, Failed }
public enum FailureReason { None, Validation, OutOfStock, PaymentDeclined, PaymentUnavailable }
public enum CustomerTier { Standard, Gold, Platinum }

public record Customer(Guid Id, string Email, CustomerTier Tier, string CountryCode);
public record OrderLine(string Sku, int Quantity, decimal UnitPrice);
public record Discount(string Code, decimal Percent, bool Stackable);
public record PlaceOrderRequest(string IdempotencyKey, Customer Customer, IReadOnlyList<OrderLine> Lines, string PaymentToken);
public record PaymentRequest(Guid CustomerId, decimal Amount, string Token, string IdempotencyKey);
public record PaymentResult(bool Success, bool IsTransientFailure, string? TransactionId, string? Error);

public class Order
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string IdempotencyKey { get; init; } = "";
    public Guid CustomerId { get; init; }
    public OrderStatus Status { get; set; }
    public decimal Subtotal { get; set; }
    public decimal DiscountTotal { get; set; }
    public decimal Tax { get; set; }
    public decimal Total { get; set; }
    public string? TransactionId { get; set; }
    public DateTimeOffset CreatedAt { get; init; }
}

public record OrderResult(Order? Order, FailureReason Failure, string? Message)
{
    public bool IsSuccess => Failure == FailureReason.None;
}

// ---------- Dependencies ----------
public interface IOrderRepository
{
    Task<Order?> GetByIdempotencyKeyAsync(string key, CancellationToken ct);
    Task SaveAsync(Order order, CancellationToken ct);
}

public interface IInventoryService
{
    Task<bool> TryReserveAsync(string sku, int qty, CancellationToken ct);
    Task ReleaseAsync(string sku, int qty, CancellationToken ct);
}

public interface IPaymentGateway
{
    Task<PaymentResult> ChargeAsync(PaymentRequest request, CancellationToken ct);
    Task RefundAsync(string transactionId, CancellationToken ct);
}

public interface IDiscountEngine
{
    Task<IReadOnlyList<Discount>> GetApplicableAsync(
        Customer customer, IReadOnlyList<OrderLine> lines, DateTimeOffset now, CancellationToken ct);
}

public interface ITaxCalculator { decimal Calculate(string countryCode, decimal taxableAmount); }
public interface INotificationService { Task SendConfirmationAsync(Order order, string email, CancellationToken ct); }
public interface IUnitOfWork { Task<T> ExecuteAsync<T>(Func<Task<T>> work, CancellationToken ct); }
public interface IClock { DateTimeOffset UtcNow { get; } }
public interface IDelayer { Task DelayAsync(TimeSpan delay, CancellationToken ct); }

public class OrderOptions
{
    public int MaxLines { get; init; } = 50;
    public decimal ManualReviewThreshold { get; init; } = 10_000m;
    public decimal MaxDiscountPercent { get; init; } = 50m;
    public int MaxPaymentAttempts { get; init; } = 3;
    public decimal PlatinumExtraPercent { get; init; } = 5m;
}

// ---------- Service under test ----------
public class OrderProcessingService
{
    private readonly IOrderRepository _orders;
    private readonly IInventoryService _inventory;
    private readonly IPaymentGateway _payments;
    private readonly IDiscountEngine _discounts;
    private readonly ITaxCalculator _tax;
    private readonly INotificationService _notifications;
    private readonly IUnitOfWork _uow;
    private readonly IClock _clock;
    private readonly IDelayer _delayer;
    private readonly OrderOptions _options;
    private readonly ILogger<OrderProcessingService> _logger;

    public OrderProcessingService(
        IOrderRepository orders, IInventoryService inventory, IPaymentGateway payments,
        IDiscountEngine discounts, ITaxCalculator tax, INotificationService notifications,
        IUnitOfWork uow, IClock clock, IDelayer delayer,
        OrderOptions options, ILogger<OrderProcessingService> logger)
    {
        _orders = orders; _inventory = inventory; _payments = payments;
        _discounts = discounts; _tax = tax; _notifications = notifications;
        _uow = uow; _clock = clock; _delayer = delayer;
        _options = options; _logger = logger;
    }

    public async Task<OrderResult> PlaceOrderAsync(PlaceOrderRequest request, CancellationToken ct = default)
    {
        // 1. Validation
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey))
            return Fail(FailureReason.Validation, "Idempotency key is required.");
        if (request.Lines is null || request.Lines.Count == 0)
            return Fail(FailureReason.Validation, "Order must contain at least one line.");
        if (request.Lines.Count > _options.MaxLines)
            return Fail(FailureReason.Validation, $"Order cannot exceed {_options.MaxLines} lines.");
        if (request.Lines.Any(l => l.Quantity <= 0 || l.UnitPrice < 0))
            return Fail(FailureReason.Validation, "Invalid quantity or price.");

        // 2. Idempotency: return the existing order for a repeated request
        var existing = await _orders.GetByIdempotencyKeyAsync(request.IdempotencyKey, ct);
        if (existing is not null)
        {
            _logger.LogInformation("Duplicate request {Key}, returning order {Id}", request.IdempotencyKey, existing.Id);
            return new OrderResult(existing, FailureReason.None, "Duplicate request.");
        }

        var now = _clock.UtcNow;
        var subtotal = request.Lines.Sum(l => l.Quantity * l.UnitPrice);

        // 3. Discounts (capped, stackable rules, tier bonus, happy-hour bonus)
        var discountPercent = await CalculateDiscountPercentAsync(request, now, ct);
        var discountTotal = Math.Round(subtotal * discountPercent / 100m, 2, MidpointRounding.AwayFromZero);
        var taxable = subtotal - discountTotal;
        var tax = _tax.Calculate(request.Customer.CountryCode, taxable);
        var total = taxable + tax;

        var order = new Order
        {
            IdempotencyKey = request.IdempotencyKey,
            CustomerId = request.Customer.Id,
            Subtotal = subtotal,
            DiscountTotal = discountTotal,
            Tax = tax,
            Total = total,
            CreatedAt = now
        };

        // 4. High-value orders skip payment and go to manual review
        if (total >= _options.ManualReviewThreshold)
        {
            order.Status = OrderStatus.PendingReview;
            await _uow.ExecuteAsync(async () => { await _orders.SaveAsync(order, ct); return true; }, ct);
            return new OrderResult(order, FailureReason.None, "Order requires manual review.");
        }

        // 5. Reserve inventory; roll back partial reservations on failure
        var reserved = new List<OrderLine>();
        foreach (var line in request.Lines)
        {
            if (!await _inventory.TryReserveAsync(line.Sku, line.Quantity, ct))
            {
                await ReleaseAllAsync(reserved);
                return Fail(FailureReason.OutOfStock, $"SKU {line.Sku} is out of stock.");
            }
            reserved.Add(line);
        }

        // 6. Charge with retry on transient errors (linear backoff)
        PaymentResult? payment = null;
        for (var attempt = 1; attempt <= _options.MaxPaymentAttempts; attempt++)
        {
            payment = await _payments.ChargeAsync(
                new PaymentRequest(request.Customer.Id, total, request.PaymentToken, request.IdempotencyKey), ct);

            if (payment.Success || !payment.IsTransientFailure) break;

            if (attempt < _options.MaxPaymentAttempts)
                await _delayer.DelayAsync(TimeSpan.FromMilliseconds(200 * attempt), ct);
        }

        if (payment is null || !payment.Success)
        {
            await ReleaseAllAsync(reserved);
            return payment is { IsTransientFailure: true }
                ? Fail(FailureReason.PaymentUnavailable, "Payment provider unavailable.")
                : Fail(FailureReason.PaymentDeclined, payment?.Error ?? "Payment declined.");
        }

        order.TransactionId = payment.TransactionId;
        order.Status = OrderStatus.Confirmed;

        // 7. Persist; compensate (refund + release) if saving fails after charging
        try
        {
            await _uow.ExecuteAsync(async () => { await _orders.SaveAsync(order, ct); return true; }, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save order {Id}; compensating", order.Id);
            await _payments.RefundAsync(payment.TransactionId!, CancellationToken.None);
            await ReleaseAllAsync(reserved);
            throw;
        }

        // 8. Best-effort notification: must never fail the order
        try
        {
            await _notifications.SendConfirmationAsync(order, request.Customer.Email, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Confirmation email failed for order {Id}", order.Id);
        }

        return new OrderResult(order, FailureReason.None, null);
    }

    private async Task<decimal> CalculateDiscountPercentAsync(PlaceOrderRequest request, DateTimeOffset now, CancellationToken ct)
    {
        var discounts = await _discounts.GetApplicableAsync(request.Customer, request.Lines, now, ct);

        var nonStackable = discounts.Where(d => !d.Stackable).Select(d => d.Percent).DefaultIfEmpty(0).Max();
        var stackable = discounts.Where(d => d.Stackable).Sum(d => d.Percent);
        var percent = Math.Max(nonStackable, stackable);

        if (request.Customer.Tier == CustomerTier.Platinum)
            percent += _options.PlatinumExtraPercent;

        // Weekend happy hour: extra 2% between 17:00 and 19:59 UTC
        if (now.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday && now.Hour is >= 17 and < 20)
            percent += 2m;

        return Math.Min(percent, _options.MaxDiscountPercent);
    }

    private async Task ReleaseAllAsync(IEnumerable<OrderLine> lines)
    {
        foreach (var line in lines)
        {
            try { await _inventory.ReleaseAsync(line.Sku, line.Quantity, CancellationToken.None); }
            catch (Exception ex) { _logger.LogError(ex, "Failed to release {Sku}", line.Sku); }
        }
    }

    private static OrderResult Fail(FailureReason reason, string message) => new(null, reason, message);
}
