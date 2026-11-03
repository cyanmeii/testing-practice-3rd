using SubscriptionManager.Modules.Payments.Contracts;
using SubscriptionManager.Modules.Subscriptions.Contracts;
using SubscriptionManager.Shared;

namespace SubscriptionManager.Modules.Analytics.Application;

public sealed record CurrencyTotal(string Currency, decimal Total);
public sealed record SubscriptionExpense(Guid SubscriptionId, string Name, string Currency, decimal Total, int PaymentsCount);
public sealed record ExpensesReport(
    DateOnly DateFrom, DateOnly DateTo,
    IReadOnlyList<CurrencyTotal> Totals,
    IReadOnlyList<SubscriptionExpense> BySubscription,
    int PaymentsCount);

/// <summary>FR-06: расходы за период. Суммы разных валют НЕ складываются — итог считается по каждой валюте отдельно.</summary>
public sealed class AnalyticsService(ISubscriptionsApi subscriptions, IPaymentsApi payments)
{
    public async Task<ExpensesReport> GetExpensesAsync(Guid userId, DateOnly? dateFrom, DateOnly? dateTo, CancellationToken ct)
    {
        var errors = new ValidationErrors();
        if (dateFrom is null) errors.Add("dateFrom", "dateFrom is required.");
        if (dateTo is null) errors.Add("dateTo", "dateTo is required.");
        if (dateFrom > dateTo) errors.Add("dateFrom", "dateFrom must not be later than dateTo.");
        errors.ThrowIfAny();
        var from = dateFrom!.Value;
        var to = dateTo!.Value;

        // включая удалённые подписки: их история платежей нужна для расчёта (раздел 2.5 ТЗ)
        var owned = await subscriptions.ListAllOfUserAsync(userId, ct);
        if (owned.Count == 0)
            return new ExpensesReport(from, to, Array.Empty<CurrencyTotal>(), Array.Empty<SubscriptionExpense>(), 0);

        var names = owned.ToDictionary(s => s.Id, s => s.Name);
        var list = await payments.ListSucceededAsync(owned.Select(s => s.Id).ToArray(), from, to, ct);

        var totals = list.GroupBy(p => p.Currency)
            .Select(g => new CurrencyTotal(g.Key, g.Sum(p => p.Amount)))
            .OrderBy(t => t.Currency, StringComparer.Ordinal).ToList();

        var bySubscription = list.GroupBy(p => (p.SubscriptionId, p.Currency))
            .Select(g => new SubscriptionExpense(g.Key.SubscriptionId, names[g.Key.SubscriptionId], g.Key.Currency,
                g.Sum(p => p.Amount), g.Count()))
            .OrderByDescending(e => e.Total).ThenBy(e => e.Name, StringComparer.Ordinal).ToList();

        return new ExpensesReport(from, to, totals, bySubscription, list.Count);
    }
}
