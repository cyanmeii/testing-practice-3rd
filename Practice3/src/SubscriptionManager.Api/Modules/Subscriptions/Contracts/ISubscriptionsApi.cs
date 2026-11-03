namespace SubscriptionManager.Modules.Subscriptions.Contracts;

/// <summary>Публичный контракт модуля Subscriptions: единственное, что видят другие модули.</summary>
public sealed record SubscriptionInfo(Guid Id, Guid UserId, string Name, string Currency, string Status);

public interface ISubscriptionsApi
{
    /// <exception cref="Shared.NotFoundException">нет такой подписки (или она удалена и includeDeleted=false)</exception>
    /// <exception cref="Shared.ForbiddenException">подписка принадлежит другому пользователю</exception>
    Task<SubscriptionInfo> GetOwnedAsync(Guid userId, Guid subscriptionId, bool includeDeleted, CancellationToken ct);

    /// <summary>Все подписки пользователя, включая удалённые (нужно для истории платежей и аналитики).</summary>
    Task<IReadOnlyList<SubscriptionInfo>> ListAllOfUserAsync(Guid userId, CancellationToken ct);
}
