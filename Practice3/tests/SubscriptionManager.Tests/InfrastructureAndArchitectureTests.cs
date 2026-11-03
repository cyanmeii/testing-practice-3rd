using System.IdentityModel.Tokens.Jwt;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SubscriptionManager.Modules.Identity.Domain;
using SubscriptionManager.Modules.Identity.Infrastructure;
using SubscriptionManager.Tests.Support;

namespace SubscriptionManager.Tests;

[TestClass]
public class Pbkdf2PasswordHasherTests
{
    private readonly Pbkdf2PasswordHasher _hasher = new();

    [TestMethod]
    public void Hash_IsNotPlainText_AndVerifiesCorrectPasswordOnly()
    {
        var hash = _hasher.Hash("Password1!");

        Assert.IsFalse(hash.Contains("Password1!"));
        Assert.IsTrue(_hasher.Verify("Password1!", hash));
        Assert.IsFalse(_hasher.Verify("password1!", hash));
    }

    [TestMethod]
    public void Hash_SamePasswordTwice_GivesDifferentHashes_BecauseOfSalt()
    {
        Assert.AreNotEqual(_hasher.Hash("Password1!"), _hasher.Hash("Password1!"));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("plain")]
    [DataRow("v2.1000.AA==.AA==")]
    [DataRow("v1.abc.AA==.AA==")]
    [DataRow("v1.0.AA==.AA==")]
    [DataRow("v1.10.***.***")]
    [DataRow("v1.10.AA==.")]
    public void Verify_MalformedHash_ReturnsFalse_InsteadOfThrowing(string hash)
    {
        Assert.IsFalse(_hasher.Verify("Password1!", hash));
    }
}

[TestClass]
public class JwtTokenServiceTests
{
    [TestMethod]
    public void Issue_CreatesSignedTokenWithSubEmailAndExpiry()
    {
        var clock = new FakeClock();
        var options = Options.Create(new JwtOptions { Key = new string('k', 40), ExpiresMinutes = 30 });
        var user = new User { Id = Guid.NewGuid(), Email = "jane@example.com" };

        var token = new JwtTokenService(options, clock).Issue(user);

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token.AccessToken);
        Assert.AreEqual(user.Id.ToString(), jwt.Subject);
        Assert.AreEqual("jane@example.com", jwt.Claims.First(c => c.Type == JwtRegisteredClaimNames.Email).Value);
        Assert.AreEqual(clock.UtcNow.AddMinutes(30), token.ExpiresAtUtc);
        Assert.AreEqual("SubscriptionManager", jwt.Issuer);
    }
}

[TestClass]
public class ArchitectureTests
{
    private const string ModulesPrefix = "SubscriptionManager.Modules.";

    /// <summary>
    /// Правило модульного монолита: типы одного модуля могут зависеть от типов другого модуля
    /// (через параметры конструктора) только если те лежат в его пространстве имён Contracts.
    /// </summary>
    [TestMethod]
    public void Modules_DependOnEachOther_OnlyThroughContracts()
    {
        var violations = new List<string>();
        var types = typeof(Program).Assembly.GetTypes().Where(t => t.Namespace?.StartsWith(ModulesPrefix) == true);

        foreach (var type in types)
        {
            var module = type.Namespace!.Substring(ModulesPrefix.Length).Split('.')[0];
            var dependencies = type.GetConstructors().SelectMany(c => c.GetParameters()).Select(p => p.ParameterType)
                .SelectMany(t => t.IsGenericType ? t.GetGenericArguments().Append(t) : new[] { t });

            foreach (var dep in dependencies.Where(d => d.Namespace?.StartsWith(ModulesPrefix) == true))
            {
                var depModule = dep.Namespace!.Substring(ModulesPrefix.Length).Split('.')[0];
                if (depModule != module && !dep.Namespace.EndsWith(".Contracts"))
                    violations.Add($"{type.FullName} -> {dep.FullName}");
            }
        }

        Assert.AreEqual(0, violations.Count, string.Join(Environment.NewLine, violations));
    }

    [TestMethod]
    public void Domain_DoesNotDependOnInfrastructureOrAspNet()
    {
        var domainTypes = typeof(Program).Assembly.GetTypes()
            .Where(t => t.Namespace?.StartsWith(ModulesPrefix) == true && t.Namespace.EndsWith(".Domain"));

        foreach (var type in domainTypes)
        {
            var referenced = type.GetProperties().Select(p => p.PropertyType.Namespace ?? "");
            Assert.IsFalse(referenced.Any(ns => ns.StartsWith("Microsoft.")), $"{type.Name} зависит от инфраструктуры");
        }
    }
}
