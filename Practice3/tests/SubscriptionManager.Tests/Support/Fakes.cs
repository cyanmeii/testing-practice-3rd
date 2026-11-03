using SubscriptionManager.Modules.Identity.Application;
using SubscriptionManager.Modules.Identity.Domain;
using SubscriptionManager.Modules.Payments.Application;
using SubscriptionManager.Modules.Payments.Domain;
using SubscriptionManager.Modules.Subscriptions.Application;
using SubscriptionManager.Modules.Subscriptions.Domain;
using SubscriptionManager.Shared;

namespace SubscriptionManager.Tests.Support;

public sealed class FakeClock : IClock
{
    public DateTime UtcNow { get; set; } = new(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc);
}

public sealed class FakeUserRepository : IUserRepository
{
    public List<User> Items { get; } = new();
    public bool FailNextAdd { get; set; }
    public bool FailNextUpdate { get; set; }

    public Task<User?> FindByIdAsync(Guid id, CancellationToken ct) => Task.FromResult(Items.FirstOrDefault(u => u.Id == id));
    public Task<User?> FindByEmailAsync(string email, CancellationToken ct) => Task.FromResult(Items.FirstOrDefault(u => u.Email == email));

    public Task<bool> TryAddAsync(User user, CancellationToken ct)
    {
        if (FailNextAdd || Items.Any(u => u.Email == user.Email)) return Task.FromResult(false);
        Items.Add(user);
        return Task.FromResult(true);
    }

    public Task<bool> TryUpdateAsync(User user, CancellationToken ct)
    {
        if (!FailNextUpdate) return Task.FromResult(true);
        FailNextUpdate = false;
        return Task.FromResult(false);
    }
}

public sealed class FakeSubscriptionRepository : ISubscriptionRepository
{
    public List<Subscription> Items { get; } = new();
    public int UpdateCalls { get; private set; }

    public Task<Subscription?> FindAsync(Guid id, CancellationToken ct) => Task.FromResult(Items.FirstOrDefault(s => s.Id == id));

    public Task<(IReadOnlyList<Subscription> Items, int Total)> ListAsync(Guid userId, SubscriptionStatus? status, int skip, int take, CancellationToken ct)
    {
        var q = Items.Where(s => s.UserId == userId && s.Status != SubscriptionStatus.Deleted);
        if (status is { } st) q = q.Where(s => s.Status == st);
        var ordered = q.OrderBy(s => s.StartDate).ThenBy(s => s.Name).ToList();
        return Task.FromResult(((IReadOnlyList<Subscription>)ordered.Skip(skip).Take(take).ToList(), ordered.Count));
    }

    public Task<IReadOnlyList<Subscription>> ListAllAsync(Guid userId, CancellationToken ct) =>
        Task.FromResult((IReadOnlyList<Subscription>)Items.Where(s => s.UserId == userId).ToList());

    public Task AddAsync(Subscription subscription, CancellationToken ct) { Items.Add(subscription); return Task.CompletedTask; }
    public Task UpdateAsync(Subscription subscription, CancellationToken ct) { UpdateCalls++; return Task.CompletedTask; }
}

public sealed class FakePaymentRepository : IPaymentRepository
{
    public List<Payment> Items { get; } = new();
    /// <summary>Платёж «параллельного запроса»: появится в хранилище в момент TryAdd, который вернёт false.</summary>
    public Payment? RaceWinner { get; set; }
    public bool FailAddWithoutWinner { get; set; }

    public Task<Payment?> FindByExternalIdAsync(string externalId, CancellationToken ct) =>
        Task.FromResult(Items.FirstOrDefault(p => p.ExternalId == externalId));

    public Task<bool> TryAddAsync(Payment payment, CancellationToken ct)
    {
        if (RaceWinner is not null) { Items.Add(RaceWinner); RaceWinner = null; return Task.FromResult(false); }
        if (FailAddWithoutWinner || Items.Any(p => p.ExternalId == payment.ExternalId)) return Task.FromResult(false);
        Items.Add(payment);
        return Task.FromResult(true);
    }

    public Task<IReadOnlyList<Payment>> ListAsync(IReadOnlyCollection<Guid> ids, DateOnly? from, DateOnly? to, PaymentStatus? status, CancellationToken ct)
    {
        var q = Items.Where(p => ids.Contains(p.SubscriptionId));
        if (from is { } f) q = q.Where(p => p.PaymentDate >= f);
        if (to is { } t) q = q.Where(p => p.PaymentDate <= t);
        if (status is { } s) q = q.Where(p => p.Status == s);
        return Task.FromResult((IReadOnlyList<Payment>)q.OrderByDescending(p => p.PaymentDate).ThenBy(p => p.ExternalId).ToList());
    }
}

public static class Seed
{
    public static Subscription Sub(Guid userId, string name = "Netflix", string currency = "USD",
        SubscriptionStatus status = SubscriptionStatus.Active, int startDay = 1) => new()
    {
        Id = Guid.NewGuid(), UserId = userId, Name = name, Amount = 10m, Currency = currency,
        Period = BillingPeriod.Monthly, StartDate = new DateOnly(2026, 1, startDay), Status = status
    };

    public static Payment Pay(Guid subscriptionId, string externalId, decimal amount = 10m, string currency = "USD",
        string date = "2026-02-15", PaymentStatus status = PaymentStatus.Succeeded) => new()
    {
        Id = Guid.NewGuid(), SubscriptionId = subscriptionId, Amount = amount, Currency = currency,
        PaymentDate = DateOnly.Parse(date), Status = status, ExternalId = externalId
    };
}
