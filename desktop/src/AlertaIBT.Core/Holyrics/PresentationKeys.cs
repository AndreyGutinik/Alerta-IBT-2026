namespace AlertaIBT.Core.Holyrics;

/// <summary>What a key of the presentation mode does.</summary>
public enum PresentationKeyAction
{
    /// <summary>Nothing: the key is not one of the chosen ones, or the mode does not apply.</summary>
    None,

    /// <summary>Next slide.</summary>
    Next,

    /// <summary>Previous slide.</summary>
    Previous,
}

/// <summary>The keys of the presentation mode: the defaults, which keys may be chosen, and how a key is named on the screen.</summary>
public static class PresentationKeys
{
    /// <summary>The most keys a set may hold.</summary>
    public const int MaxPerSet = 8;

    /// <summary>The default "next" keys: Page Down and the right arrow (the two families of presenter remotes).</summary>
    public static IReadOnlyList<int> DefaultNext { get; } = [0x22, 0x27];

    /// <summary>The default "previous" keys: Page Up and the left arrow.</summary>
    public static IReadOnlyList<int> DefaultPrevious { get; } = [0x21, 0x25];

    /// <summary>
    /// Whether <paramref name="virtualKey"/> may be one of the chosen keys. Refused: modifiers (a key is only taken when none is held), the Windows keys, Escape,
    /// Caps/Num/Scroll Lock, the media and browser keys (the player owns those) and the codes that are not a key.
    /// </summary>
    public static bool IsAllowed(int virtualKey) => virtualKey switch
    {
        < 0x08 => false, // mouse buttons and the like
        0x10 or 0x11 or 0x12 => false, // Shift, Ctrl, Alt
        0x14 or 0x90 or 0x91 => false, // Caps, Num, Scroll Lock
        0x1B => false, // Escape
        0x5B or 0x5C => false, // the Windows keys
        >= 0xA0 and <= 0xB7 => false, // left/right modifiers, browser, volume and media keys
        0xE7 or 0xFF => false, // packet, none
        > 0xFE => false,
        _ => true,
    };

    /// <summary>The short text of the key for its chip (<c>PgDn</c>, a right arrow, <c>B</c>).</summary>
    public static string Label(int virtualKey) => virtualKey switch
    {
        0x22 => "PgDn",
        0x21 => "PgUp",
        0x27 => "\U00002192",
        0x25 => "\U00002190",
        0x26 => "\U00002191",
        0x28 => "\U00002193",
        0x20 => "Espa\U000000E7o",
        0x0D => "Enter",
        0x08 => "Backspace",
        0x09 => "Tab",
        0x2E => "Del",
        0x2D => "Ins",
        0x24 => "Home",
        0x23 => "End",
        0x2C => "PrtSc",
        0x13 => "Pause",
        >= 0x30 and <= 0x39 => ((char)virtualKey).ToString(),
        >= 0x41 and <= 0x5A => ((char)virtualKey).ToString(),
        >= 0x60 and <= 0x69 => "Num " + (virtualKey - 0x60),
        >= 0x70 and <= 0x87 => "F" + (virtualKey - 0x6F),
        _ => "#" + virtualKey,
    };

    /// <summary>The full name of the key, for the tooltip (<c>Page Down</c>, <c>Seta direita</c>).</summary>
    public static string Name(int virtualKey) => virtualKey switch
    {
        0x22 => "Page Down",
        0x21 => "Page Up",
        0x27 => "Seta direita",
        0x25 => "Seta esquerda",
        0x26 => "Seta para cima",
        0x28 => "Seta para baixo",
        0x20 => "Espa\U000000E7o",
        0x0D => "Enter",
        0x08 => "Backspace",
        0x09 => "Tab",
        0x2E => "Delete",
        0x2D => "Insert",
        0x24 => "Home",
        0x23 => "End",
        0x2C => "Print Screen",
        0x13 => "Pause",
        0x6A => "Num *",
        0x6B => "Num +",
        0x6D => "Num -",
        0x6E => "Num .",
        0x6F => "Num /",
        _ => Label(virtualKey) is var label && label.StartsWith('#') ? "Tecla " + virtualKey : Label(virtualKey),
    };

    /// <summary>
    /// A set as it should be kept: only allowed keys, each once, at most <see cref="MaxPerSet"/>, none that is in <paramref name="taken"/> (the other set).
    /// </summary>
    public static List<int> Clean(IEnumerable<int>? keys, IReadOnlyCollection<int>? taken = null)
    {
        var result = new List<int>();
        foreach (var key in keys ?? [])
        {
            if (IsAllowed(key) && !result.Contains(key) && taken?.Contains(key) != true && result.Count < MaxPerSet)
            {
                result.Add(key);
            }
        }

        return result;
    }
}

/// <summary>One key event as the filter sees it.</summary>
/// <param name="VirtualKey">The virtual-key code.</param>
/// <param name="KeyDown">True for a key going down, false for it coming up.</param>
/// <param name="ModifierHeld">Whether Ctrl, Alt, Shift or a Windows key is held (the key then passes through untouched).</param>
/// <param name="ForegroundIsOwn">Whether the window in front belongs to this app (its own text boxes keep their keys; the quick panel has its own arrows).</param>
public readonly record struct PresentationKeyEvent(int VirtualKey, bool KeyDown, bool ModifierHeld, bool ForegroundIsOwn);

/// <summary>What to do with a key event.</summary>
/// <param name="Consume">True: the hook swallows the event and the program in front never sees it.</param>
/// <param name="Action">The slide command to send; <see cref="PresentationKeyAction.None"/> for an event that only needs swallowing (a key-up, a repeat).</param>
public readonly record struct PresentationVerdict(bool Consume, PresentationKeyAction Action)
{
    /// <summary>Let the key through.</summary>
    public static PresentationVerdict Pass => new(false, PresentationKeyAction.None);
}

/// <summary>
/// The decision of the presentation mode, pure and fast (it runs inside the keyboard hook: no allocation, no lock, no call out). It swallows the chosen keys while the
/// mode is on and Holyrics is configured, and only when no modifier is held and the window in front is not this app. A swallowed key-down is remembered so its
/// key-up is swallowed too (always, even if the mode was turned off in between): the program in front never sees half a key press. An auto-repeat of a held key is
/// swallowed and sends nothing: one press, one command.
/// </summary>
/// <remarks>Used on one thread (the one that owns the hook); only <see cref="On"/>, <see cref="Configured"/> and <see cref="SetKeys"/> may be called from another.</remarks>
public sealed class PresentationKeyFilter
{
    private readonly bool[] _held = new bool[256];
    private volatile KeyMap _keys = new(PresentationKeys.DefaultNext.ToArray(), PresentationKeys.DefaultPrevious.ToArray());

    /// <summary>Whether the mode is on.</summary>
    public volatile bool On;

    /// <summary>Whether Holyrics is configured (a token is stored); nothing is swallowed without it.</summary>
    public volatile bool Configured;

    /// <summary>Replaces the chosen keys.</summary>
    public void SetKeys(IReadOnlyList<int> next, IReadOnlyList<int> previous) =>
        _keys = new KeyMap(PresentationKeys.Clean(next).ToArray(), PresentationKeys.Clean(previous).ToArray());

    /// <summary>Forgets the keys that are held (the hook was installed again).</summary>
    public void Reset() => Array.Clear(_held);

    /// <summary>
    /// A cheap first look: whether <see cref="Decide"/> could do anything for this event (the mode is on and the key is a chosen one, or the key is held swallowed). The hook
    /// asks this for every key of the keyboard before it spends time on the modifiers and the window in front.
    /// </summary>
    public bool Matters(int virtualKey, bool keyDown)
    {
        if ((uint)virtualKey >= (uint)_held.Length)
        {
            return false;
        }

        if (_held[virtualKey])
        {
            return true;
        }

        if (!keyDown || !On || !Configured)
        {
            return false;
        }

        var map = _keys;
        return Array.IndexOf(map.Next, virtualKey) >= 0 || Array.IndexOf(map.Previous, virtualKey) >= 0;
    }

    /// <summary>Decides one key event.</summary>
    public PresentationVerdict Decide(PresentationKeyEvent e)
    {
        var key = e.VirtualKey;
        if ((uint)key >= (uint)_held.Length)
        {
            return PresentationVerdict.Pass;
        }

        if (!e.KeyDown)
        {
            // The pairing rule: a key whose down was swallowed has its up swallowed, whatever the mode or the modifiers say now.
            if (_held[key])
            {
                _held[key] = false;
                return new PresentationVerdict(true, PresentationKeyAction.None);
            }

            return PresentationVerdict.Pass;
        }

        if (_held[key])
        {
            return new PresentationVerdict(true, PresentationKeyAction.None); // auto-repeat of a swallowed key: still swallowed, sends nothing
        }

        if (!On || !Configured || e.ModifierHeld || e.ForegroundIsOwn)
        {
            return PresentationVerdict.Pass;
        }

        var map = _keys;
        var action = Array.IndexOf(map.Next, key) >= 0
            ? PresentationKeyAction.Next
            : Array.IndexOf(map.Previous, key) >= 0 ? PresentationKeyAction.Previous : PresentationKeyAction.None;
        if (action == PresentationKeyAction.None)
        {
            return PresentationVerdict.Pass;
        }

        _held[key] = true;
        return new PresentationVerdict(true, action);
    }

    private sealed record KeyMap(int[] Next, int[] Previous);
}
