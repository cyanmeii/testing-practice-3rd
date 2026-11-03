using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SubscriptionManager.Modules.Identity.Domain;
using SubscriptionManager.Modules.Payments.Domain;
using SubscriptionManager.Modules.Subscriptions.Domain;

namespace SubscriptionManager.Shared.Persistence;

/// <summary>
/// Одна БД на монолит; конфигурации сущностей лежат в Infrastructure каждого модуля
/// (IEntityTypeConfiguration), между модулями нет внешних ключей — целостность обеспечивают контракты модулей.
/// </summary>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Subscription> Subscriptions => Set<Subscription>();
    public DbSet<Payment> Payments => Set<Payment>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
}

public static class DbExtensions
{
    /// <summary>Сохраняет изменения; false — нарушено ограничение уникальности (контекст очищается).</summary>
    public static async Task<bool> TrySaveAsync(this DbContext db, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqliteException { SqliteErrorCode: 19 } s
                                            && s.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase))
        {
            db.ChangeTracker.Clear();
            return false;
        }
    }
}
