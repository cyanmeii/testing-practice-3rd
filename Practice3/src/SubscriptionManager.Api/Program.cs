using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using SubscriptionManager.Modules.Analytics;
using SubscriptionManager.Modules.Identity;
using SubscriptionManager.Modules.Identity.Infrastructure;
using SubscriptionManager.Modules.Payments;
using SubscriptionManager.Modules.Subscriptions;
using SubscriptionManager.Shared;
using SubscriptionManager.Shared.Persistence;

var builder = WebApplication.CreateBuilder(args);

// ---- конфигурация JWT (fail-fast: слабый или дефолтный ключ в Production недопустим) ----
var jwt = builder.Configuration.GetSection("Jwt").Get<JwtOptions>() ?? new JwtOptions();
if (Encoding.UTF8.GetByteCount(jwt.Key) < 32)
    throw new InvalidOperationException("Jwt:Key must be at least 32 bytes long.");
if (builder.Environment.IsProduction() && jwt.Key.StartsWith("DEV-ONLY", StringComparison.Ordinal))
    throw new InvalidOperationException("Set a real Jwt:Key (e.g. via environment variable Jwt__Key) in Production.");

// ---- инфраструктура ----
builder.Services.AddDbContext<AppDbContext>(o =>
    o.UseSqlite(builder.Configuration.GetConnectionString("Default") ?? "Data Source=subscriptions.db"));
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection("Jwt"));

// ---- модули ----
builder.Services.AddIdentityModule();
builder.Services.AddSubscriptionsModule();
builder.Services.AddPaymentsModule();
builder.Services.AddAnalyticsModule();

// ---- аутентификация / ошибки / документация ----
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
{
    o.MapInboundClaims = false; // оставляем claim "sub" как есть
    o.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true, ValidIssuer = jwt.Issuer,
        ValidateAudience = true, ValidAudience = jwt.Audience,
        ValidateLifetime = true, ValidateIssuerSigningKey = true,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Key)),
        ClockSkew = TimeSpan.FromSeconds(30)
    };
});
builder.Services.AddAuthorization();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddProblemDetails();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "Subscription Manager API", Version = "v1" });
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.Http, Scheme = "bearer", BearerFormat = "JWT",
        In = ParameterLocation.Header, Description = "Токен из POST /api/auth/login"
    });
    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        { new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" } }, Array.Empty<string>() }
    });
});

var app = builder.Build();

app.UseExceptionHandler();
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}
app.UseAuthentication();
app.UseAuthorization();

app.MapIdentityEndpoints();
app.MapSubscriptionsEndpoints();
app.MapPaymentsEndpoints();
app.MapAnalyticsEndpoints();

// Для учебного проекта схема создаётся при старте. В production — миграции EF Core.
using (var scope = app.Services.CreateScope())
    scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreated();

app.Run();

public partial class Program { } // нужен для WebApplicationFactory<Program> в интеграционных тестах
