using SubscriptionManager.Modules.Payments.Contracts;
using SubscriptionManager.Modules.Payments.Domain;
using SubscriptionManager.Modules.Subscriptions.Contracts;
using SubscriptionManager.Shared;

namespace SubscriptionManager.Modules.Payments.Application;

// ---------- DTO ----------
public sealed record CreatePaymentRequest(decimal? Amount, string? Currency, DateOnly? PaymentDate, string? Status, string? ExternalId);
public sealed record PaymentDto(Guid Id, Guid SubscriptionId, decimal Amount, string Currency, DateOnly PaymentDate, string Status, string ExternalId);
/// <param name="Created">true — платёж создан (201); false — повторная обработка того же ExternalId (200, дубликат не создан).</param>
public sealed record RecordPaymentResult(PaymentDto Payment, bool Created);

// ---------- порт ----------
public interface IPaymentRepository
{
    Task<Payment?> FindByExternalIdAsync(string externalId, CancellationToken ct);
    /// <returns>false — ExternalId уже существует (уникальный индекс).</returns>
    Task<bool> TryAddAsync(Payment payment, CancellationToken ct);
    Task<IReadOnlyList<Payment>> ListAsync(IReadOnlyCollection<Guid> subscriptionIds, DateOnly? from, DateOnly? to, PaymentStatus? status, CancellationToken ct);
}

// ---------- бизнес-логика (FR-05, FR-07, NFR-03) ----------
public sealed class PaymentService(IPaymentRepository repo, ISubscriptionsApi subscriptions) : IPaymentsApi
{
    private const decimal MaxAmount = 1_000_000_000m;

    public async Task<IReadOnlyList<PaymentDto>> ListAsync(Guid userId, Guid? subscriptionId, DateOnly? dateFrom, DateOnly? dateTo, CancellationToken ct)
    {
        if (dateFrom > dateTo)
        {
            var errors = new ValidationErrors();
            errors.Add("dateFrom", "dateFrom must not be later than dateTo.");
            errors.ThrowIfAny();
        }

        Guid[] ids;
        if (subscriptionId is { } sid)
        {
            await subscriptions.GetOwnedAsync(userId, sid, includeDeleted: true, ct); // история удалённой подписки доступна
            ids = new[] { sid };
        }
        else
        {
            ids = (await subscriptions.ListAllOfUserAsync(userId, ct)).Select(s => s.Id).ToArray();
            if (ids.Length == 0) return Array.Empty<PaymentDto>();
        }

        var payments = await repo.ListAsync(ids, dateFrom, dateTo, null, ct);
        return payments.Select(ToDto).ToList();
    }

    public async Task<RecordPaymentResult> RecordAsync(Guid userId, Guid subscriptionId, CreatePaymentRequest request, CancellationToken ct)
    {
        var errors = new ValidationErrors();
        if (request.Amount is null) errors.Add("amount", "Amount is required.");
        else if (request.Amount < 0 || request.Amount > MaxAmount) errors.Add("amount", "Amount must be between 0 and 1000000000.");

        var currency = request.Currency?.Trim().ToUpperInvariant();
        if (currency is null || currency.Length != 3 || !currency.All(c => c is >= 'A' and <= 'Z'))
            errors.Add("currency", "Currency must be a 3-letter ISO code, e.g. USD.");

        if (request.PaymentDate is null) errors.Add("paymentDate", "PaymentDate is required.");

        var status = ParseStatus(request.Status);
        if (status is null) errors.Add("status", "Status must be one of: Pending, Succeeded, Failed.");

        var externalId = request.ExternalId?.Trim();
        if (string.IsNullOrEmpty(externalId) || externalId.Length > 100)
            errors.Add("externalId", "ExternalId is required (1-100 characters).");
        errors.ThrowIfAny();

        // платёж можно записать только в свою, не удалённую подписку (FR-07)
        await subscriptions.GetOwnedAsync(userId, subscriptionId, includeDeleted: false, ct);

        var existing = await repo.FindByExternalIdAsync(externalId!, ct);
        if (existing is not null) return Replay(existing, subscriptionId);

        var payment = new Payment
        {
            Id = Guid.NewGuid(),
            SubscriptionId = subscriptionId,
            Amount = request.Amount!.Value,
            Currency = currency!,
            PaymentDate = request.PaymentDate!.Value,
            Status = status!.Value,
            ExternalId = externalId!
        };
        if (await repo.TryAddAsync(payment, ct))
            return new RecordPaymentResult(ToDto(payment), true);

        // гонка: параллельный запрос с тем же ExternalId успел записать платёж раньше
        existing = await repo.FindByExternalIdAsync(externalId!, ct)
                   ?? throw new ConflictException("Payment could not be stored due to a concurrent update.");
        return Replay(existing, subscriptionId);
    }

    // ---------- IPaymentsApi ----------
    public async Task<IReadOnlyList<PaymentInfo>> ListSucceededAsync(
        IReadOnlyCollection<Guid> subscriptionIds, DateOnly from, DateOnly to, CancellationToken ct) =>
        (await repo.ListAsync(subscriptionIds, from, to, PaymentStatus.Succeeded, ct))
            .Select(p => new PaymentInfo(p.SubscriptionId, p.Amount, p.Currency, p.PaymentDate)).ToList();

    // ---------- helpers ----------
    /// <summary>null статус в запросе = Succeeded; неизвестное значение (и числа) -> null.</summary>
    private static PaymentStatus? ParseStatus(string? value)
    {
        if (value is null) return PaymentStatus.Succeeded;
        foreach (var s in Enum.GetValues<PaymentStatus>())
            if (s.ToString().Equals(value.Trim(), StringComparison.OrdinalIgnoreCase)) return s;
        return null;
    }

    private static RecordPaymentResult Replay(Payment existing, Guid subscriptionId)
    {
        if (existing.SubscriptionId != subscriptionId)
            throw new ConflictException("ExternalId is already used by a payment of another subscription.");
        return new RecordPaymentResult(ToDto(existing), false);
    }

    private static PaymentDto ToDto(Payment p) =>
        new(p.Id, p.SubscriptionId, p.Amount, p.Currency, p.PaymentDate, p.Status.ToString(), p.ExternalId);
}
