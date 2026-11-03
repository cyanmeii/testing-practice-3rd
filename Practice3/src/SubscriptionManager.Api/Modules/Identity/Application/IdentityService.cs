using System.Text.RegularExpressions;
using SubscriptionManager.Modules.Identity.Domain;
using SubscriptionManager.Shared;

namespace SubscriptionManager.Modules.Identity.Application;

// ---------- DTO ----------
public sealed record RegisterRequest(string? Email, string? Password);
public sealed record LoginRequest(string? Email, string? Password);
public sealed record PatchProfileRequest(string? Email, string? CurrentPassword, string? NewPassword);
public sealed record UserDto(Guid Id, string Email);
public sealed record LoginResponse(string AccessToken, string TokenType, DateTime ExpiresAtUtc);
public sealed record AuthToken(string AccessToken, DateTime ExpiresAtUtc);

// ---------- порты (реализуются в Infrastructure) ----------
public interface IUserRepository
{
    Task<User?> FindByIdAsync(Guid id, CancellationToken ct);
    Task<User?> FindByEmailAsync(string normalizedEmail, CancellationToken ct);
    /// <returns>false — email уже занят (нарушена уникальность).</returns>
    Task<bool> TryAddAsync(User user, CancellationToken ct);
    /// <returns>false — email уже занят (нарушена уникальность).</returns>
    Task<bool> TryUpdateAsync(User user, CancellationToken ct);
}

public interface IPasswordHasher
{
    string Hash(string password);
    bool Verify(string password, string hash);
}

public interface ITokenService
{
    AuthToken Issue(User user);
}

// ---------- бизнес-логика (FR-01, FR-02, FR-03, FR-08) ----------
public sealed class IdentityService(IUserRepository users, IPasswordHasher hasher, ITokenService tokens, IClock clock)
{
    private static readonly Regex EmailPattern = new(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.Compiled);

    public async Task<UserDto> RegisterAsync(RegisterRequest request, CancellationToken ct)
    {
        var errors = new ValidationErrors();
        var email = ValidateEmail(request.Email, "email", errors);
        ValidatePassword(request.Password, "password", errors);
        errors.ThrowIfAny();

        if (await users.FindByEmailAsync(email, ct) is not null)
            throw new ConflictException("Email is already registered.");

        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = email,
            PasswordHash = hasher.Hash(request.Password!),
            CreatedAtUtc = clock.UtcNow
        };
        // повторная защита от гонки: уникальный индекс в БД
        if (!await users.TryAddAsync(user, ct))
            throw new ConflictException("Email is already registered.");

        return ToDto(user);
    }

    public async Task<LoginResponse> LoginAsync(LoginRequest request, CancellationToken ct)
    {
        var errors = new ValidationErrors();
        if (string.IsNullOrWhiteSpace(request.Email)) errors.Add("email", "Email is required.");
        if (string.IsNullOrEmpty(request.Password)) errors.Add("password", "Password is required.");
        errors.ThrowIfAny();

        var user = await users.FindByEmailAsync(NormalizeEmail(request.Email!), ct);
        // одинаковый ответ для «нет пользователя» и «неверный пароль» — не раскрываем, какой email зарегистрирован
        if (user is null || !hasher.Verify(request.Password!, user.PasswordHash))
            throw new UnauthorizedException("Invalid email or password.");

        var token = tokens.Issue(user);
        return new LoginResponse(token.AccessToken, "Bearer", token.ExpiresAtUtc);
    }

    public async Task<UserDto> GetProfileAsync(Guid userId, CancellationToken ct) =>
        ToDto(await RequireUserAsync(userId, ct));

    public async Task<UserDto> UpdateProfileAsync(Guid userId, PatchProfileRequest request, CancellationToken ct)
    {
        var user = await RequireUserAsync(userId, ct);

        var errors = new ValidationErrors();
        if (request.Email is null && request.NewPassword is null)
            errors.Add("body", "At least one of email, newPassword must be provided.");

        string? newEmail = null;
        if (request.Email is not null) newEmail = ValidateEmail(request.Email, "email", errors);

        if (request.NewPassword is not null)
        {
            ValidatePassword(request.NewPassword, "newPassword", errors);
            if (string.IsNullOrEmpty(request.CurrentPassword) || !hasher.Verify(request.CurrentPassword, user.PasswordHash))
                errors.Add("currentPassword", "Current password is missing or incorrect.");
        }
        errors.ThrowIfAny();

        if (newEmail is not null && newEmail != user.Email)
        {
            if (await users.FindByEmailAsync(newEmail, ct) is not null)
                throw new ConflictException("Email is already registered.");
            user.Email = newEmail;
        }
        if (request.NewPassword is not null)
            user.PasswordHash = hasher.Hash(request.NewPassword);

        if (!await users.TryUpdateAsync(user, ct))
            throw new ConflictException("Email is already registered.");

        return ToDto(user);
    }

    // ---------- helpers ----------
    private async Task<User> RequireUserAsync(Guid userId, CancellationToken ct) =>
        await users.FindByIdAsync(userId, ct) ?? throw new UnauthorizedException("User no longer exists.");

    private static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();

    private static string ValidateEmail(string? value, string field, ValidationErrors errors)
    {
        if (string.IsNullOrWhiteSpace(value)) { errors.Add(field, "Email is required."); return ""; }
        var email = NormalizeEmail(value);
        if (email.Length > 254 || !EmailPattern.IsMatch(email)) { errors.Add(field, "Email format is invalid."); return ""; }
        return email;
    }

    private static void ValidatePassword(string? value, string field, ValidationErrors errors)
    {
        if (string.IsNullOrEmpty(value)) errors.Add(field, "Password is required.");
        else if (value.Length is < 8 or > 128) errors.Add(field, "Password must be 8-128 characters long.");
    }

    private static UserDto ToDto(User u) => new(u.Id, u.Email);
}
