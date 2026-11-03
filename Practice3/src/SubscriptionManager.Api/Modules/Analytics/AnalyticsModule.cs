using System.Security.Claims;
using SubscriptionManager.Modules.Analytics.Application;
using SubscriptionManager.Shared;

namespace SubscriptionManager.Modules.Analytics;

public static class AnalyticsModule
{
    public static IServiceCollection AddAnalyticsModule(this IServiceCollection services) =>
        services.AddScoped<AnalyticsService>();

    public static IEndpointRouteBuilder MapAnalyticsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/analytics/expenses", async (ClaimsPrincipal u, DateOnly? dateFrom, DateOnly? dateTo,
                    AnalyticsService s, CancellationToken ct) =>
                Results.Ok(await s.GetExpensesAsync(u.GetUserId(), dateFrom, dateTo, ct)))
            .WithTags("Analytics").RequireAuthorization()
            .WithSummary("FR-06: расходы за период (dateFrom, dateTo обязательны, формат yyyy-MM-dd)");
        return app;
    }
}
