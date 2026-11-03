using SubscriptionManager.Modules.Identity.Application;
using SubscriptionManager.Modules.Identity.Infrastructure;
using SubscriptionManager.Shared;

namespace SubscriptionManager.Modules.Identity;

/// <summary>Точка входа модуля: регистрация зависимостей и HTTP-маршрутов (слой Api).</summary>
public static class IdentityModule
{
    public static IServiceCollection AddIdentityModule(this IServiceCollection services)
    {
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddSingleton<IPasswordHasher, Pbkdf2PasswordHasher>();
        services.AddSingleton<ITokenService, JwtTokenService>();
        services.AddScoped<IdentityService>();
        return services;
    }

    public static IEndpointRouteBuilder MapIdentityEndpoints(this IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup("/api/auth").WithTags("Auth");
        auth.MapPost("/register", async (RegisterRequest r, IdentityService s, CancellationToken ct) =>
                Results.Created("/api/users/me", await s.RegisterAsync(r, ct)))
            .WithSummary("FR-01: регистрация по email и паролю (201 / 409 / 422)");
        auth.MapPost("/login", async (LoginRequest r, IdentityService s, CancellationToken ct) =>
                Results.Ok(await s.LoginAsync(r, ct)))
            .WithSummary("FR-02: вход, возвращает Bearer JWT (200 / 401 / 422)");

        var users = app.MapGroup("/api/users").WithTags("Users").RequireAuthorization();
        users.MapGet("/me", async (System.Security.Claims.ClaimsPrincipal u, IdentityService s, CancellationToken ct) =>
                Results.Ok(await s.GetProfileAsync(u.GetUserId(), ct)))
            .WithSummary("FR-03: профиль текущего пользователя");
        users.MapPatch("/me", async (PatchProfileRequest r, System.Security.Claims.ClaimsPrincipal u, IdentityService s, CancellationToken ct) =>
                Results.Ok(await s.UpdateProfileAsync(u.GetUserId(), r, ct)))
            .WithSummary("FR-03: изменить email и/или пароль (нужен currentPassword)");
        return app;
    }
}
