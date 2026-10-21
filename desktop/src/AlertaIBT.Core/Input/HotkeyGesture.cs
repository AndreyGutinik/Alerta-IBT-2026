using System.Diagnostics.CodeAnalysis;

namespace AlertaIBT.Core.Input;

/// <summary>The modifier keys of a global hotkey. The values are the ones Windows' <c>RegisterHotKey</c> expects.</summary>
[Flags]
public enum HotkeyModifiers
{
    /// <summary>No modifier.</summary>
    None = 0,

    /// <summary>Alt.</summary>
    Alt = 1,

    /// <summary>Ctrl.</summary>
    Control = 2,

    /// <summary>Shift.</summary>
    Shift = 4,

    /// <summary>The Windows key.</summary>
    Win = 8,
}

/// <summary>
/// A global hotkey such as <c>Ctrl+Alt+Space</c>: modifiers plus one virtual key. A hotkey always has Ctrl, Alt or Win
/// (Shift alone would swallow ordinary typing) and is never one of the chords of <see cref="HotkeyPolicy"/>. Ported from
/// App Flash, same names; the reserved chords are new.
/// </summary>
/// <param name="Modifiers">The modifier keys.</param>
/// <param name="VirtualKey">The Windows virtual-key code of the main key.</param>
public sealed record HotkeyGesture(HotkeyModifiers Modifiers, int VirtualKey)
{
    private static readonly Dictionary<int, string> NamesByKey = BuildNames();

    private static readonly Dictionary<string, int> KeysByName =
        NamesByKey.ToDictionary(pair => pair.Value, pair => pair.Key, StringComparer.OrdinalIgnoreCase);

    /// <summary>The main key's name, such as <c>F12</c> or <c>Space</c>.</summary>
    public string KeyName => NamesByKey.TryGetValue(VirtualKey, out var name) ? name : $"0x{VirtualKey:X2}";

    /// <summary>The text form, such as <c>Ctrl+Alt+Space</c>: modifiers in the order Ctrl, Alt, Shift, Win, then the key.</summary>
    public override string ToString()
    {
        var parts = new List<string>(5);
        if (Modifiers.HasFlag(HotkeyModifiers.Control))
        {
            parts.Add("Ctrl");
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Alt))
        {
            parts.Add("Alt");
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Shift))
        {
            parts.Add("Shift");
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Win))
        {
            parts.Add("Win");
        }

        parts.Add(KeyName);
        return string.Join('+', parts);
    }

    /// <summary>
    /// Why a chord cannot be the shortcut (see <see cref="HotkeyPolicy"/>), or <see cref="HotkeyProblem.None"/>: no Ctrl, Alt or
    /// Win, a chord of the system or of editing, or a main key the app does not name.
    /// </summary>
    public static HotkeyProblem Validate(HotkeyModifiers modifiers, int virtualKey)
    {
        var problem = HotkeyPolicy.Check(modifiers, virtualKey);
        if (problem != HotkeyProblem.None)
        {
            return problem;
        }

        return NamesByKey.ContainsKey(virtualKey) ? HotkeyProblem.None : HotkeyProblem.UnknownKey;
    }

    /// <summary>Builds a gesture; false when <see cref="Validate"/> finds a problem.</summary>
    public static bool TryCreate(HotkeyModifiers modifiers, int virtualKey, [NotNullWhen(true)] out HotkeyGesture? gesture)
    {
        gesture = null;
        if (Validate(modifiers, virtualKey) != HotkeyProblem.None)
        {
            return false;
        }

        gesture = new HotkeyGesture(modifiers, virtualKey);
        return true;
    }

    /// <summary>
    /// Reads text such as <c>ctrl + alt + space</c> (case and spaces do not matter; <c>Control</c> and <c>Windows</c> are
    /// accepted). False for anything that is not exactly modifiers plus one known key.
    /// </summary>
    public static bool TryParse(string? text, [NotNullWhen(true)] out HotkeyGesture? gesture)
    {
        gesture = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var modifiers = HotkeyModifiers.None;
        int? key = null;
        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control":
                    modifiers |= HotkeyModifiers.Control;
                    break;
                case "alt":
                    modifiers |= HotkeyModifiers.Alt;
                    break;
                case "shift":
                    modifiers |= HotkeyModifiers.Shift;
                    break;
                case "win" or "windows":
                    modifiers |= HotkeyModifiers.Win;
                    break;
                default:
                    if (key is not null || !KeysByName.TryGetValue(raw, out var virtualKey))
                    {
                        return false;
                    }

                    key = virtualKey;
                    break;
            }
        }

        return key is int code && TryCreate(modifiers, code, out gesture);
    }

    private static Dictionary<int, string> BuildNames()
    {
        var names = new Dictionary<int, string>();
        for (var i = 1; i <= 24; i++)
        {
            names[0x6F + i] = $"F{i}";
        }

        for (var c = 'A'; c <= 'Z'; c++)
        {
            names[c] = c.ToString();
        }

        for (var d = 0; d <= 9; d++)
        {
            names[0x30 + d] = d.ToString();
            names[0x60 + d] = $"Num{d}";
        }

        names[0x13] = "Pause";
        names[0x20] = "Space";
        names[0x21] = "PageUp";
        names[0x22] = "PageDown";
        names[0x23] = "End";
        names[0x24] = "Home";
        names[0x2D] = "Insert";
        names[0x2E] = "Delete";
        names[0x91] = "ScrollLock";
        return names;
    }
}
