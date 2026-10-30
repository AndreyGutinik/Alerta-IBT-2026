using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AlertaIBT.Core.Correction;

namespace AlertaIBT.Core.Settings;

/// <summary>
/// Reads and writes <see cref="AppSettings"/> as JSON, forgivingly: whatever is wrong with the text, the app gets usable
/// settings back. A hand-edited, outdated, half-written or damaged file never throws.
/// </summary>
public static partial class SettingsJson
{
    private const int FormatVersion = 1;

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,

        // The file is UTF-8 and meant to be read by people: keep accents readable instead of escaped.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonDocumentOptions ReadOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>
    /// Reads settings from the text of <c>settings.json</c>.
    /// </summary>
    /// <param name="json">The file's text, or <c>null</c> when there is no file.</param>
    /// <returns>
    /// The settings, held in range, and whether the text was corrupt. No file, a missing key, a missing section, an
    /// unknown key or a value of the wrong type are not corruption: they give the default for that value. Text that is
    /// not JSON (empty, cut in the middle, zero bytes) or not a JSON object gives all the defaults and
    /// <c>WasCorrupt = true</c>, so the caller can keep a copy of the file.
    /// </returns>
    public static (AppSettings Settings, bool WasCorrupt) Load(string? json)
    {
        if (json is null)
        {
            return (new AppSettings(), false);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, ReadOptions);
        }
        catch (JsonException)
        {
            return (new AppSettings(), true);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return (new AppSettings(), true);
            }

            var defaults = new AppSettings();
            var bar = Section(root, "bar");
            var telegram = Section(root, "telegram");
            var correction = Section(root, "correction");
            var system = Section(root, "system");
            var site = Section(root, "site");
            var mouseLock = Section(root, "mouseLock");
            var audio = Section(root, "audio");
            var player = Section(root, "player");
            var ndi = Section(root, "ndi");
            var backup = Section(root, "backup");

            var settings = new AppSettings
            {
                Bar = new BarSettings
                {
                    Position = ReadString(bar, "position") ?? defaults.Bar.Position,
                    Height = ReadInt(bar, "height") ?? defaults.Bar.Height,
                    FontSize = ReadInt(bar, "fontSize"),
                    Speed = ReadInt(bar, "speed") ?? defaults.Bar.Speed,
                    Background = ReadString(bar, "background") ?? defaults.Bar.Background,
                    Foreground = ReadString(bar, "foreground") ?? defaults.Bar.Foreground,
                    SlideMs = ReadInt(bar, "slideMs") ?? defaults.Bar.SlideMs,
                    PaddingX = ReadInt(bar, "paddingX") ?? defaults.Bar.PaddingX,
                },
                Telegram = new TelegramSettings
                {
                    ChatId = ReadLong(telegram, "chatId") ?? defaults.Telegram.ChatId,
                    TokenProtected = ReadString(telegram, "tokenProtected"),
                    Offset = ReadLong(telegram, "offset") ?? defaults.Telegram.Offset,
                },
                Correction = new CorrectionSettings
                {
                    Enabled = ReadBool(correction, "enabled") ?? defaults.Correction.Enabled,
                    Mode = ReadString(correction, "mode") ?? defaults.Correction.Mode,
                    GeminiKeyProtected = ReadString(correction, "geminiKeyProtected"),
                    GeminiModel = ReadString(correction, "geminiModel") ?? defaults.Correction.GeminiModel,
                    FixedRules = ReadString(correction, "fixedRules") ?? defaults.Correction.FixedRules,
                },
                System = new SystemSettings
                {
                    Hotkey = ReadString(system, "hotkey") ?? defaults.System.Hotkey,
                    LastLedDevicePath = ReadString(system, "lastLedDevicePath"),
                    ClearHistoryWeekly = ReadBool(system, "clearHistoryWeekly") ?? defaults.System.ClearHistoryWeekly,
                    LastHistoryClearAt = ReadTime(system, "lastHistoryClearAt"),
                    SitePendingClear = ReadBool(system, "sitePendingClear") ?? defaults.System.SitePendingClear,
                    AutoUpdate = ReadBool(system, "autoUpdate") ?? defaults.System.AutoUpdate,
                    AlertsEnabled = ReadBool(system, "alertsEnabled") ?? defaults.System.AlertsEnabled,
                    UpdateAttemptVersion = ReadString(system, "updateAttemptVersion"),
                    UpdateAttemptSince = ReadTime(system, "updateAttemptSince"),
                    UpdateAttempts = ReadInt(system, "updateAttempts") ?? defaults.System.UpdateAttempts,
                },
                Site = new SiteSettings
                {
                    Enabled = ReadBool(site, "enabled") ?? defaults.Site.Enabled,
                    DatabaseUrl = ReadString(site, "databaseUrl") ?? defaults.Site.DatabaseUrl,
                    ApiKey = ReadString(site, "apiKey") ?? defaults.Site.ApiKey,
                    ProjectId = ReadString(site, "projectId") ?? defaults.Site.ProjectId,
                    DeviceEmail = ReadString(site, "deviceEmail") ?? defaults.Site.DeviceEmail,
                    PasswordProtected = ReadString(site, "passwordProtected"),
                },
                MouseLock = new MouseLockSettings
                {
                    Enabled = ReadBool(mouseLock, "enabled") ?? defaults.MouseLock.Enabled,
                    Hotkey = ReadString(mouseLock, "hotkey") ?? defaults.MouseLock.Hotkey,
                    TripleCtrl = ReadBool(mouseLock, "tripleCtrl") ?? defaults.MouseLock.TripleCtrl,
                },
                Audio = new AudioSettings
                {
                    Enabled = ReadBool(audio, "enabled") ?? defaults.Audio.Enabled,
                    MasterVolume = ReadInt(audio, "masterVolume") ?? defaults.Audio.MasterVolume,
                    MasterMuted = ReadBool(audio, "masterMuted") ?? defaults.Audio.MasterMuted,
                    SystemSoundsVolume = ReadInt(audio, "systemSoundsVolume") ?? defaults.Audio.SystemSoundsVolume,
                    SystemSoundsMuted = ReadBool(audio, "systemSoundsMuted") ?? defaults.Audio.SystemSoundsMuted,
                    Apps = ReadApps(audio) ?? defaults.Audio.Apps,
                    LockOutput = ReadBool(audio, "lockOutput") ?? defaults.Audio.LockOutput,
                    OutputDeviceId = ReadString(audio, "outputDeviceId"),
                    OutputDeviceName = ReadString(audio, "outputDeviceName"),
                    OutputMatch = ReadStrings(audio, "outputMatch") ?? defaults.Audio.OutputMatch,
                    HolyricsRoute = ReadBool(audio, "holyricsRoute") ?? defaults.Audio.HolyricsRoute,
                    HolyricsDeviceId = ReadString(audio, "holyricsDeviceId"),
                    HolyricsDeviceName = ReadString(audio, "holyricsDeviceName"),
                    HolyricsMatch = ReadStrings(audio, "holyricsMatch") ?? defaults.Audio.HolyricsMatch,
                    VolumeRampSeconds = ReadDouble(audio, "volumeRampSeconds") ?? defaults.Audio.VolumeRampSeconds,
                },
                Player = new PlayerSettings
                {
                    Enabled = ReadBool(player, "enabled") ?? defaults.Player.Enabled,
                    OutputDeviceId = ReadString(player, "outputDeviceId"),
                    OutputDeviceName = ReadString(player, "outputDeviceName"),
                    OutputMatch = ReadStrings(player, "outputMatch") ?? defaults.Player.OutputMatch,
                    Volume = ReadInt(player, "volume") ?? defaults.Player.Volume,
                    StartMuted = ReadBool(player, "startMuted") ?? defaults.Player.StartMuted,
                    Shuffle = ReadBool(player, "shuffle") ?? defaults.Player.Shuffle,
                    Repeat = ReadString(player, "repeat") ?? defaults.Player.Repeat,
                    MediaKeys = ReadBool(player, "mediaKeys") ?? defaults.Player.MediaKeys,
                    // The old file had a stop fade (stopFadeSeconds) and an alarm fade (alarmFadeSeconds): they become the fade-out and the (shared) fade-in.
                    FadeInSeconds = ReadDouble(player, "fadeInSeconds") ?? ReadDouble(player, "alarmFadeSeconds") ?? defaults.Player.FadeInSeconds,
                    FadeOutSeconds = ReadDouble(player, "fadeOutSeconds") ?? ReadDouble(player, "stopFadeSeconds") ?? defaults.Player.FadeOutSeconds,
                    CrossfadeSeconds = ReadDouble(player, "crossfadeSeconds") ?? defaults.Player.CrossfadeSeconds,
                    CrossfadeCurve = CrossfadeCurves.Parse(ReadString(player, "crossfadeCurve")),
                    CurrentPlaylist = ReadString(player, "currentPlaylist") ?? defaults.Player.CurrentPlaylist,
                    Playlists = ReadPlaylists(player) ?? defaults.Player.Playlists,
                    AlarmsEnabled = ReadBool(player, "alarmsEnabled") ?? defaults.Player.AlarmsEnabled,
                    Alarms = ReadAlarms(player) ?? defaults.Player.Alarms,
                },
                Ndi = new NdiSettings
                {
                    ShortcutsEnabled = ReadBool(ndi, "shortcutsEnabled") ?? defaults.Ndi.ShortcutsEnabled,
                    FixedIps = ReadStrings(ndi, "fixedIps") ?? defaults.Ndi.FixedIps,
                    Cameras = ReadCameras(ndi) ?? defaults.Ndi.Cameras,
                    MonitorLocked = ReadBool(ndi, "monitorLocked") ?? defaults.Ndi.MonitorLocked,
                    MonitorDeviceName = ReadString(ndi, "monitorDeviceName") ?? defaults.Ndi.MonitorDeviceName,
                    HideFromCapture = ReadBool(ndi, "hideFromCapture") ?? defaults.Ndi.HideFromCapture,
                },
                Backup = new BackupSettings
                {
                    AutoEnabled = ReadBool(backup, "autoEnabled") ?? defaults.Backup.AutoEnabled,
                    Folder = ReadString(backup, "folder") ?? defaults.Backup.Folder,
                    PasswordProtected = ReadString(backup, "passwordProtected"),
                },
            };
            return (Normalize(settings), false);
        }
    }

    /// <summary>The text to write to <c>settings.json</c>: indented JSON of the settings held in range. It never holds a secret in clear.</summary>
    public static string Save(AppSettings s)
    {
        ArgumentNullException.ThrowIfNull(s);

        var n = Normalize(s);
        var root = new JsonObject
        {
            ["version"] = FormatVersion,
            ["bar"] = new JsonObject
            {
                ["position"] = n.Bar.Position,
                ["height"] = n.Bar.Height,
                ["fontSize"] = n.Bar.FontSize,
                ["speed"] = n.Bar.Speed,
                ["background"] = n.Bar.Background,
                ["foreground"] = n.Bar.Foreground,
                ["slideMs"] = n.Bar.SlideMs,
                ["paddingX"] = n.Bar.PaddingX,
            },
            ["telegram"] = new JsonObject
            {
                ["chatId"] = n.Telegram.ChatId,
                ["tokenProtected"] = n.Telegram.TokenProtected,
                ["offset"] = n.Telegram.Offset,
            },
            ["correction"] = new JsonObject
            {
                ["enabled"] = n.Correction.Enabled,
                ["mode"] = n.Correction.Mode,
                ["geminiKeyProtected"] = n.Correction.GeminiKeyProtected,
                ["geminiModel"] = n.Correction.GeminiModel,
                ["fixedRules"] = n.Correction.FixedRules,
            },
            ["system"] = new JsonObject
            {
                ["hotkey"] = n.System.Hotkey,
                ["lastLedDevicePath"] = n.System.LastLedDevicePath,
                ["clearHistoryWeekly"] = n.System.ClearHistoryWeekly,
                ["lastHistoryClearAt"] = n.System.LastHistoryClearAt?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                ["sitePendingClear"] = n.System.SitePendingClear,
                ["autoUpdate"] = n.System.AutoUpdate,
                ["alertsEnabled"] = n.System.AlertsEnabled,
                ["updateAttemptVersion"] = n.System.UpdateAttemptVersion,
                ["updateAttemptSince"] = n.System.UpdateAttemptSince?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                ["updateAttempts"] = n.System.UpdateAttempts,
            },
            ["site"] = new JsonObject
            {
                ["enabled"] = n.Site.Enabled,
                ["databaseUrl"] = n.Site.DatabaseUrl,
                ["apiKey"] = n.Site.ApiKey,
                ["projectId"] = n.Site.ProjectId,
                ["deviceEmail"] = n.Site.DeviceEmail,
                ["passwordProtected"] = n.Site.PasswordProtected,
            },
            ["mouseLock"] = new JsonObject
            {
                ["enabled"] = n.MouseLock.Enabled,
                ["hotkey"] = n.MouseLock.Hotkey,
                ["tripleCtrl"] = n.MouseLock.TripleCtrl,
            },
            ["audio"] = new JsonObject
            {
                ["enabled"] = n.Audio.Enabled,
                ["masterVolume"] = n.Audio.MasterVolume,
                ["masterMuted"] = n.Audio.MasterMuted,
                ["systemSoundsVolume"] = n.Audio.SystemSoundsVolume,
                ["systemSoundsMuted"] = n.Audio.SystemSoundsMuted,
                ["apps"] = new JsonArray(n.Audio.Apps.Select(app => (JsonNode)new JsonObject
                {
                    ["name"] = app.Name,
                    ["processNames"] = new JsonArray(app.ProcessNames.Select(name => (JsonNode)JsonValue.Create(name)!).ToArray()),
                    ["volume"] = app.Volume,
                    ["muted"] = app.Muted,
                }).ToArray()),
                ["lockOutput"] = n.Audio.LockOutput,
                ["outputDeviceId"] = n.Audio.OutputDeviceId,
                ["outputDeviceName"] = n.Audio.OutputDeviceName,
                ["outputMatch"] = new JsonArray(n.Audio.OutputMatch.Select(word => (JsonNode)JsonValue.Create(word)!).ToArray()),
                ["holyricsRoute"] = n.Audio.HolyricsRoute,
                ["holyricsDeviceId"] = n.Audio.HolyricsDeviceId,
                ["holyricsDeviceName"] = n.Audio.HolyricsDeviceName,
                ["holyricsMatch"] = new JsonArray(n.Audio.HolyricsMatch.Select(word => (JsonNode)JsonValue.Create(word)!).ToArray()),
                ["volumeRampSeconds"] = n.Audio.VolumeRampSeconds,
            },
            ["player"] = new JsonObject
            {
                ["enabled"] = n.Player.Enabled,
                ["outputDeviceId"] = n.Player.OutputDeviceId,
                ["outputDeviceName"] = n.Player.OutputDeviceName,
                ["outputMatch"] = new JsonArray(n.Player.OutputMatch.Select(word => (JsonNode)JsonValue.Create(word)!).ToArray()),
                ["volume"] = n.Player.Volume,
                ["startMuted"] = n.Player.StartMuted,
                ["shuffle"] = n.Player.Shuffle,
                ["repeat"] = n.Player.Repeat,
                ["mediaKeys"] = n.Player.MediaKeys,
                ["fadeInSeconds"] = n.Player.FadeInSeconds,
                ["fadeOutSeconds"] = n.Player.FadeOutSeconds,
                ["crossfadeSeconds"] = n.Player.CrossfadeSeconds,
                ["crossfadeCurve"] = CrossfadeCurves.ToText(n.Player.CrossfadeCurve),
                ["currentPlaylist"] = n.Player.CurrentPlaylist,
                ["playlists"] = new JsonArray(n.Player.Playlists.Select(list => (JsonNode)new JsonObject
                {
                    ["name"] = list.Name,
                    ["folder"] = list.Folder,
                }).ToArray()),
                ["alarmsEnabled"] = n.Player.AlarmsEnabled,
                ["alarms"] = new JsonArray(n.Player.Alarms.Select(alarm => (JsonNode)new JsonObject
                {
                    ["id"] = alarm.Id,
                    ["enabled"] = alarm.Enabled,
                    ["start"] = alarm.Start,
                    ["latest"] = alarm.Latest,
                    ["days"] = alarm.Days,
                    ["playlist"] = alarm.Playlist,
                    ["lastFiredOn"] = alarm.LastFiredOn,
                }).ToArray()),
            },
            ["backup"] = new JsonObject
            {
                ["autoEnabled"] = n.Backup.AutoEnabled,
                ["folder"] = n.Backup.Folder,
                ["passwordProtected"] = n.Backup.PasswordProtected,
            },
            ["ndi"] = new JsonObject
            {
                ["shortcutsEnabled"] = n.Ndi.ShortcutsEnabled,
                ["fixedIps"] = new JsonArray(n.Ndi.FixedIps.Select(ip => (JsonNode)JsonValue.Create(ip)!).ToArray()),
                ["cameras"] = new JsonArray(n.Ndi.Cameras.Select(camera => (JsonNode)new JsonObject
                {
                    ["name"] = camera.Name,
                    ["source"] = camera.Source,
                    ["verticalPosition"] = camera.VerticalPosition,
                }).ToArray()),
                ["monitorLocked"] = n.Ndi.MonitorLocked,
                ["monitorDeviceName"] = n.Ndi.MonitorDeviceName,
                ["hideFromCapture"] = n.Ndi.HideFromCapture,
            },
        };
        return root.ToJsonString(WriteOptions);
    }

    /// <summary>
    /// A copy with every value held where the app can use it: height 28 to 120, speed 30 to 300, margin 0 to 200,
    /// slide 100 to 1500 ms, font size 8 to 200 (or automatic), position <c>top</c> or <c>bottom</c>, colors
    /// <c>#RRGGBB</c>, mode <c>gemini</c> or <c>offline</c>, no negative offset, a database address that is a Firebase one (or none),
    /// and blank text back to its default.
    /// Applying it twice changes nothing more.
    /// </summary>
    public static AppSettings Normalize(AppSettings s)
    {
        ArgumentNullException.ThrowIfNull(s);

        var d = new AppSettings();
        var bar = s.Bar ?? d.Bar;
        var telegram = s.Telegram ?? d.Telegram;
        var correction = s.Correction ?? d.Correction;
        var system = s.System ?? d.System;
        var site = s.Site ?? d.Site;
        var mouseLock = s.MouseLock ?? d.MouseLock;
        var audio = s.Audio ?? d.Audio;
        var player = s.Player ?? d.Player;
        var ndi = s.Ndi ?? d.Ndi;
        var backup = s.Backup ?? d.Backup;

        return new AppSettings
        {
            Bar = new BarSettings
            {
                Position = string.Equals(bar.Position?.Trim(), "bottom", StringComparison.OrdinalIgnoreCase) ? "bottom" : "top",
                Height = Math.Clamp(bar.Height, BarSettings.MinHeight, BarSettings.MaxHeight),
                FontSize = bar.FontSize is > 0 ? Math.Clamp(bar.FontSize.Value, BarSettings.MinFontSize, BarSettings.MaxFontSize) : null,
                Speed = Math.Clamp(bar.Speed, BarSettings.MinSpeed, BarSettings.MaxSpeed),
                Background = NormalizeColor(bar.Background, d.Bar.Background),
                Foreground = NormalizeColor(bar.Foreground, d.Bar.Foreground),
                SlideMs = Math.Clamp(bar.SlideMs, BarSettings.MinSlideMs, BarSettings.MaxSlideMs),
                PaddingX = Math.Clamp(bar.PaddingX, 0, BarSettings.MaxPaddingX),
            },
            Telegram = new TelegramSettings
            {
                ChatId = telegram.ChatId,
                TokenProtected = NullIfBlank(telegram.TokenProtected),
                Offset = Math.Max(0, telegram.Offset),
            },
            Correction = new CorrectionSettings
            {
                Enabled = correction.Enabled,
                Mode = string.Equals(correction.Mode?.Trim(), "offline", StringComparison.OrdinalIgnoreCase) ? "offline" : "gemini",
                GeminiKeyProtected = NullIfBlank(correction.GeminiKeyProtected),
                GeminiModel = NullIfBlank(correction.GeminiModel) ?? GeminiPrompt.DefaultModel,
                FixedRules = correction.FixedRules ?? string.Empty,
            },
            System = new SystemSettings
            {
                Hotkey = NullIfBlank(system.Hotkey) ?? d.System.Hotkey,
                LastLedDevicePath = NullIfBlank(system.LastLedDevicePath),
                ClearHistoryWeekly = system.ClearHistoryWeekly,
                LastHistoryClearAt = system.LastHistoryClearAt?.ToUniversalTime(),
                SitePendingClear = system.SitePendingClear,
                AutoUpdate = system.AutoUpdate,
                AlertsEnabled = system.AlertsEnabled,
                UpdateAttemptVersion = NullIfBlank(system.UpdateAttemptVersion),
                UpdateAttemptSince = system.UpdateAttemptSince?.ToUniversalTime(),
                UpdateAttempts = Math.Max(0, system.UpdateAttempts),
            },
            Site = new SiteSettings
            {
                Enabled = site.Enabled,

                // An address that is not a Firebase database (or not https) is dropped: the connector never sees it.
                DatabaseUrl = SiteSettings.IsValidDatabaseUrl(site.DatabaseUrl) ? site.DatabaseUrl!.Trim().TrimEnd('/') : string.Empty,
                ApiKey = NullIfBlank(site.ApiKey) ?? string.Empty,
                ProjectId = NullIfBlank(site.ProjectId) ?? string.Empty,
                DeviceEmail = NullIfBlank(site.DeviceEmail) ?? string.Empty,
                PasswordProtected = NullIfBlank(site.PasswordProtected),
            },
            MouseLock = new MouseLockSettings
            {
                Enabled = mouseLock.Enabled,

                // Whether the text is a usable shortcut is decided where it is registered (the default takes its place there, with a log line).
                Hotkey = NullIfBlank(mouseLock.Hotkey) ?? d.MouseLock.Hotkey,
                TripleCtrl = mouseLock.TripleCtrl,
            },
            Audio = new AudioSettings
            {
                Enabled = audio.Enabled,
                MasterVolume = ClampVolume(audio.MasterVolume),
                MasterMuted = audio.MasterMuted,
                SystemSoundsVolume = ClampVolume(audio.SystemSoundsVolume),
                SystemSoundsMuted = audio.SystemSoundsMuted,
                Apps = NormalizeApps(audio.Apps) ?? AudioSettings.DefaultApps(),
                LockOutput = audio.LockOutput,
                OutputDeviceId = NullIfBlank(audio.OutputDeviceId),
                OutputDeviceName = NullIfBlank(audio.OutputDeviceName),
                OutputMatch = NormalizeWords(audio.OutputMatch) ?? AudioSettings.DefaultOutputWords,
                HolyricsRoute = audio.HolyricsRoute,
                HolyricsDeviceId = NullIfBlank(audio.HolyricsDeviceId),
                HolyricsDeviceName = NullIfBlank(audio.HolyricsDeviceName),
                HolyricsMatch = NormalizeWords(audio.HolyricsMatch) ?? AudioSettings.DefaultHolyricsWords,
                VolumeRampSeconds = PlayerSettings.Nearest(AudioSettings.VolumeRampChoices, audio.VolumeRampSeconds),
            },
            Player = NormalizePlayer(player),
            Ndi = NormalizeNdi(ndi),
            Backup = new BackupSettings
            {
                AutoEnabled = backup.AutoEnabled,
                Folder = NullIfBlank(backup.Folder)?.Trim() ?? string.Empty,
                PasswordProtected = NullIfBlank(backup.PasswordProtected),
            },
        };
    }

    // The NDI part held usable: exactly six slots (missing ones filled with the defaults), position 0..100, no blank or repeated fixed address.
    private static NdiSettings NormalizeNdi(NdiSettings n)
    {
        var d = new NdiSettings();
        var cameras = new List<NdiCameraSlot>(NdiSettings.SlotCount);
        var given = n.Cameras ?? d.Cameras;
        for (var i = 0; i < NdiSettings.SlotCount; i++)
        {
            var fallback = d.Cameras[i];
            var slot = i < given.Count ? given[i] : null;
            cameras.Add(slot is null
                ? fallback
                : new NdiCameraSlot(
                    NullIfBlank(slot.Name) ?? fallback.Name,
                    NullIfBlank(slot.Source) ?? string.Empty,
                    Math.Clamp(slot.VerticalPosition, 0, 100)));
        }

        return new NdiSettings
        {
            ShortcutsEnabled = n.ShortcutsEnabled,
            FixedIps = NormalizeWords(n.FixedIps) ?? d.FixedIps,
            Cameras = cameras,
            MonitorLocked = n.MonitorLocked,
            MonitorDeviceName = NullIfBlank(n.MonitorDeviceName) ?? string.Empty,
            HideFromCapture = n.HideFromCapture,
        };
    }

    private static List<NdiCameraSlot>? ReadCameras(JsonElement ndi)
    {
        if (Find(ndi, "cameras") is not { ValueKind: JsonValueKind.Array } list)
        {
            return null;
        }

        return list.EnumerateArray()
            .Select(item => item.ValueKind == JsonValueKind.Object
                ? new NdiCameraSlot(ReadString(item, "name") ?? string.Empty, ReadString(item, "source") ?? string.Empty, ReadInt(item, "verticalPosition") ?? NdiSettings.DefaultVerticalPosition)
                : new NdiCameraSlot(string.Empty, string.Empty))
            .ToList();
    }

    // The player held usable. Empty lists are kept (the owner removed everything); only a missing (null) list gets the defaults.
    private static PlayerSettings NormalizePlayer(PlayerSettings p)
    {
        var d = new PlayerSettings();
        var playlists = NormalizePlaylists(p.Playlists) ?? d.Playlists;
        var current = NullIfBlank(p.CurrentPlaylist) ?? d.CurrentPlaylist;
        if (playlists.FirstOrDefault(l => string.Equals(l.Name, current, StringComparison.OrdinalIgnoreCase)) is { } found)
        {
            current = found.Name;
        }
        else if (playlists.Count > 0)
        {
            current = playlists[0].Name;
        }

        return new PlayerSettings
        {
            Enabled = p.Enabled,
            OutputDeviceId = NullIfBlank(p.OutputDeviceId),
            OutputDeviceName = NullIfBlank(p.OutputDeviceName),
            OutputMatch = p.OutputMatch is null
                ? d.OutputMatch
                : p.OutputMatch.Select(NullIfBlank).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            Volume = ClampVolume(p.Volume),
            StartMuted = p.StartMuted,
            Shuffle = p.Shuffle,
            Repeat = p.Repeat?.Trim().ToLowerInvariant() switch { "off" => "off", "one" => "one", _ => "all" },
            MediaKeys = p.MediaKeys,
            FadeInSeconds = PlayerSettings.Nearest(PlayerSettings.FadeChoices, p.FadeInSeconds),
            FadeOutSeconds = PlayerSettings.Nearest(PlayerSettings.FadeChoices, p.FadeOutSeconds),
            CrossfadeSeconds = PlayerSettings.Nearest(PlayerSettings.CrossfadeChoices, p.CrossfadeSeconds),
            CrossfadeCurve = Enum.IsDefined(p.CrossfadeCurve) ? p.CrossfadeCurve : CrossfadeCurve.Linear,
            CurrentPlaylist = current,
            Playlists = playlists,
            AlarmsEnabled = p.AlarmsEnabled,
            Alarms = NormalizeAlarms(p.Alarms) ?? d.Alarms,
        };
    }

    private static List<PlaylistEntry>? NormalizePlaylists(IReadOnlyList<PlaylistEntry>? playlists)
    {
        if (playlists is null)
        {
            return null;
        }

        var result = new List<PlaylistEntry>();
        foreach (var list in playlists)
        {
            var name = NullIfBlank(list?.Name);
            var folder = NullIfBlank(list?.Folder);
            if (name is null || folder is null || result.Any(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            result.Add(new PlaylistEntry(name, folder));
        }

        return result;
    }

    private static List<AlarmEntry>? NormalizeAlarms(IReadOnlyList<AlarmEntry>? alarms)
    {
        if (alarms is null)
        {
            return null;
        }

        var result = new List<AlarmEntry>();
        foreach (var alarm in alarms)
        {
            if (alarm is null)
            {
                continue;
            }

            var id = NullIfBlank(alarm.Id) ?? "alarme-" + (result.Count + 1).ToString(CultureInfo.InvariantCulture);
            while (result.Any(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase)))
            {
                id += "-2";
            }

            var start = PlayerSettings.ParseTime(alarm.Start) ?? PlayerSettings.DefaultStart;
            var latest = PlayerSettings.ParseTime(alarm.Latest) ?? PlayerSettings.DefaultLatest;
            if (latest < start)
            {
                // "Do not start after" before the start makes no sense: 25 minutes later, or the end of the day.
                latest = start.AddMinutes(25) < start ? new TimeOnly(23, 59) : start.AddMinutes(25);
            }

            var fired = NullIfBlank(alarm.LastFiredOn);
            result.Add(new AlarmEntry(
                id,
                alarm.Enabled,
                start.ToString("HH:mm", CultureInfo.InvariantCulture),
                latest.ToString("HH:mm", CultureInfo.InvariantCulture),
                alarm.Days & AlarmEntry.AllDays,
                NullIfBlank(alarm.Playlist) ?? PlayerSettings.DefaultPlaylistName,
                fired is not null && DateOnly.TryParseExact(fired, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
                    ? day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                    : null));
        }

        return result;
    }

    // A list of strings; null when it is missing or not a list (the default then applies).
    private static List<string>? ReadStrings(JsonElement section, string name) =>
        Find(section, name) is { ValueKind: JsonValueKind.Array } list
            ? list.EnumerateArray().Where(n => n.ValueKind == JsonValueKind.String).Select(n => n.GetString() ?? string.Empty).ToList()
            : null;

    private static List<PlaylistEntry>? ReadPlaylists(JsonElement player)
    {
        if (Find(player, "playlists") is not { ValueKind: JsonValueKind.Array } list)
        {
            return null;
        }

        return list.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.Object)
            .Select(item => new PlaylistEntry(ReadString(item, "name") ?? string.Empty, ReadString(item, "folder") ?? string.Empty))
            .ToList();
    }

    private static List<AlarmEntry>? ReadAlarms(JsonElement player)
    {
        if (Find(player, "alarms") is not { ValueKind: JsonValueKind.Array } list)
        {
            return null;
        }

        return list.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.Object)
            .Select(item => new AlarmEntry(
                ReadString(item, "id") ?? string.Empty,
                ReadBool(item, "enabled") ?? true,
                ReadString(item, "start") ?? string.Empty,
                ReadString(item, "latest") ?? string.Empty,
                ReadInt(item, "days") ?? AlarmEntry.SundayOnly,
                ReadString(item, "playlist") ?? string.Empty,
                ReadString(item, "lastFiredOn")))
            .ToList();
    }

    // The words of a name rule: blanks and repeats (any case) dropped; null (missing) means "use the default". An emptied list is kept.
    private static List<string>? NormalizeWords(IReadOnlyList<string>? words) =>
        words?.Select(NullIfBlank).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static int ClampVolume(int volume) => Math.Clamp(volume, AudioSettings.MinVolume, AudioSettings.MaxVolume);

    // The programs held usable: a name, at least one process name, a volume 0 to 100. An empty result is "nothing usable": null.
    private static List<AudioAppEntry>? NormalizeApps(IReadOnlyList<AudioAppEntry>? apps)
    {
        if (apps is null)
        {
            return null;
        }

        var result = new List<AudioAppEntry>();
        foreach (var app in apps)
        {
            var name = NullIfBlank(app?.Name);
            var processes = app?.ProcessNames?
                .Select(NullIfBlank)
                .OfType<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (name is null || processes is not { Count: > 0 } || result.Any(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            result.Add(new AudioAppEntry(name, processes, ClampVolume(app!.Volume), app.Muted));
        }

        return result.Count > 0 ? result : null;
    }

    // The "apps" list of the audio section; null when it is missing or not a list (the defaults then apply).
    private static List<AudioAppEntry>? ReadApps(JsonElement audio)
    {
        if (Find(audio, "apps") is not { ValueKind: JsonValueKind.Array } list)
        {
            return null;
        }

        var apps = new List<AudioAppEntry>();
        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var processes = new List<string>();
            if (Find(item, "processNames") is { ValueKind: JsonValueKind.Array } names)
            {
                processes.AddRange(names.EnumerateArray().Where(n => n.ValueKind == JsonValueKind.String).Select(n => n.GetString() ?? string.Empty));
            }

            apps.Add(new AudioAppEntry(
                ReadString(item, "name") ?? string.Empty,
                processes,
                ReadInt(item, "volume") ?? 100,
                ReadBool(item, "muted") ?? false));
        }

        return apps;
    }

    private static string? NullIfBlank(string? text)
    {
        var trimmed = text?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    private static string NormalizeColor(string? color, string fallback)
    {
        var trimmed = color?.Trim() ?? string.Empty;
        return HexColor().IsMatch(trimmed) ? trimmed.ToUpperInvariant() : fallback;
    }

    // The section as an object, or a default (undefined) element when it is missing or is not an object.
    private static JsonElement Section(JsonElement root, string name) =>
        Find(root, name) is { ValueKind: JsonValueKind.Object } section ? section : default;

    private static JsonElement? Find(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var property in parent.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value;
            }
        }

        return null;
    }

    private static string? ReadString(JsonElement section, string name) =>
        Find(section, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    private static bool? ReadBool(JsonElement section, string name) =>
        Find(section, name) is { } value && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    // A moment written as ISO 8601 text; anything else (a number, a mangled date) is "no moment", so the app starts the weekly count over.
    private static DateTimeOffset? ReadTime(JsonElement section, string name) =>
        ReadString(section, name) is { } text
        && DateTimeOffset.TryParse(text.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var moment)
            ? moment
            : null;

    // A whole number; a fractional or enormous one is rounded or pinned to the int range (the caller then clamps it).
    private static double? ReadDouble(JsonElement section, string name) =>
        Find(section, name) is { ValueKind: JsonValueKind.Number } value && value.TryGetDouble(out var real) && double.IsFinite(real) ? real : null;

    private static int? ReadInt(JsonElement section, string name)
    {
        if (Find(section, name) is not { ValueKind: JsonValueKind.Number } value)
        {
            return null;
        }

        if (value.TryGetInt32(out var whole))
        {
            return whole;
        }

        return value.TryGetDouble(out var real) && double.IsFinite(real)
            ? (int)Math.Clamp(Math.Round(real, MidpointRounding.AwayFromZero), int.MinValue, int.MaxValue)
            : null;
    }

    // Ids and offsets are exact: a number that is not a whole 64-bit one is not an id. A number written as text is
    // understood (the old app kept the chat id as a string).
    private static long? ReadLong(JsonElement section, string name)
    {
        return Find(section, name) switch
        {
            { ValueKind: JsonValueKind.Number } value when value.TryGetInt64(out var number) => number,
            { ValueKind: JsonValueKind.String } value
                when long.TryParse(value.GetString()?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) => number,
            _ => null,
        };
    }

    [GeneratedRegex("^#[0-9A-Fa-f]{6}$")]
    private static partial Regex HexColor();
}
