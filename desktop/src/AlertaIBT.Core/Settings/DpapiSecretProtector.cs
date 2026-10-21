using System.Security.Cryptography;
using System.Text;

namespace AlertaIBT.Core.Settings;

// The app only runs on Windows (WPF, Velopack). Core stays net10.0 so it can be tested anywhere; on another OS the calls
// below throw PlatformNotSupportedException, which Unprotect turns into null and Protect lets through.
#pragma warning disable CA1416

/// <summary>
/// Windows DPAPI for the current user: the protected text (base64) can be read back only by the same Windows account on
/// the same machine, so copying <c>settings.json</c> elsewhere does not leak the secrets. A fixed, application specific
/// entropy keeps other programs of the same user from reading it with plain DPAPI.
/// </summary>
public sealed class DpapiSecretProtector : ISecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Alerta-IBT/v8/settings-secrets");

    /// <inheritdoc />
    public string? Protect(string? plain)
    {
        if (string.IsNullOrWhiteSpace(plain))
        {
            return null;
        }

        var clear = Encoding.UTF8.GetBytes(plain);
        try
        {
            return Convert.ToBase64String(ProtectedData.Protect(clear, Entropy, DataProtectionScope.CurrentUser));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clear);
        }
    }

    /// <inheritdoc />
    public string? Unprotect(string? protectedValue)
    {
        if (string.IsNullOrWhiteSpace(protectedValue))
        {
            return null;
        }

        try
        {
            var clear = ProtectedData.Unprotect(Convert.FromBase64String(protectedValue.Trim()), Entropy, DataProtectionScope.CurrentUser);
            try
            {
                var text = Encoding.UTF8.GetString(clear);
                return string.IsNullOrWhiteSpace(text) ? null : text;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(clear);
            }
        }
        catch (Exception)
        {
            // Not base64, damaged, another user or machine, or not Windows: there is no secret to give.
            return null;
        }
    }
}

#pragma warning restore CA1416
