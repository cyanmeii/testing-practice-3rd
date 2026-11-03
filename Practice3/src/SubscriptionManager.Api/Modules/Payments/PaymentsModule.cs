using System.Security.Claims;
using SubscriptionManager.Modules.Payments.Application;
using SubscriptionManager.Modules.Payments.Contracts;
using SubscriptionManager.Modules.Payments.Infrastructure;
using SubscriptionManager.Shared;

namespace SubscriptionManager.Modules.Payments;

public static class PaymentsModule
{
    public static IServiceCollection AddPaymentsModule(this IServiceCollection services)
    {
        services.AddScoped<IPaymentRepository, PaymentRepository>();
        services.AddScoped<PaymentService>();
        services.AddScoped<IPaymentsApi>(sp => sp.GetRequiredService<PaymentService>());
        return services;
    }

    public static IEndpointRouteBuilder MapPaymentsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/payments", async (ClaimsPrincipal u, Guid? subscriptionId, DateOnly? dateFrom, DateOnly? dateTo,
                    PaymentService s, CancellationToken ct) =>
                Results.Ok(await s.ListAsync(u.GetUserId(), subscriptionId, dateFrom, dateTo, ct)))
            .WithTags("Payments").RequireAuthorization()
            .WithSummary("FR-05: история платежей (subscriptionId, dateFrom, dateTo — необязательны)");

        // Дополнение к таблице 2 ТЗ: без точки записи платежей FR-05 и NFR-03 нельзя проверить.
        app.MapPost("/api/subscriptions/{id:guid}/payments", async (Guid id, CreatePaymentRequest r, ClaimsPrincipal u,
                    PaymentService s, CancellationToken ct) =>
            {
                var result = await s.RecordAsync(u.GetUserId(), id, r, ct);
                return result.Created
                    ? Results.Created($"/api/payments?subscriptionId={id}", result.Payment)
                    : Results.Ok(result.Payment); // идемпотентный повтор того же externalId
            })
            .WithTags("Payments").RequireAuthorization()
            .WithSummary("FR-05/NFR-03: записать платёж по подписке (201 создан / 200 повтор того же externalId)");
        return app;
    }
}
