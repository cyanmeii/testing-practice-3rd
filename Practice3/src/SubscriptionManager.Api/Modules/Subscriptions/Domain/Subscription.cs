namespace SubscriptionManager.Modules.Subscriptions.Domain;

public enum BillingPeriod { Weekly, Monthly, Quarterly, Yearly }

/// <summary>Deleted — мягкое удаление: подписка скрыта, платежи сохраняются для аналитики (раздел 2.5 ТЗ).</summary>
public enum SubscriptionStatus { Active, Paused, Cancelled, Deleted }

public sealed class Subscription
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string Name { get; set; } = "";
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "";
    public BillingPeriod Period { get; set; }
    public DateOnly StartDate { get; set; }
    public SubscriptionStatus Status { get; set; }
}
