namespace SubscriptionManager.Modules.Payments.Domain;

public enum PaymentStatus { Pending, Succeeded, Failed }

public sealed class Payment
{
    public Guid Id { get; set; }
    public Guid SubscriptionId { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "";
    public DateOnly PaymentDate { get; set; }
    public PaymentStatus Status { get; set; }
    /// <summary>Идентификатор платежа у внешнего провайдера; уникален -> идемпотентность (NFR-03).</summary>
    public string ExternalId { get; set; } = "";
}
