using System.Text.RegularExpressions;
using SubscriptionManager.Modules.Subscriptions.Contracts;
using SubscriptionManager.Modules.Subscriptions.Domain;
using SubscriptionManager.Shared;

namespace SubscriptionManager.Modules.Subscriptions.Application;

// ---------- DTO ----------
public sealed record CreateSubscriptionRequest(string? Name, decimal? Amount, string? Currency, string? Period, DateOnly? StartDate);
public sealed record PatchSubscriptionRequest(string? Name, decimal? Amount, string? Currency, string? Period, DateOnly? StartDate, string? Status);
public sealed record SubscriptionDto(Guid Id, string Name, decimal Amount, string Currency, string Period, DateOnly StartDate, string Status);
public sealed record DeleteResult(Guid Id, string Status);

// ---------- порт ----------
public interface ISubscriptionRepository
{
    Task<Subscription?> FindAsync(Guid id, CancellationToken ct);
    /// <summary>Подписки пользователя без удалённых, опционально по статусу; Total — до пагинации.</summary>
    Task<(IReadOnlyList<Subscription> Items, int Total)> ListAsync(Guid userId, SubscriptionStatus? status, int skip, int take, CancellationToken ct);
    Task<IReadOnlyList<Subscription>> ListAllAsync(Guid userId, CancellationToken ct);
    Task AddAsync(Subscription subscription, CancellationToken ct);
    Task UpdateAsync(Subscription subscription, CancellationToken ct);
}

// ---------- правила валидации (FR-08) ----------
internal static class SubscriptionRules
{
    private const decimal MaxAmount = 1_000_000_000m;
    private static readonly Regex CurrencyPattern = new("^[A-Z]{3}$", RegexOptions.Compiled);

    public static string? Name(string? v, bool required, ValidationErrors e)
    {
        if (v is null) { if (required) e.Add("name", "Name is required."); return null; }
        var t = v.Trim();
        if (t.Length is 0 or > 100) { e.Add("name", "Name must be 1-100 characters."); return null; }
        return t;
    }

    public static decimal? Amount(decimal? v, bool required, ValidationErrors e)
    {
        if (v is null) { if (required) e.Add("amount", "Amount is required."); return null; }
        if (v < 0 || v > MaxAmount) { e.Add("amount", "Amount must be between 0 and 1000000000."); return null; }
        return v;
    }

    public static string? Currency(string? v, bool required, ValidationErrors e)
    {
        if (v is null) { if (required) e.Add("currency", "Currency is required."); return null; }
        var t = v.Trim().ToUpperInvariant();
        if (!CurrencyPattern.IsMatch(t)) { e.Add("currency", "Currency must be a 3-letter ISO code, e.g. USD."); return null; }
        return t;
    }

    public static BillingPeriod? Period(string? v, bool required, ValidationErrors e)
    {
        if (v is null) { if (required) e.Add("period", "Period is required."); return null; }
        var match = Enum.GetNames<BillingPeriod>().FirstOrDefault(n => n.Equals(v.Trim(), StringComparison.OrdinalIgnoreCase));
        if (match is null) { e.Add("period", "Period must be one of: Weekly, Monthly, Quarterly, Yearly."); return null; }
        return Enum.Parse<BillingPeriod>(match);
    }

    /// <summary>Статус, который можно выставить/отфильтровать извне (Deleted — только через DELETE).</summary>
    public static SubscriptionStatus? Status(string? v, string field, ValidationErrors e)
    {
        if (v is null) return null;
        foreach (var s in new[] { SubscriptionStatus.Active, SubscriptionStatus.Paused, SubscriptionStatus.Cancelled })
            if (s.ToString().Equals(v.Trim(), StringComparison.OrdinalIgnoreCase)) return s;
        e.Add(field, "Status must be one of: Active, Paused, Cancelled.");
        return null;
    }
}

// ---------- бизнес-логика (FR-04, FR-07, FR-08) ----------
public sealed class SubscriptionService(ISubscriptionRepository repo) : ISubscriptionsApi
{
    private const int DefaultPageSize = 20, MaxPageSize = 100, MaxPage = 1_000_000;

    public async Task<PagedResult<SubscriptionDto>> ListAsync(Guid userId, string? status, int? page, int? pageSize, CancellationToken ct)
    {
        var errors = new ValidationErrors();
        var p = page ?? 1;
        var size = pageSize ?? DefaultPageSize;
        if (p is < 1 or > MaxPage) errors.Add("page", $"Page must be between 1 and {MaxPage}.");
        if (size is < 1 or > MaxPageSize) errors.Add("pageSize", $"PageSize must be between 1 and {MaxPageSize}.");
        var filter = SubscriptionRules.Status(status, "status", errors);
        errors.ThrowIfAny();

        var (items, total) = await repo.ListAsync(userId, filter, (p - 1) * size, size, ct);
        return new PagedResult<SubscriptionDto>(items.Select(ToDto).ToList(), p, size, total);
    }

    public async Task<SubscriptionDto> CreateAsync(Guid userId, CreateSubscriptionRequest request, CancellationToken ct)
    {
        var errors = new ValidationErrors();
        var name = SubscriptionRules.Name(request.Name, true, errors);
        var amount = SubscriptionRules.Amount(request.Amount, true, errors);
        var currency = SubscriptionRules.Currency(request.Currency, true, errors);
        var period = SubscriptionRules.Period(request.Period, true, errors);
        if (request.StartDate is null) errors.Add("startDate", "StartDate is required.");
        errors.ThrowIfAny();

        var subscription = new Subscription
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Name = name!,
            Amount = amount!.Value,
            Currency = currency!,
            Period = period!.Value,
            StartDate = request.StartDate!.Value,
            Status = SubscriptionStatus.Active
        };
        await repo.AddAsync(subscription, ct);
        return ToDto(subscription);
    }

    public async Task<SubscriptionDto> GetAsync(Guid userId, Guid id, CancellationToken ct) =>
        ToDto(await RequireOwnedAsync(userId, id, false, ct));

    public async Task<SubscriptionDto> UpdateAsync(Guid userId, Guid id, PatchSubscriptionRequest request, CancellationToken ct)
    {
        var subscription = await RequireOwnedAsync(userId, id, false, ct);

        var errors = new ValidationErrors();
        if (request.Name is null && request.Amount is null && request.Currency is null &&
            request.Period is null && request.StartDate is null && request.Status is null)
            errors.Add("body", "At least one field must be provided.");
        var name = SubscriptionRules.Name(request.Name, false, errors);
        var amount = SubscriptionRules.Amount(request.Amount, false, errors);
        var currency = SubscriptionRules.Currency(request.Currency, false, errors);
        var period = SubscriptionRules.Period(request.Period, false, errors);
        var status = SubscriptionRules.Status(request.Status, "status", errors);
        errors.ThrowIfAny();

        if (name is not null) subscription.Name = name;
        if (amount is not null) subscription.Amount = amount.Value;
        if (currency is not null) subscription.Currency = currency;
        if (period is not null) subscription.Period = period.Value;
        if (request.StartDate is not null) subscription.StartDate = request.StartDate.Value;
        if (status is not null) subscription.Status = status.Value;

        await repo.UpdateAsync(subscription, ct);
        return ToDto(subscription);
    }

    /// <summary>Мягкое удаление: платежи не затрагиваются.</summary>
    public async Task<DeleteResult> DeleteAsync(Guid userId, Guid id, CancellationToken ct)
    {
        var subscription = await RequireOwnedAsync(userId, id, false, ct);
        subscription.Status = SubscriptionStatus.Deleted;
        await repo.UpdateAsync(subscription, ct);
        return new DeleteResult(subscription.Id, subscription.Status.ToString());
    }

    // ---------- ISubscriptionsApi ----------
    public async Task<SubscriptionInfo> GetOwnedAsync(Guid userId, Guid subscriptionId, bool includeDeleted, CancellationToken ct) =>
        ToInfo(await RequireOwnedAsync(userId, subscriptionId, includeDeleted, ct));

    public async Task<IReadOnlyList<SubscriptionInfo>> ListAllOfUserAsync(Guid userId, CancellationToken ct) =>
        (await repo.ListAllAsync(userId, ct)).Select(ToInfo).ToList();

    // ---------- helpers ----------
    private async Task<Subscription> RequireOwnedAsync(Guid userId, Guid id, bool includeDeleted, CancellationToken ct)
    {
        var s = await repo.FindAsync(id, ct);
        if (s is null || (!includeDeleted && s.Status == SubscriptionStatus.Deleted))
            throw new NotFoundException("Subscription not found.");
        if (s.UserId != userId)
            throw new ForbiddenException("Access to this subscription is denied."); // FR-07
        return s;
    }

    private static SubscriptionDto ToDto(Subscription s) =>
        new(s.Id, s.Name, s.Amount, s.Currency, s.Period.ToString(), s.StartDate, s.Status.ToString());

    private static SubscriptionInfo ToInfo(Subscription s) =>
        new(s.Id, s.UserId, s.Name, s.Currency, s.Status.ToString());
}
