using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SubscriptionManager.Modules.Subscriptions.Application;
using SubscriptionManager.Modules.Subscriptions.Domain;
using SubscriptionManager.Shared.Persistence;

namespace SubscriptionManager.Modules.Subscriptions.Infrastructure;

public sealed class SubscriptionConfiguration : IEntityTypeConfiguration<Subscription>
{
    public void Configure(EntityTypeBuilder<Subscription> b)
    {
        b.ToTable("Subscriptions");
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).IsRequired().HasMaxLength(100);
        b.Property(x => x.Currency).IsRequired().HasMaxLength(3);
        b.Property(x => x.Period).HasConversion<string>().HasMaxLength(16);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
        b.HasIndex(x => x.UserId);
    }
}

public sealed class SubscriptionRepository(AppDbContext db) : ISubscriptionRepository
{
    public Task<Subscription?> FindAsync(Guid id, CancellationToken ct) =>
        db.Subscriptions.FirstOrDefaultAsync(s => s.Id == id, ct);

    public async Task<(IReadOnlyList<Subscription> Items, int Total)> ListAsync(
        Guid userId, SubscriptionStatus? status, int skip, int take, CancellationToken ct)
    {
        var query = db.Subscriptions.AsNoTracking()
            .Where(s => s.UserId == userId && s.Status != SubscriptionStatus.Deleted);
        if (status is { } st) query = query.Where(s => s.Status == st);

        var total = await query.CountAsync(ct);
        var items = await query.OrderBy(s => s.StartDate).ThenBy(s => s.Name).ThenBy(s => s.Id)
            .Skip(skip).Take(take).ToListAsync(ct);
        return (items, total);
    }

    public async Task<IReadOnlyList<Subscription>> ListAllAsync(Guid userId, CancellationToken ct) =>
        await db.Subscriptions.AsNoTracking().Where(s => s.UserId == userId).ToListAsync(ct);

    public async Task AddAsync(Subscription subscription, CancellationToken ct)
    {
        db.Subscriptions.Add(subscription);
        await db.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(Subscription subscription, CancellationToken ct) =>
        await db.SaveChangesAsync(ct); // сущность отслеживается контекстом (получена через FindAsync)
}
