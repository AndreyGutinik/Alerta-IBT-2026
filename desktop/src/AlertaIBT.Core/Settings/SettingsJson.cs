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
            },
        };
        return root.ToJsonString(WriteOptions);
    }

    /// <summary>
    /// A copy with every value held where the app can use it: height 28 to 120, speed 30 to 300, margin 0 to 200,
    /// slide 100 to 1500 ms, font size 8 to 200 (or automatic), position <c>top</c> or <c>bottom</c>, colors
    /// <c>#RRGGBB</c>, mode <c>gemini</c> or <c>offline</c>, no negative offset, and blank text back to its default.
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
            },
        };
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

    // A whole number; a fractional or enormous one is rounded or pinned to the int range (the caller then clamps it).
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
