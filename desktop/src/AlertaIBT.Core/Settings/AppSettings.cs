using AlertaIBT.Core.Correction;

namespace AlertaIBT.Core.Settings;

/// <summary>
/// Everything the user can set, saved to <c>%AppData%\AlertaIBT\settings.json</c>. The defaults are the ones of the
/// spec. Secrets are only ever held in their DPAPI-protected form (see <see cref="SettingsSecrets"/>). Values are held
/// inside their ranges by <see cref="SettingsJson.Normalize"/>, which loading and saving apply.
/// </summary>
public sealed record AppSettings
{
    /// <summary>The scrolling bar on the LED.</summary>
    public BarSettings Bar { get; init; } = new();

    /// <summary>The Telegram bot.</summary>
    public TelegramSettings Telegram { get; init; } = new();

    /// <summary>The spelling correction.</summary>
    public CorrectionSettings Correction { get; init; } = new();

    /// <summary>The Firebase site: how notices sent from the phones reach this PC.</summary>
    public SiteSettings Site { get; init; } = new();

    /// <summary>The panel shortcut and the remembered LED.</summary>
    public SystemSettings System { get; init; } = new();
}

/// <summary>How the bar looks and moves.</summary>
public sealed record BarSettings
{
    /// <summary>Lowest bar height, in pixels.</summary>
    public const int MinHeight = 28;

    /// <summary>Highest bar height, in pixels.</summary>
    public const int MaxHeight = 120;

    /// <summary>Slowest scroll, in pixels per second.</summary>
    public const int MinSpeed = 30;

    /// <summary>Fastest scroll, in pixels per second.</summary>
    public const int MaxSpeed = 300;

    /// <summary>Smallest explicit font size, in pixels.</summary>
    public const int MinFontSize = 8;

    /// <summary>Largest explicit font size, in pixels.</summary>
    public const int MaxFontSize = 200;

    /// <summary>Shortest slide in or out, in milliseconds.</summary>
    public const int MinSlideMs = 100;

    /// <summary>Longest slide in or out, in milliseconds.</summary>
    public const int MaxSlideMs = 1500;

    /// <summary>Largest side margin, in pixels.</summary>
    public const int MaxPaddingX = 200;

    /// <summary>Where the bar sits: <c>top</c> or <c>bottom</c>.</summary>
    public string Position { get; init; } = "top";

    /// <summary>The bar height in pixels, 28 to 120.</summary>
    public int Height { get; init; } = 56;

    /// <summary>The font size in pixels, or <c>null</c> for automatic (64% of the height).</summary>
    public int? FontSize { get; init; }

    /// <summary>The scroll speed in pixels per second, 30 to 300.</summary>
    public int Speed { get; init; } = 90;

    /// <summary>The bar color, <c>#RRGGBB</c>.</summary>
    public string Background { get; init; } = "#000000";

    /// <summary>The text color, <c>#RRGGBB</c>.</summary>
    public string Foreground { get; init; } = "#FFF7E8";

    /// <summary>How long the bar takes to slide in and out, in milliseconds.</summary>
    public int SlideMs { get; init; } = 320;

    /// <summary>The side margin in pixels.</summary>
    public int PaddingX { get; init; } = 24;
}

/// <summary>The Telegram bot's settings.</summary>
public sealed record TelegramSettings
{
    /// <summary>The group's chat id (negative for groups); 0 means none yet.</summary>
    public long ChatId { get; init; }

    /// <summary>The bot token, protected with DPAPI (base64); <c>null</c> when none.</summary>
    public string? TokenProtected { get; init; }

    /// <summary>The <c>getUpdates</c> offset the poller reached, so a restart does not repeat messages.</summary>
    public long Offset { get; init; }
}

/// <summary>The connection to the Firebase project of the site (the PC signs in with its own e-mail and password account).</summary>
public sealed record SiteSettings
{
    /// <summary>Whether the PC connects to the site.</summary>
    public bool Enabled { get; init; }

    /// <summary>The Realtime Database address (<c>https://...firebaseio.com</c> or <c>...firebasedatabase.app</c>); blank when none.</summary>
    public string DatabaseUrl { get; init; } = string.Empty;

    /// <summary>The web API key of the project (it is public by design: it only names the project); blank when none.</summary>
    public string ApiKey { get; init; } = string.Empty;

    /// <summary>The Firebase project id (optional); blank when none.</summary>
    public string ProjectId { get; init; } = string.Empty;

    /// <summary>The e-mail of the PC own account.</summary>
    public string DeviceEmail { get; init; } = string.Empty;

    /// <summary>The password of the PC account, protected with DPAPI (base64); <c>null</c> when none.</summary>
    public string? PasswordProtected { get; init; }

    /// <summary>
    /// Whether <paramref name="url"/> may be the database address: <c>https</c> and a host that ends with <c>firebaseio.com</c> or
    /// <c>firebasedatabase.app</c>, or an <c>http(s)</c> address on this computer (the emulator, for tests).
    /// </summary>
    /// <param name="url">The address as typed.</param>
    public static bool IsValidDatabaseUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.UserInfo.Length > 0)
        {
            return false;
        }

        if (uri.IsLoopback)
        {
            return uri.Scheme is "http" or "https";
        }

        if (uri.Scheme != "https")
        {
            return false;
        }

        var host = uri.Host.ToLowerInvariant();
        return host.EndsWith(".firebaseio.com", StringComparison.Ordinal) || host.EndsWith(".firebasedatabase.app", StringComparison.Ordinal);
    }

    /// <summary>Whether everything the connection needs is filled in.</summary>
    public bool IsConfigured =>
        IsValidDatabaseUrl(DatabaseUrl) && ApiKey.Length > 0 && DeviceEmail.Length > 0 && PasswordProtected is not null;
}

/// <summary>The correction's settings.</summary>
public sealed record CorrectionSettings
{
    /// <summary>Whether alerts are corrected at all.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary><c>gemini</c> (with the offline corrector as the fallback) or <c>offline</c>.</summary>
    public string Mode { get; init; } = "gemini";

    /// <summary>The Gemini API key, protected with DPAPI (base64); <c>null</c> when none.</summary>
    public string? GeminiKeyProtected { get; init; }

    /// <summary>The Gemini model id.</summary>
    public string GeminiModel { get; init; } = GeminiPrompt.DefaultModel;

    /// <summary>The fixed rules, one per line (see <see cref="FixedRules"/>).</summary>
    public string FixedRules { get; init; } = string.Empty;
}

/// <summary>The remaining settings.</summary>
public sealed record SystemSettings
{
    /// <summary>The shortcut that opens the quick panel.</summary>
    public string Hotkey { get; init; } = "Ctrl+Alt+Space";

    /// <summary>The device path of the LED screen used last, to prefer it when there are two.</summary>
    public string? LastLedDevicePath { get; init; }

    /// <summary>Whether the history is cleared by itself every Monday at 00:00 (local time), and when the PC was off, at the next start.</summary>
    public bool ClearHistoryWeekly { get; init; } = true;

    /// <summary>
    /// When the history was last cleared (or, on the first run and while the switch is off, when the app last looked and had nothing
    /// to clear), or <c>null</c> before the first run. A Monday 00:00 that passed after this moment is still owed.
    /// </summary>
    public DateTimeOffset? LastHistoryClearAt { get; init; }

    /// <summary>Whether the site still has to be cleared: the history was cleared while the site was offline.</summary>
    public bool SitePendingClear { get; init; }
}
