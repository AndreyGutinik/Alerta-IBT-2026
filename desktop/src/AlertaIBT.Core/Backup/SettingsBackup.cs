using System.Globalization;
using System.Text.Json;
using AlertaIBT.Core.Input;
using AlertaIBT.Core.Settings;

namespace AlertaIBT.Core.Backup;

/// <summary>
/// The settings a backup holds, with the secrets in clear (the file that carries it is encrypted, see <see cref="BackupCrypto"/>). This is the
/// whole list of what a backup keeps; nothing else of <see cref="AppSettings"/> is ever read or written by a backup. Never log or print it:
/// <see cref="ToString"/> hides the secrets.
/// </summary>
public sealed record BackupPayload
{
    /// <summary>The content format of this record (not the file format).</summary>
    public const int CurrentVersion = 1;

    /// <summary>The content format.</summary>
    public int Version { get; init; } = CurrentVersion;

    /// <summary>When the backup was made (UTC, ISO 8601).</summary>
    public string ExportedAt { get; init; } = string.Empty;

    /// <summary>The Telegram bot token in clear, or blank.</summary>
    public string TelegramToken { get; init; } = string.Empty;

    /// <summary>The Telegram chat id (0 for none).</summary>
    public long TelegramChatId { get; init; }

    /// <summary>The Gemini API key in clear, or blank.</summary>
    public string GeminiKey { get; init; } = string.Empty;

    /// <summary>The Gemini model name.</summary>
    public string GeminiModel { get; init; } = string.Empty;

    /// <summary>The quick-panel shortcut.</summary>
    public string PanelHotkey { get; init; } = string.Empty;

    /// <summary>The mouse-lock shortcut.</summary>
    public string MouseLockHotkey { get; init; } = string.Empty;

    /// <summary>Whether Ctrl pressed three times centers the cursor.</summary>
    public bool TripleCtrl { get; init; }

    /// <summary>Whether the NDI shortcuts (F1-F4, Ctrl+Space) are on.</summary>
    public bool NdiShortcuts { get; init; }

    /// <summary>Whether the media keys drive the player.</summary>
    public bool MediaKeys { get; init; }

    /// <summary>The bar position, <c>top</c> or <c>bottom</c>.</summary>
    public string BarPosition { get; init; } = "top";

    /// <summary>The bar height in pixels.</summary>
    public int BarHeight { get; init; }

    /// <summary>The bar speed in pixels per second.</summary>
    public int BarSpeed { get; init; }

    /// <summary>The device path of the chosen LED monitor, or blank.</summary>
    public string LedDevicePath { get; init; } = string.Empty;

    /// <summary>The site database address.</summary>
    public string SiteDatabaseUrl { get; init; } = string.Empty;

    /// <summary>The site web API key.</summary>
    public string SiteApiKey { get; init; } = string.Empty;

    /// <summary>The e-mail of the PC account of the site.</summary>
    public string SiteEmail { get; init; } = string.Empty;

    /// <summary>The password of the PC account of the site, in clear, or blank.</summary>
    public string SitePassword { get; init; } = string.Empty;

    /// <summary>The fixed correction rules text.</summary>
    public string FixedRules { get; init; } = string.Empty;

    /// <summary>"Alertas ligados".</summary>
    public bool AlertsEnabled { get; init; }

    /// <summary>The weekly history clear.</summary>
    public bool ClearHistoryWeekly { get; init; }

    /// <summary>Hides every secret and every long text.</summary>
    public override string ToString() => $"BackupPayload v{Version} ({ExportedAt})";
}

/// <summary>One line of the preview: an item of the backup, what it is now and what it would become.</summary>
/// <param name="Label">The item name, pt-BR.</param>
/// <param name="Old">The value now, ready to show (secrets masked).</param>
/// <param name="New">The value in the backup, ready to show (secrets masked).</param>
/// <param name="IsSecret">Whether the item is a secret (masked).</param>
/// <param name="Changed">Whether restoring changes it.</param>
public sealed record BackupChange(string Label, string Old, string New, bool IsSecret, bool Changed);

/// <summary>
/// Makes, reads and applies backups of the critical settings. <see cref="Capture"/> and <see cref="Apply"/> are the only places that know the
/// list of what a backup holds.
/// </summary>
public static class SettingsBackup
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    /// <summary>The included items, in the order of the preview, for the docs and the tests.</summary>
    public static IReadOnlyList<string> ItemLabels { get; } =
    [
        "Token do Telegram", "Grupo do Telegram", "Chave do Gemini", "Modelo do Gemini", "Atalho do painel", "Atalho do mouse",
        "Ctrl 3 vezes", "Atalhos do NDI", "Teclas de m\U000000EDdia", "Posi\U000000E7\U000000E3o da barra", "Tamanho da barra", "Velocidade da barra",
        "Tela do LED", "Endere\U000000E7o do site", "Chave da API do site", "E-mail do PC", "Senha do PC", "Regras fixas",
        "Alertas ligados", "Limpar hist\U000000F3rico toda segunda",
    ];

    /// <summary>Reads the included values out of <paramref name="settings"/>, secrets in clear.</summary>
    public static BackupPayload Capture(AppSettings settings, ISecretProtector protector, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(protector);

        return new BackupPayload
        {
            ExportedAt = now.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            TelegramToken = settings.GetTelegramToken(protector) ?? string.Empty,
            TelegramChatId = settings.Telegram.ChatId,
            GeminiKey = settings.GetGeminiKey(protector) ?? string.Empty,
            GeminiModel = settings.Correction.GeminiModel,
            PanelHotkey = settings.System.Hotkey,
            MouseLockHotkey = settings.MouseLock.Hotkey,
            TripleCtrl = settings.MouseLock.TripleCtrl,
            NdiShortcuts = settings.Ndi.ShortcutsEnabled,
            MediaKeys = settings.Player.MediaKeys,
            BarPosition = settings.Bar.Position,
            BarHeight = settings.Bar.Height,
            BarSpeed = settings.Bar.Speed,
            LedDevicePath = settings.System.LastLedDevicePath ?? string.Empty,
            SiteDatabaseUrl = settings.Site.DatabaseUrl,
            SiteApiKey = settings.Site.ApiKey,
            SiteEmail = settings.Site.DeviceEmail,
            SitePassword = settings.GetSitePassword(protector) ?? string.Empty,
            FixedRules = settings.Correction.FixedRules,
            AlertsEnabled = settings.System.AlertsEnabled,
            ClearHistoryWeekly = settings.System.ClearHistoryWeekly,
        };
    }

    /// <summary>The bytes of a <c>.ibtbackup</c> file with the included settings, encrypted with <paramref name="password"/>.</summary>
    /// <exception cref="BackupException"><see cref="BackupError.WeakPassword"/> when the password is shorter than 8 characters.</exception>
    public static byte[] Export(AppSettings settings, ISecretProtector protector, string password, DateTimeOffset now)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(Capture(settings, protector, now), Json);
        try
        {
            return BackupCrypto.Encrypt(plain, password);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(plain);
        }
    }

    /// <summary>Decrypts and checks a backup file.</summary>
    /// <exception cref="BackupException">The file or the password is not usable; the message is the pt-BR text to show.</exception>
    public static BackupPayload Read(ReadOnlySpan<byte> file, string password)
    {
        var plain = BackupCrypto.Decrypt(file, password);
        try
        {
            BackupPayload? payload;
            try
            {
                payload = JsonSerializer.Deserialize<BackupPayload>(plain, Json);
            }
            catch (JsonException ex)
            {
                throw new BackupException(BackupError.InvalidContent, ex);
            }

            if (payload is null)
            {
                throw new BackupException(BackupError.InvalidContent);
            }

            if (payload.Version > BackupPayload.CurrentVersion)
            {
                throw new BackupException(BackupError.UnsupportedVersion);
            }

            return IsUsable(payload) ? payload : throw new BackupException(BackupError.InvalidContent);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(plain);
        }
    }

    /// <summary>Whether every value of <paramref name="p"/> can be applied as it is (shortcuts parse, ranges hold, the address is a database).</summary>
    public static bool IsUsable(BackupPayload p)
    {
        ArgumentNullException.ThrowIfNull(p);

        return p.Version >= 1
            && HotkeyGesture.TryParse(p.PanelHotkey, out _)
            && HotkeyGesture.TryParse(p.MouseLockHotkey, out _)
            && p.BarPosition is "top" or "bottom"
            && p.BarHeight is >= BarSettings.MinHeight and <= BarSettings.MaxHeight
            && p.BarSpeed is >= BarSettings.MinSpeed and <= BarSettings.MaxSpeed
            && (p.SiteDatabaseUrl.Length == 0 || SiteSettings.IsValidDatabaseUrl(p.SiteDatabaseUrl))
            && p.GeminiModel.Trim().Length > 0
            && p.FixedRules is not null;
    }

    /// <summary>
    /// A copy of <paramref name="settings"/> with the included values of <paramref name="payload"/> in place and everything else untouched (the
    /// secrets go through the same helpers the Configura\U000000E7\U000000F5es tab uses, so a new Telegram token also sends its <c>getUpdates</c> offset
    /// back to 0, as it does when typed).
    /// </summary>
    public static AppSettings Apply(AppSettings settings, BackupPayload payload, ISecretProtector protector)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(protector);

        var s = settings
            .WithTelegramToken(payload.TelegramToken, protector)
            .WithGeminiKey(payload.GeminiKey, protector)
            .WithSitePassword(payload.SitePassword, protector);
        return s with
        {
            Telegram = s.Telegram with { ChatId = payload.TelegramChatId },
            Correction = s.Correction with { GeminiModel = payload.GeminiModel.Trim(), FixedRules = payload.FixedRules },
            System = s.System with
            {
                Hotkey = payload.PanelHotkey,
                LastLedDevicePath = payload.LedDevicePath.Length == 0 ? null : payload.LedDevicePath,
                AlertsEnabled = payload.AlertsEnabled,
                ClearHistoryWeekly = payload.ClearHistoryWeekly,
            },
            MouseLock = s.MouseLock with { Hotkey = payload.MouseLockHotkey, TripleCtrl = payload.TripleCtrl },
            Ndi = s.Ndi with { ShortcutsEnabled = payload.NdiShortcuts },
            Player = s.Player with { MediaKeys = payload.MediaKeys },
            Bar = s.Bar with { Position = payload.BarPosition, Height = payload.BarHeight, Speed = payload.BarSpeed },
            Site = s.Site with { DatabaseUrl = payload.SiteDatabaseUrl, ApiKey = payload.SiteApiKey, DeviceEmail = payload.SiteEmail },
        };
    }

    /// <summary>
    /// One line per included item with what it is now and what the backup would make it. Secrets are masked (first 3 characters and dots), the
    /// rules text is shown as a count, the LED screen as chosen or not.
    /// </summary>
    public static IReadOnlyList<BackupChange> Preview(AppSettings current, BackupPayload payload, ISecretProtector protector)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(protector);

        var now = Capture(current, protector, DateTimeOffset.UnixEpoch);
        var labels = ItemLabels;
        return
        [
            Secret(labels[0], now.TelegramToken, payload.TelegramToken),
            Plain(labels[1], now.TelegramChatId.ToString(CultureInfo.InvariantCulture), payload.TelegramChatId.ToString(CultureInfo.InvariantCulture)),
            Secret(labels[2], now.GeminiKey, payload.GeminiKey),
            Plain(labels[3], now.GeminiModel, payload.GeminiModel),
            Plain(labels[4], now.PanelHotkey, payload.PanelHotkey),
            Plain(labels[5], now.MouseLockHotkey, payload.MouseLockHotkey),
            Plain(labels[6], OnOff(now.TripleCtrl), OnOff(payload.TripleCtrl), now.TripleCtrl != payload.TripleCtrl),
            Plain(labels[7], OnOff(now.NdiShortcuts), OnOff(payload.NdiShortcuts), now.NdiShortcuts != payload.NdiShortcuts),
            Plain(labels[8], OnOff(now.MediaKeys), OnOff(payload.MediaKeys), now.MediaKeys != payload.MediaKeys),
            Plain(labels[9], Side(now.BarPosition), Side(payload.BarPosition), now.BarPosition != payload.BarPosition),
            Plain(labels[10], now.BarHeight.ToString(CultureInfo.InvariantCulture) + " px", payload.BarHeight.ToString(CultureInfo.InvariantCulture) + " px", now.BarHeight != payload.BarHeight),
            Plain(labels[11], now.BarSpeed.ToString(CultureInfo.InvariantCulture) + " px/s", payload.BarSpeed.ToString(CultureInfo.InvariantCulture) + " px/s", now.BarSpeed != payload.BarSpeed),
            Plain(labels[12], Chosen(now.LedDevicePath), Chosen(payload.LedDevicePath), now.LedDevicePath != payload.LedDevicePath),
            Plain(labels[13], Blank(now.SiteDatabaseUrl), Blank(payload.SiteDatabaseUrl), now.SiteDatabaseUrl != payload.SiteDatabaseUrl),
            Secret(labels[14], now.SiteApiKey, payload.SiteApiKey),
            Plain(labels[15], Blank(now.SiteEmail), Blank(payload.SiteEmail), now.SiteEmail != payload.SiteEmail),
            Secret(labels[16], now.SitePassword, payload.SitePassword),
            Plain(labels[17], RuleCount(now.FixedRules), RuleCount(payload.FixedRules), now.FixedRules != payload.FixedRules),
            Plain(labels[18], OnOff(now.AlertsEnabled), OnOff(payload.AlertsEnabled), now.AlertsEnabled != payload.AlertsEnabled),
            Plain(labels[19], OnOff(now.ClearHistoryWeekly), OnOff(payload.ClearHistoryWeekly), now.ClearHistoryWeekly != payload.ClearHistoryWeekly),
        ];
    }

    /// <summary>A secret as the preview shows it: the first 3 characters and dots, <c>(vazio)</c> for none. A short secret shows only dots.</summary>
    public static string Mask(string? secret)
    {
        if (string.IsNullOrEmpty(secret))
        {
            return "(vazio)";
        }

        const string Dots = "\U00002022\U00002022\U00002022\U00002022\U00002022\U00002022";
        return secret.Length <= 6 ? Dots : secret[..3] + Dots;
    }

    /// <summary>The name suggested for a backup made by hand: <c>ibt-backup-20261029.ibtbackup</c>.</summary>
    public static string ManualFileName(DateTimeOffset now) =>
        "ibt-backup-" + now.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + BackupCrypto.Extension;

    private static BackupChange Secret(string label, string old, string @new) =>
        new(label, Mask(old), Mask(@new), true, !string.Equals(old, @new, StringComparison.Ordinal));

    private static BackupChange Plain(string label, string old, string @new) =>
        new(label, Blank(old), Blank(@new), false, !string.Equals(old, @new, StringComparison.Ordinal));

    private static BackupChange Plain(string label, string old, string @new, bool changed) => new(label, old, @new, false, changed);

    private static string Blank(string text) => text.Length == 0 ? "(vazio)" : text;

    private static string OnOff(bool on) => on ? "Ligado" : "Desligado";

    private static string Side(string position) => position == "bottom" ? "Embaixo" : "Em cima";

    private static string Chosen(string path) => path.Length == 0 ? "(nenhuma)" : "Tela salva";

    private static string RuleCount(string rules)
    {
        var count = rules.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;
        return count switch { 0 => "(nenhuma)", 1 => "1 linha", _ => count.ToString(CultureInfo.InvariantCulture) + " linhas" };
    }
}
