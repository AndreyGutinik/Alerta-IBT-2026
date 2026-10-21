using System.Text;
using AlertaIBT.Core.Settings;

namespace AlertaIBT.Core.Tests.Settings;

#pragma warning disable CA1416 // Windows-only app: DPAPI is called directly to prove the entropy binds the secret to Alerta-IBT.

public class SecretProtectorTests
{
    private static readonly DpapiSecretProtector Protector = new();

    [Fact]
    public void A_secret_survives_the_round_trip()
    {
        var protectedValue = Protector.Protect("123456:ABC-def_GHI");

        Assert.Equal("123456:ABC-def_GHI", Protector.Unprotect(protectedValue));
    }

    [Fact]
    public void The_protected_form_is_base64_and_does_not_hold_the_secret_in_clear()
    {
        const string secret = "AIzaSyD-this-is-a-fake-key-0123456789";

        var protectedValue = Protector.Protect(secret);

        Assert.NotNull(protectedValue);
        var bytes = Convert.FromBase64String(protectedValue);
        Assert.DoesNotContain(secret, protectedValue);
        Assert.DoesNotContain(secret, Encoding.UTF8.GetString(bytes));
        Assert.DoesNotContain(secret, Encoding.Unicode.GetString(bytes));
    }

    [Fact]
    public void A_secret_with_accents_and_emoji_survives()
    {
        const string secret = "chave-\U000000E7\U000000E3o-\U0001F511";

        Assert.Equal(secret, Protector.Unprotect(Protector.Protect(secret)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Nothing_to_protect_gives_null(string? blank)
    {
        Assert.Null(Protector.Protect(blank));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("this is not base64!")]
    [InlineData("AAAA")]
    public void What_cannot_be_unprotected_gives_null_and_never_throws(string? garbage)
    {
        Assert.Null(Protector.Unprotect(garbage));
    }

    [Fact]
    public void A_tampered_value_gives_null()
    {
        var bytes = Convert.FromBase64String(Protector.Protect("secret")!);
        bytes[^1] ^= 0xFF;
        bytes[bytes.Length / 2] ^= 0xFF;

        Assert.Null(Protector.Unprotect(Convert.ToBase64String(bytes)));
    }

    [Fact]
    public void Secrets_are_bound_to_this_application_not_just_to_the_user()
    {
        // Another program running as the same user using plain DPAPI (no entropy) cannot read it.
        var protectedBytes = Convert.FromBase64String(Protector.Protect("secret")!);

        Assert.Throws<System.Security.Cryptography.CryptographicException>(() =>
            System.Security.Cryptography.ProtectedData.Unprotect(
                protectedBytes,
                null,
                System.Security.Cryptography.DataProtectionScope.CurrentUser));
    }
}

#pragma warning restore CA1416
