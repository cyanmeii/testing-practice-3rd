using System.Security.Claims;
using SubscriptionManager.Modules.Subscriptions.Application;
using SubscriptionManager.Modules.Subscriptions.Contracts;
using SubscriptionManager.Modules.Subscriptions.Infrastructure;
using SubscriptionManager.Shared;

namespace SubscriptionManager.Modules.Subscriptions;

public static class SubscriptionsModule
{
    public static IServiceCollection AddSubscriptionsModule(this IServiceCollection services)
    {
        services.AddScoped<ISubscriptionRepository, SubscriptionRepository>();
        services.AddScoped<SubscriptionService>();
        services.AddScoped<ISubscriptionsApi>(sp => sp.GetRequiredService<SubscriptionService>()); // контракт для других модулей
        return services;
    }

    public static IEndpointRouteBuilder MapSubscriptionsEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/subscriptions").WithTags("Subscriptions").RequireAuthorization();

        g.MapGet("/", async (ClaimsPrincipal u, string? status, int? page, int? pageSize, SubscriptionService s, CancellationToken ct) =>
                Results.Ok(await s.ListAsync(u.GetUserId(), status, page, pageSize, ct)))
            .WithSummary("FR-04: список подписок (status, page, pageSize)");

        g.MapPost("/", async (CreateSubscriptionRequest r, ClaimsPrincipal u, SubscriptionService s, CancellationToken ct) =>
            {
                var dto = await s.CreateAsync(u.GetUserId(), r, ct);
                return Results.Created($"/api/subscriptions/{dto.Id}", dto);
            })
            .WithSummary("FR-04: создать подписку (201 / 422)");

        g.MapGet("/{id:guid}", async (Guid id, ClaimsPrincipal u, SubscriptionService s, CancellationToken ct) =>
                Results.Ok(await s.GetAsync(u.GetUserId(), id, ct)))
            .WithSummary("FR-04: получить подписку (200 / 403 / 404)");

        g.MapPatch("/{id:guid}", async (Guid id, PatchSubscriptionRequest r, ClaimsPrincipal u, SubscriptionService s, CancellationToken ct) =>
                Results.Ok(await s.UpdateAsync(u.GetUserId(), id, r, ct)))
            .WithSummary("FR-04: частично изменить подписку (200 / 403 / 404 / 422)");

        g.MapDelete("/{id:guid}", async (Guid id, ClaimsPrincipal u, SubscriptionService s, CancellationToken ct) =>
                Results.Ok(await s.DeleteAsync(u.GetUserId(), id, ct)))
            .WithSummary("FR-04: удалить подписку (мягко; платежи сохраняются)");
        return app;
    }
}
