namespace SubscriptionManager.Modules.Identity.Domain;

public sealed class User
{
    public Guid Id { get; set; }
    public string Email { get; set; } = "";          // всегда в нижнем регистре, без пробелов
    public string PasswordHash { get; set; } = "";   // только хеш (NFR-01)
    public DateTime CreatedAtUtc { get; set; }
}
