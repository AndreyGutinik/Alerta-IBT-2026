namespace AlertaIBT.Core.Settings;

/// <summary>
/// The only way the secrets cross the border between clear text and <see cref="AppSettings"/>: settings hold the
/// protected form only, so neither saving nor printing them can leak a token or a key.
/// </summary>
public static class SettingsSecrets
{
    /// <summary>The Telegram bot token in clear, or <c>null</c> when there is none or it cannot be read.</summary>
    public static string? GetTelegramToken(this AppSettings settings, ISecretProtector protector)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(protector);

        return protector.Unprotect(settings.Telegram.TokenProtected);
    }

    /// <summary>The Gemini API key in clear, or <c>null</c> when there is none or it cannot be read.</summary>
    public static string? GetGeminiKey(this AppSettings settings, ISecretProtector protector)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(protector);

        return protector.Unprotect(settings.Correction.GeminiKeyProtected);
    }

    /// <summary>The password of the PC Firebase account in clear, or <c>null</c> when there is none or it cannot be read.</summary>
    public static string? GetSitePassword(this AppSettings settings, ISecretProtector protector)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(protector);

        return protector.Unprotect(settings.Site.PasswordProtected);
    }

    /// <summary>
    /// A copy with the password of the PC Firebase account set (not trimmed: a password may start or end with a space) or, when
    /// <paramref name="password"/> is empty, cleared.
    /// </summary>
    public static AppSettings WithSitePassword(this AppSettings settings, string? password, ISecretProtector protector)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(protector);

        return settings with { Site = settings.Site with { PasswordProtected = protector.Protect(string.IsNullOrEmpty(password) ? null : password) } };
    }

    /// <summary>
    /// A copy with the Telegram token set (trimmed) or, when <paramref name="token"/> is blank, cleared. A different token is a
    /// different bot, whose updates are numbered on their own: the saved <c>getUpdates</c> offset goes back to 0 in the same change
    /// (the same token set again keeps it).
    /// </summary>
    public static AppSettings WithTelegramToken(this AppSettings settings, string? token, ISecretProtector protector)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(protector);

        var next = string.IsNullOrWhiteSpace(token) ? null : token.Trim();
        var changed = !string.Equals(next, settings.GetTelegramToken(protector), StringComparison.Ordinal);
        return settings with
        {
            Telegram = settings.Telegram with
            {
                TokenProtected = protector.Protect(next),
                Offset = changed ? 0 : settings.Telegram.Offset,
            },
        };
    }

    /// <summary>A copy with the Gemini key set (trimmed) or, when <paramref name="key"/> is blank, cleared.</summary>
    public static AppSettings WithGeminiKey(this AppSettings settings, string? key, ISecretProtector protector)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(protector);

        return settings with { Correction = settings.Correction with { GeminiKeyProtected = protector.Protect(key?.Trim()) } };
    }
}
