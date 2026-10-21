namespace AlertaIBT.Core.Settings;

/// <summary>Turns a secret (the Telegram token, the Gemini key) into text that is safe to keep in <c>settings.json</c>, and back.</summary>
public interface ISecretProtector
{
    /// <summary>The protected form of <paramref name="plain"/>, or <c>null</c> when there is nothing to protect (null or blank).</summary>
    string? Protect(string? plain);

    /// <summary>
    /// The secret inside <paramref name="protectedValue"/>, or <c>null</c> when there is none or it cannot be read
    /// (damaged, or made by another Windows user or machine). Never throws.
    /// </summary>
    string? Unprotect(string? protectedValue);
}
