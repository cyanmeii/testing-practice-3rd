using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using SubscriptionManager.Modules.Identity.Application;
using SubscriptionManager.Modules.Identity.Domain;
using SubscriptionManager.Shared;
using SubscriptionManager.Tests.Support;

namespace SubscriptionManager.Tests;

[TestClass]
public class IdentityServiceTests
{
    private const string Password = "Password1!";
    private FakeUserRepository _users = null!;
    private Mock<IPasswordHasher> _hasher = null!;
    private Mock<ITokenService> _tokens = null!;
    private FakeClock _clock = null!;
    private IdentityService _sut = null!;
    private User _jane = null!;

    [TestInitialize]
    public void SetUp()
    {
        _users = new FakeUserRepository();
        _clock = new FakeClock();
        _hasher = new Mock<IPasswordHasher>();
        _hasher.Setup(h => h.Hash(It.IsAny<string>())).Returns<string>(p => "hash:" + p);
        _hasher.Setup(h => h.Verify(It.IsAny<string>(), It.IsAny<string>())).Returns<string, string>((p, h) => h == "hash:" + p);
        _tokens = new Mock<ITokenService>();
        _tokens.Setup(t => t.Issue(It.IsAny<User>())).Returns(new AuthToken("jwt-token", _clock.UtcNow.AddHours(1)));
        _sut = new IdentityService(_users, _hasher.Object, _tokens.Object, _clock);

        _jane = new User { Id = Guid.NewGuid(), Email = "jane@example.com", PasswordHash = "hash:" + Password };
        _users.Items.Add(_jane);
    }

    private static async Task<ValidationFailedException> Invalid(Func<Task> action) =>
        await Assert.ThrowsExceptionAsync<ValidationFailedException>(action);

    // ---------- FR-01 регистрация ----------

    [TestMethod]
    public async Task Register_Valid_StoresNormalizedEmail_AndOnlyPasswordHash()
    {
        var dto = await _sut.RegisterAsync(new RegisterRequest("  New.User@Example.COM ", Password), default);

        Assert.AreEqual("new.user@example.com", dto.Email);
        var stored = _users.Items.Single(u => u.Id == dto.Id);
        Assert.AreEqual("hash:" + Password, stored.PasswordHash);
        Assert.AreNotEqual(Password, stored.PasswordHash);
        Assert.AreEqual(_clock.UtcNow, stored.CreatedAtUtc);
        _hasher.Verify(h => h.Hash(Password), Times.Once());
    }

    [TestMethod]
    [DataRow(null, Password, "email")]
    [DataRow("   ", Password, "email")]
    [DataRow("not-an-email", Password, "email")]
    [DataRow("a@b", Password, "email")]
    [DataRow("ok@example.com", null, "password")]
    [DataRow("ok@example.com", "short", "password")]
    public async Task Register_InvalidInput_Throws422WithFieldName(string? email, string? password, string field)
    {
        var ex = await Invalid(() => _sut.RegisterAsync(new RegisterRequest(email, password), default));

        Assert.IsTrue(ex.Errors.ContainsKey(field), $"expected error for '{field}'");
        Assert.AreEqual(1, _users.Items.Count, "пользователь не должен быть создан");
    }

    [TestMethod]
    public async Task Register_TooLongEmailAndPassword_AreRejected()
    {
        var ex = await Invalid(() => _sut.RegisterAsync(
            new RegisterRequest(new string('a', 250) + "@x.com", new string('p', 129)), default));

        Assert.IsTrue(ex.Errors.ContainsKey("email"));
        Assert.IsTrue(ex.Errors.ContainsKey("password"));
    }

    [TestMethod]
    public async Task Register_EmailAlreadyRegistered_IgnoringCase_ThrowsConflict()
    {
        await Assert.ThrowsExceptionAsync<ConflictException>(
            () => _sut.RegisterAsync(new RegisterRequest(" JANE@example.com ", Password), default));
    }

    [TestMethod]
    public async Task Register_ConcurrentDuplicate_DetectedByStorage_ThrowsConflict()
    {
        _users.FailNextAdd = true;

        await Assert.ThrowsExceptionAsync<ConflictException>(
            () => _sut.RegisterAsync(new RegisterRequest("race@example.com", Password), default));
    }

    // ---------- FR-02 авторизация ----------

    [TestMethod]
    public async Task Login_ValidCredentials_ReturnsBearerToken()
    {
        var response = await _sut.LoginAsync(new LoginRequest(" Jane@Example.com", Password), default);

        Assert.AreEqual("jwt-token", response.AccessToken);
        Assert.AreEqual("Bearer", response.TokenType);
        Assert.AreEqual(_clock.UtcNow.AddHours(1), response.ExpiresAtUtc);
    }

    [TestMethod]
    public async Task Login_UnknownEmail_Throws401()
    {
        await Assert.ThrowsExceptionAsync<UnauthorizedException>(
            () => _sut.LoginAsync(new LoginRequest("nobody@example.com", Password), default));
    }

    [TestMethod]
    public async Task Login_WrongPassword_Throws401_WithSameMessageAsUnknownEmail()
    {
        var wrong = await Assert.ThrowsExceptionAsync<UnauthorizedException>(
            () => _sut.LoginAsync(new LoginRequest("jane@example.com", "Wrong-pass1"), default));
        var unknown = await Assert.ThrowsExceptionAsync<UnauthorizedException>(
            () => _sut.LoginAsync(new LoginRequest("nobody@example.com", "Wrong-pass1"), default));

        Assert.AreEqual(unknown.Message, wrong.Message);
        _tokens.Verify(t => t.Issue(It.IsAny<User>()), Times.Never());
    }

    [TestMethod]
    public async Task Login_MissingFields_Throws422ForBoth()
    {
        var ex = await Invalid(() => _sut.LoginAsync(new LoginRequest(null, ""), default));

        Assert.IsTrue(ex.Errors.ContainsKey("email"));
        Assert.IsTrue(ex.Errors.ContainsKey("password"));
    }

    // ---------- FR-03 профиль ----------

    [TestMethod]
    public async Task GetProfile_ReturnsIdAndEmail()
    {
        var dto = await _sut.GetProfileAsync(_jane.Id, default);

        Assert.AreEqual(_jane.Id, dto.Id);
        Assert.AreEqual("jane@example.com", dto.Email);
    }

    [TestMethod]
    public async Task GetProfile_UnknownUser_Throws401()
    {
        await Assert.ThrowsExceptionAsync<UnauthorizedException>(() => _sut.GetProfileAsync(Guid.NewGuid(), default));
    }

    [TestMethod]
    public async Task UpdateProfile_NewEmail_IsNormalizedAndSaved()
    {
        var dto = await _sut.UpdateProfileAsync(_jane.Id, new PatchProfileRequest(" New@Example.com", null, null), default);

        Assert.AreEqual("new@example.com", dto.Email);
        Assert.AreEqual("new@example.com", _jane.Email);
        Assert.AreEqual("hash:" + Password, _jane.PasswordHash, "пароль не меняется");
    }

    [TestMethod]
    public async Task UpdateProfile_SameEmailDifferentCase_IsNotAConflict()
    {
        var dto = await _sut.UpdateProfileAsync(_jane.Id, new PatchProfileRequest("JANE@example.com", null, null), default);

        Assert.AreEqual("jane@example.com", dto.Email);
    }

    [TestMethod]
    public async Task UpdateProfile_EmailTakenByAnotherUser_ThrowsConflict()
    {
        _users.Items.Add(new User { Id = Guid.NewGuid(), Email = "bob@example.com", PasswordHash = "x" });

        await Assert.ThrowsExceptionAsync<ConflictException>(
            () => _sut.UpdateProfileAsync(_jane.Id, new PatchProfileRequest("bob@example.com", null, null), default));
        Assert.AreEqual("jane@example.com", _jane.Email);
    }

    [TestMethod]
    public async Task UpdateProfile_StorageReportsDuplicate_ThrowsConflict()
    {
        _users.FailNextUpdate = true;

        await Assert.ThrowsExceptionAsync<ConflictException>(
            () => _sut.UpdateProfileAsync(_jane.Id, new PatchProfileRequest("fresh@example.com", null, null), default));
    }

    [TestMethod]
    public async Task UpdateProfile_NoFields_Throws422()
    {
        var ex = await Invalid(() => _sut.UpdateProfileAsync(_jane.Id, new PatchProfileRequest(null, null, null), default));

        Assert.IsTrue(ex.Errors.ContainsKey("body"));
    }

    [TestMethod]
    public async Task UpdateProfile_InvalidEmail_Throws422()
    {
        var ex = await Invalid(() => _sut.UpdateProfileAsync(_jane.Id, new PatchProfileRequest("broken", null, null), default));

        Assert.IsTrue(ex.Errors.ContainsKey("email"));
    }

    [TestMethod]
    public async Task UpdateProfile_NewPasswordWithCorrectCurrent_ReplacesHash()
    {
        await _sut.UpdateProfileAsync(_jane.Id, new PatchProfileRequest(null, Password, "BrandNewPass1"), default);

        Assert.AreEqual("hash:BrandNewPass1", _jane.PasswordHash);
    }

    [TestMethod]
    [DataRow("Wrong-current1", "BrandNewPass1", "currentPassword")]
    [DataRow(null, "BrandNewPass1", "currentPassword")]
    [DataRow(Password, "short", "newPassword")]
    public async Task UpdateProfile_PasswordChangeRejected_Throws422(string? current, string newPassword, string field)
    {
        var ex = await Invalid(() => _sut.UpdateProfileAsync(_jane.Id, new PatchProfileRequest(null, current, newPassword), default));

        Assert.IsTrue(ex.Errors.ContainsKey(field));
        Assert.AreEqual("hash:" + Password, _jane.PasswordHash);
    }
}
