using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SubscriptionManager.Modules.Payments.Application;
using SubscriptionManager.Modules.Payments.Domain;
using SubscriptionManager.Shared.Persistence;

namespace SubscriptionManager.Modules.Payments.Infrastructure;

public sealed class PaymentConfiguration : IEntityTypeConfiguration<Payment>
{
    public void Configure(EntityTypeBuilder<Payment> b)
    {
        b.ToTable("Payments");
        b.HasKey(x => x.Id);
        b.Property(x => x.Currency).IsRequired().HasMaxLength(3);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
        b.Property(x => x.ExternalId).IsRequired().HasMaxLength(100);
        b.HasIndex(x => x.ExternalId).IsUnique(); // NFR-03: повторная обработка не создаёт дубликат
        b.HasIndex(x => new { x.SubscriptionId, x.PaymentDate });
    }
}

public sealed class PaymentRepository(AppDbContext db) : IPaymentRepository
{
    public Task<Payment?> FindByExternalIdAsync(string externalId, CancellationToken ct) =>
        db.Payments.AsNoTracking().FirstOrDefaultAsync(p => p.ExternalId == externalId, ct);

    public Task<bool> TryAddAsync(Payment payment, CancellationToken ct)
    {
        db.Payments.Add(payment);
        return db.TrySaveAsync(ct);
    }

    public async Task<IReadOnlyList<Payment>> ListAsync(
        IReadOnlyCollection<Guid> subscriptionIds, DateOnly? from, DateOnly? to, PaymentStatus? status, CancellationToken ct)
    {
        var ids = subscriptionIds.ToArray();
        var query = db.Payments.AsNoTracking().Where(p => ids.Contains(p.SubscriptionId));
        if (from is { } f) query = query.Where(p => p.PaymentDate >= f);
        if (to is { } t) query = query.Where(p => p.PaymentDate <= t);
        if (status is { } s) query = query.Where(p => p.Status == s);
        return await query.OrderByDescending(p => p.PaymentDate).ThenBy(p => p.ExternalId).ToListAsync(ct);
    }
}
