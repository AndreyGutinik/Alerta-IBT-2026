namespace AlertaIBT.Core.Input;

/// <summary>
/// Recognizes "Ctrl pressed three times quickly" from a stream of key events. It is a tiny state machine on timestamps: it never
/// stores which keys were pressed (only a count, one time and two held flags), so it can sit behind a low-level keyboard hook
/// without recording anything and without taking long. Three presses of Ctrl (either one, in any mix) must start within
/// <see cref="WindowMs"/> of the first, with no other key pressed in between. Holding Ctrl (auto-repeat) is one press, Ctrl+C and
/// Ctrl+Alt+... never count, and after the third press the count starts over (a fourth press is the first of a new sequence).
/// Not thread-safe: feed it from one thread (the one that owns the hook).
/// </summary>
public sealed class TripleCtrlDetector
{
    /// <summary>How long, from the first press to the third, the three presses may take.</summary>
    public const int WindowMs = 900;

    /// <summary>Virtual-key code of Ctrl in general.</summary>
    public const int VkControl = 0x11;

    /// <summary>Virtual-key code of the left Ctrl.</summary>
    public const int VkLeftControl = 0xA2;

    /// <summary>Virtual-key code of the right Ctrl.</summary>
    public const int VkRightControl = 0xA3;

    private int _count;
    private uint _firstMs;
    private bool _leftHeld;
    private bool _rightHeld;

    /// <summary>How many presses of the current sequence were seen (0 to 2). For tests.</summary>
    public int Count => _count;

    /// <summary>Forgets everything (the hook was removed and put back, for instance).</summary>
    public void Reset()
    {
        _count = 0;
        _leftHeld = false;
        _rightHeld = false;
    }

    /// <summary>Feeds one key event.</summary>
    /// <param name="virtualKey">The virtual-key code of the key.</param>
    /// <param name="keyDown">True for a key going down, false for it coming up.</param>
    /// <param name="altDown">Whether Alt is held (the flag Windows puts on every event), so Ctrl+Alt+... never counts.</param>
    /// <param name="timeMs">The time of the event in milliseconds (a 32-bit tick count that may wrap around).</param>
    /// <returns>True exactly when this event is the third quick press of Ctrl.</returns>
    public bool Feed(int virtualKey, bool keyDown, bool altDown, uint timeMs)
    {
        if (virtualKey is not (VkControl or VkLeftControl or VkRightControl))
        {
            if (keyDown)
            {
                // Another key broke the sequence. Both held flags go too: a Ctrl released while Windows hid it from us (secure
                // desktop) must not leave the detector deaf, and a Ctrl that stays held stops auto-repeating once another key is pressed.
                _count = 0;
                _leftHeld = false;
                _rightHeld = false;
            }

            return false;
        }

        var right = virtualKey == VkRightControl;
        if (!keyDown)
        {
            if (right)
            {
                _rightHeld = false;
            }
            else
            {
                _leftHeld = false;
            }

            return false;
        }

        if (right ? _rightHeld : _leftHeld)
        {
            return false; // auto-repeat of a Ctrl that is still down
        }

        if (right)
        {
            _rightHeld = true;
        }
        else
        {
            _leftHeld = true;
        }

        if (altDown)
        {
            _count = 0; // Ctrl+Alt+...: a shortcut, not a triple press
            return false;
        }

        if (_count > 0 && unchecked(timeMs - _firstMs) > WindowMs)
        {
            _count = 0; // too slow: this press starts a new sequence
        }

        if (_count == 0)
        {
            _firstMs = timeMs;
        }

        _count++;
        if (_count < 3)
        {
            return false;
        }

        _count = 0;
        return true;
    }
}
