using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using SubscriptionManager.Modules.Identity.Application;
using SubscriptionManager.Modules.Identity.Domain;
using SubscriptionManager.Shared;
using SubscriptionManager.Shared.Persistence;

namespace SubscriptionManager.Modules.Identity.Infrastructure;

public sealed class JwtOptions
{
    public string Key { get; set; } = "";
    public string Issuer { get; set; } = "SubscriptionManager";
    public string Audience { get; set; } = "SubscriptionManager.Clients";
    public int ExpiresMinutes { get; set; } = 60;
}

public sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> b)
    {
        b.ToTable("Users");
        b.HasKey(x => x.Id);
        b.Property(x => x.Email).IsRequired().HasMaxLength(254);
        b.HasIndex(x => x.Email).IsUnique();
        b.Property(x => x.PasswordHash).IsRequired();
    }
}

public sealed class UserRepository(AppDbContext db) : IUserRepository
{
    public Task<User?> FindByIdAsync(Guid id, CancellationToken ct) =>
        db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);

    public Task<User?> FindByEmailAsync(string normalizedEmail, CancellationToken ct) =>
        db.Users.FirstOrDefaultAsync(u => u.Email == normalizedEmail, ct);

    public Task<bool> TryAddAsync(User user, CancellationToken ct)
    {
        db.Users.Add(user);
        return db.TrySaveAsync(ct);
    }

    public Task<bool> TryUpdateAsync(User user, CancellationToken ct) => db.TrySaveAsync(ct);
}

/// <summary>PBKDF2-HMAC-SHA256 с индивидуальной солью. Формат: v1.{итерации}.{соль b64}.{хеш b64}</summary>
public sealed class Pbkdf2PasswordHasher : IPasswordHasher
{
    private const int SaltSize = 16, KeySize = 32, Iterations = 100_000;

    public string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, KeySize);
        return $"v1.{Iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(key)}";
    }

    public bool Verify(string password, string hash)
    {
        var parts = hash.Split('.');
        if (parts.Length != 4 || parts[0] != "v1" || !int.TryParse(parts[1], out var iterations) || iterations <= 0)
            return false;
        byte[] salt, expected;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expected = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return false;
        }
        if (expected.Length == 0) return false;
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}

public sealed class JwtTokenService(IOptions<JwtOptions> options, IClock clock) : ITokenService
{
    public AuthToken Issue(User user)
    {
        var o = options.Value;
        var now = clock.UtcNow;
        var expires = now.AddMinutes(o.ExpiresMinutes);
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(o.Key)), SecurityAlgorithms.HmacSha256);
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new Claim(JwtRegisteredClaimNames.Email, user.Email),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };
        var token = new JwtSecurityToken(o.Issuer, o.Audience, claims, now, expires, credentials);
        return new AuthToken(new JwtSecurityTokenHandler().WriteToken(token), expires);
    }
}
