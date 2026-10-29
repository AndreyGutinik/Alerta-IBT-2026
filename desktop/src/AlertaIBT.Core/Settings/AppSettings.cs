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

    /// <summary>The mouse lock: the cursor stays on the primary monitor, and Ctrl pressed 3 times brings it back to its center.</summary>
    public MouseLockSettings MouseLock { get; init; } = new();

    /// <summary>The sound: volumes set when the PC starts, and the lock of the Windows output on one device.</summary>
    public AudioSettings Audio { get; init; } = new();

    /// <summary>The music player: playlists, alarms, fades, output and volume.</summary>
    public PlayerSettings Player { get; init; } = new();

    /// <summary>The NDI camera on the telao (receive only).</summary>
    public NdiSettings Ndi { get; init; } = new();
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

/// <summary>The mouse lock (the "bloqueador de mouse"). Every field is optional in <c>settings.json</c>: an older file loads with the defaults.</summary>
public sealed record MouseLockSettings
{
    /// <summary>The shortcut that turns the lock on and off, unless the saved one is not a valid shortcut.</summary>
    public const string DefaultHotkey = "Ctrl+Alt+M";

    /// <summary>Whether the cursor is locked to the primary monitor (restored when the app starts). Off by default.</summary>
    public bool Enabled { get; init; }

    /// <summary>The shortcut that toggles the lock, as text such as <c>Ctrl+Alt+M</c> (same form as <see cref="SystemSettings.Hotkey"/>).</summary>
    public string Hotkey { get; init; } = DefaultHotkey;

    /// <summary>Whether pressing Ctrl three times quickly sends the cursor to the center of the primary monitor (works with the lock on or off). On by default.</summary>
    public bool TripleCtrl { get; init; } = true;
}

/// <summary>One program whose volume is set when its audio session appears. Data, not code: a later "Adicionar programa" only adds entries.</summary>
/// <param name="Name">The name shown on the screen and in the log (<c>Holyrics</c>).</param>
/// <param name="ProcessNames">The process names that belong to the program, any case, with or without <c>.exe</c>.</param>
/// <param name="Volume">The volume to set, 0 to 100.</param>
/// <param name="Muted">Whether it starts muted.</param>
public sealed record AudioAppEntry(string Name, IReadOnlyList<string> ProcessNames, int Volume, bool Muted)
{
    /// <summary>Value equality, with the process names compared one by one (a list is otherwise compared by reference).</summary>
    public bool Equals(AudioAppEntry? other) =>
        other is not null && Name == other.Name && Volume == other.Volume && Muted == other.Muted && ProcessNames.SequenceEqual(other.ProcessNames);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Name, Volume, Muted, ProcessNames.Count);
}

/// <summary>
/// The "Som" tab. Every field is optional in <c>settings.json</c>: an older file loads with the defaults. The app never plays sound; it only
/// sets Windows audio state (volumes, mutes, which output is the default).
/// </summary>
public sealed record AudioSettings
{
    /// <summary>Lowest volume.</summary>
    public const int MinVolume = 0;

    /// <summary>Highest volume.</summary>
    public const int MaxVolume = 100;

    /// <summary>Value equality, with the programs compared one by one (a list is otherwise compared by reference).</summary>
    public bool Equals(AudioSettings? other) =>
        other is not null
        && Enabled == other.Enabled
        && MasterVolume == other.MasterVolume
        && MasterMuted == other.MasterMuted
        && SystemSoundsVolume == other.SystemSoundsVolume
        && SystemSoundsMuted == other.SystemSoundsMuted
        && LockOutput == other.LockOutput
        && OutputDeviceId == other.OutputDeviceId
        && OutputDeviceName == other.OutputDeviceName
        && OutputMatch.SequenceEqual(other.OutputMatch)
        && HolyricsRoute == other.HolyricsRoute
        && HolyricsDeviceId == other.HolyricsDeviceId
        && HolyricsDeviceName == other.HolyricsDeviceName
        && HolyricsMatch.SequenceEqual(other.HolyricsMatch)
        && Apps.SequenceEqual(other.Apps);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Enabled, MasterVolume, SystemSoundsVolume, LockOutput, OutputDeviceId, Apps.Count);

    /// <summary>The text that picks the output when none was chosen yet: the name of the Behringer WING USB contains it.</summary>
    public const string DefaultOutputMatch = "WING";

    /// <summary>The words of the default name rule of the Windows default output: <c>WING</c>.</summary>
    public static readonly IReadOnlyList<string> DefaultOutputWords = [DefaultOutputMatch];

    /// <summary>The words of the default name rule of the Holyrics output: <c>WING</c> and <c>1-2</c> (the church's "WING-USB (Out 1-2)").</summary>
    public static readonly IReadOnlyList<string> DefaultHolyricsWords = [DefaultOutputMatch, "1-2"];

    /// <summary>The programs the screen does not list (their settings are kept; the player took the place of the Spotify row).</summary>
    public static readonly IReadOnlyList<string> HiddenApps = ["Spotify"];

    /// <summary>Whether the volumes are set when the PC starts (and when a program opens within 30 minutes). On by default.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>The Windows master volume of the output, 0 to 100 (80 by default).</summary>
    public int MasterVolume { get; init; } = 80;

    /// <summary>Whether the master starts muted (no by default).</summary>
    public bool MasterMuted { get; init; }

    /// <summary>The volume of "Sons do Windows" (notifications and such, the "System sounds" entry of the mixer), 0 by default.</summary>
    public int SystemSoundsVolume { get; init; }

    /// <summary>Whether "Sons do Windows" starts muted (no by default).</summary>
    public bool SystemSoundsMuted { get; init; }

    /// <summary>The programs and the volume each one gets.</summary>
    public IReadOnlyList<AudioAppEntry> Apps { get; init; } = DefaultApps();

    /// <summary>Whether Windows is kept from changing the default output away from <see cref="OutputDeviceId"/>. On by default.</summary>
    public bool LockOutput { get; init; } = true;

    /// <summary>The endpoint id of the chosen output, or <c>null</c> until one was found (the first one whose name contains <see cref="DefaultOutputMatch"/>).</summary>
    public string? OutputDeviceId { get; init; }

    /// <summary>The friendly name of the chosen output (matched when the id changes, for example on another USB port).</summary>
    public string? OutputDeviceName { get; init; }

    /// <summary>The words that find the default output by name when the id is not active (all must be in the name). Default <c>WING</c>.</summary>
    public IReadOnlyList<string> OutputMatch { get; init; } = DefaultOutputWords;

    /// <summary>"Definir a saida do Holyrics": the Windows per-app output of Holyrics is set to the Holyrics output. With it off the app never touches that setting. On by default.</summary>
    public bool HolyricsRoute { get; init; } = true;

    /// <summary>The endpoint id of the Holyrics output, when known (filled in when the output is found).</summary>
    public string? HolyricsDeviceId { get; init; }

    /// <summary>The friendly name of the Holyrics output, for the screen (filled in when the output is found).</summary>
    public string? HolyricsDeviceName { get; init; }

    /// <summary>The words that find the Holyrics output by name when the id is not active (all must be in the name). Default <c>WING</c> + <c>1-2</c>.</summary>
    public IReadOnlyList<string> HolyricsMatch { get; init; } = DefaultHolyricsWords;

    /// <summary>
    /// Holyrics and Spotify at 100%, unmuted. Holyrics is a Launch4j program: the visible Holyrics.exe starts the bundled Java
    /// (the jre folder inside the Holyrics folder), so the sound comes from <c>javaw</c> on a PC that only runs Holyrics as a Java program.
    /// </summary>
    public static IReadOnlyList<AudioAppEntry> DefaultApps() =>
    [
        new AudioAppEntry("Holyrics", ["Holyrics", "javaw"], 100, false),
        new AudioAppEntry("Spotify", ["Spotify"], 100, false),
    ];
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

    /// <summary>
    /// "Alertas ligados": the master switch of the alert part of the app (the bar on the LED, Telegram, the site and the overlay API). Off
    /// is for a smaller temple that only wants the sound. On by default, and for an older settings file.
    /// </summary>
    public bool AlertsEnabled { get; init; } = true;

    /// <summary>Whether the app looks for a newer version when it opens (and every 30 minutes) and installs it, restarting, when it is idle.</summary>
    public bool AutoUpdate { get; init; } = true;

    /// <summary>The version whose installation was attempted last, or <c>null</c> before the first attempt (see <see cref="UpdateAttemptPolicy"/>).</summary>
    public string? UpdateAttemptVersion { get; init; }

    /// <summary>When the first attempt for <see cref="UpdateAttemptVersion"/> of the current 24-hour window was made.</summary>
    public DateTimeOffset? UpdateAttemptSince { get; init; }

    /// <summary>How many attempts were made for <see cref="UpdateAttemptVersion"/> since <see cref="UpdateAttemptSince"/>.</summary>
    public int UpdateAttempts { get; init; }
}
