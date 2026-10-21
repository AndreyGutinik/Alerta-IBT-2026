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
}
