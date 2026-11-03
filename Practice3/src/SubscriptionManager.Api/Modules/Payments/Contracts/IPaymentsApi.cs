namespace SubscriptionManager.Modules.Payments.Contracts;

public sealed record PaymentInfo(Guid SubscriptionId, decimal Amount, string Currency, DateOnly PaymentDate);

/// <summary>Публичный контракт модуля Payments (используется модулем Analytics).</summary>
public interface IPaymentsApi
{
    /// <summary>Успешные платежи по указанным подпискам за период [from; to] включительно.</summary>
    Task<IReadOnlyList<PaymentInfo>> ListSucceededAsync(
        IReadOnlyCollection<Guid> subscriptionIds, DateOnly from, DateOnly to, CancellationToken ct);
}
