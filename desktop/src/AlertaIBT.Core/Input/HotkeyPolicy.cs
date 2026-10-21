namespace AlertaIBT.Core.Input;

/// <summary>Why a chord cannot be the global shortcut.</summary>
public enum HotkeyProblem
{
    /// <summary>The chord is fine.</summary>
    None,

    /// <summary>It has no Ctrl, Alt or Win (Shift alone, or nothing, would swallow ordinary typing).</summary>
    NoModifier,

    /// <summary>The main key is one the app does not name (a modifier key, Tab, Esc...).</summary>
    UnknownKey,

    /// <summary>It is a chord people use all day (copy, paste, Alt+F4...) or one that belongs to Windows.</summary>
    Reserved,
}

/// <summary>
/// The rules about which chords may be the global shortcut. A shortcut registered with <c>RegisterHotKey</c> takes the chord
/// away from every other program, so the ones for editing and for the system are never allowed: Ctrl+C/V/X/Z/A/Y/S and Ctrl+F4 on their
/// own (with Shift they are different commands and stay free), Alt+F4, Alt+Tab, Alt+Space, Win+L and Ctrl+Alt+Delete (Shift is
/// ignored for these). Everything else with Ctrl, Alt or Win is the user's choice.
/// </summary>
public static class HotkeyPolicy
{
    private const int VkTab = 0x09;
    private const int VkSpace = 0x20;
    private const int VkF4 = 0x73;
    private const int VkDelete = 0x2E;
    private const int VkL = 0x4C;

    /// <summary>
    /// Checks the modifiers and the key (a Windows virtual-key code). Whether the app names the key is not looked at here,
    /// see <see cref="HotkeyGesture.Validate"/>.
    /// </summary>
    public static HotkeyProblem Check(HotkeyModifiers modifiers, int virtualKey)
    {
        if ((modifiers & (HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Win)) == 0)
        {
            return HotkeyProblem.NoModifier;
        }

        // Ctrl+Shift+V and the like are other commands; for the chords of the system, an extra Shift changes nothing.
        var chord = modifiers & ~HotkeyModifiers.Shift;
        if (chord == HotkeyModifiers.Control && modifiers != chord)
        {
            return HotkeyProblem.None;
        }

        return IsReserved(chord, virtualKey) ? HotkeyProblem.Reserved : HotkeyProblem.None;
    }

    /// <summary>The short message for the shortcut box, in Portuguese; empty for <see cref="HotkeyProblem.None"/>.</summary>
    public static string Hint(HotkeyProblem problem) => problem switch
    {
        HotkeyProblem.NoModifier => "Precisa de Ctrl, Alt ou Win com uma tecla.",
        HotkeyProblem.UnknownKey => "Essa tecla n\U000000E3o pode ser usada. Escolha outra, junto com Ctrl, Alt ou Win.",
        HotkeyProblem.Reserved => "Esse atalho \U000000E9 do Windows ou de copiar e colar. Escolha outro, como Ctrl+Alt+Espa\U000000E7o.",
        _ => string.Empty,
    };

    // Letters are their own virtual-key codes (A is 0x41).
    private static bool IsReserved(HotkeyModifiers modifiers, int virtualKey) => modifiers switch
    {
        HotkeyModifiers.Control => virtualKey is 'C' or 'V' or 'X' or 'Z' or 'A' or 'Y' or 'S' or VkF4,
        HotkeyModifiers.Alt => virtualKey is VkF4 or VkTab or VkSpace,
        HotkeyModifiers.Win => virtualKey is VkL,
        HotkeyModifiers.Control | HotkeyModifiers.Alt => virtualKey is VkDelete,
        _ => false,
    };
}
