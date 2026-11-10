namespace AlertaIBT.Core.Input;

/// <summary>
/// The one place that says how a global shortcut is registered with <c>RegisterHotKey</c>: always with <c>MOD_NOREPEAT</c>, so a key that is held down (keyboard auto-repeat)
/// sends ONE <c>WM_HOTKEY</c> and not thirty a second. Every shortcut of the app is a one-shot command (camera 1-4 to Preview, Take, the quick panel, the mouse lock, the
/// presentation mode): a held key must never repeat it. The keys seen by a keyboard hook (Ctrl x3, the media keys, the presentation keys) have their own held-key state in
/// their detectors.
/// </summary>
public static class HotkeyFlags
{
    /// <summary>MOD_NOREPEAT: Windows 7 and later send the hotkey once while the key stays down.</summary>
    public const uint NoRepeat = 0x4000;

    /// <summary>The modifier bits to hand to <c>RegisterHotKey</c> for <paramref name="modifiers"/>: the modifiers themselves plus <see cref="NoRepeat"/>.</summary>
    public static uint ForRegistration(HotkeyModifiers modifiers) => (uint)modifiers | NoRepeat;
}
