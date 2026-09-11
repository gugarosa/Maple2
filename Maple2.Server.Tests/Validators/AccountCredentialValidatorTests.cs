using System;
using System.Linq;
using Maple2.Model.Validators;

namespace Maple2.Server.Tests.Validators;

public class AccountCredentialValidatorTests {
    [TestCase(" Alice_1 ", "alice_1")]
    [TestCase("", "")]
    [TestCase(null, "")]
    public void UsernameNormalizationIsExplicitAndInvariant(string? input, string expected) {
        Assert.That(AccountCredentialValidator.NormalizeUsername(input), Is.EqualTo(expected));
    }

    [TestCase("player_1", true)]
    [TestCase("ab", false)]
    [TestCase("player name", false)]
    [TestCase("player@example.com", false)]
    [TestCase("player\n", false)]
    public void RegistrationAcceptsOnlySupportedUsernames(string username, bool expected) {
        Assert.That(AccountCredentialValidator.ValidRegistrationUsername(username), Is.EqualTo(expected));
    }

    [Test]
    public void PasswordLimitsUseUtf8BytesWithoutTruncation() {
        Assert.That(AccountCredentialValidator.ValidRegistrationPassword(new string('x', 16)), Is.True);
        Assert.That(AccountCredentialValidator.ValidRegistrationPassword(new string('x', 17)), Is.False);
        Assert.That(AccountCredentialValidator.ValidLoginPassword(new string('x', 72)), Is.True);
        Assert.That(AccountCredentialValidator.ValidLoginPassword(new string('x', 73)), Is.False);
        string rune = char.ConvertFromUtf32(0x1f341);
        Assert.That(AccountCredentialValidator.ValidRegistrationPassword(string.Concat(Enumerable.Repeat(rune, 8))), Is.True);
        Assert.That(AccountCredentialValidator.ValidLoginPassword(string.Concat(Enumerable.Repeat(rune, 18))), Is.True);
        Assert.That(AccountCredentialValidator.ValidLoginPassword(string.Concat(Enumerable.Repeat(rune, 19))), Is.False);
        Assert.That(AccountCredentialValidator.ValidRegistrationPassword(string.Concat(Enumerable.Repeat(rune, 4))), Is.False);
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("        ")]
    [TestCase("secret\0value")]
    public void EmptyOrTerminatedPasswordsCannotAuthenticate(string? password) {
        Assert.That(AccountCredentialValidator.ValidLoginPassword(password), Is.False);
        Assert.That(AccountCredentialValidator.ValidRegistrationPassword(password), Is.False);
    }

    [Test]
    public void NewPasswordPolicyDoesNotRejectExistingNonemptyShortPasswords() {
        Assert.That(AccountCredentialValidator.ValidLoginPassword("old1"), Is.True);
        Assert.That(AccountCredentialValidator.ValidRegistrationPassword("old1"), Is.False);
        Assert.That(AccountCredentialValidator.ValidRegistrationPassword(" exact password "), Is.True);
    }
}
