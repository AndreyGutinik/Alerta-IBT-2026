using System.Text;
using AlertaIBT.Core.Settings;

namespace AlertaIBT.Core.Tests.Settings;

// Reversible but not readable at a glance, so a test can tell the clear text from the protected one without Windows DPAPI.
internal sealed class FakeProtector : ISecretProtector
{
    public string? Protect(string? plain) =>
        string.IsNullOrWhiteSpace(plain) ? null : Convert.ToBase64String(Encoding.UTF8.GetBytes(plain.Reverse().ToArray()));

    public string? Unprotect(string? protectedValue)
    {
        if (string.IsNullOrWhiteSpace(protectedValue))
        {
            return null;
        }

        try
        {
            return new string(Encoding.UTF8.GetString(Convert.FromBase64String(protectedValue)).Reverse().ToArray());
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
