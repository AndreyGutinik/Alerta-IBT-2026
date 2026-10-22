using System.Runtime.InteropServices;

namespace AlertaIBT.App.Platform;

/// <summary>Receives one key event from the keyboard hook: nothing but the key code, up or down, Alt held and the time.</summary>
/// <param name="virtualKey">The virtual-key code.</param>
/// <param name="keyDown">True for a key going down, false for it coming up.</param>
/// <param name="altDown">Whether Alt is held.</param>
/// <param name="timeMs">The event time in milliseconds (32-bit tick count).</param>
public delegate void KeyObserver(int virtualKey, bool keyDown, bool altDown, uint timeMs);

/// <summary>A global keyboard hook. The real one is <see cref="LowLevelKeyboardHook"/>; tests use a fake.</summary>
public interface IKeyboardHook : IDisposable
{
    /// <summary>Whether the hook is installed.</summary>
    bool IsInstalled { get; }

    /// <summary>Installs the hook; <paramref name="observer"/> hears every key event. False when Windows refuses.</summary>
    bool Install(KeyObserver observer);

    /// <summary>Removes the hook. Does nothing when it is not installed.</summary>
    void Uninstall();
}

/// <summary>
/// A <c>WH_KEYBOARD_LL</c> hook, here only so the app can notice Ctrl pressed three times (see <c>TripleCtrlDetector</c>). It is passive: it
/// always calls the next hook, never swallows a key, reads four integers of the event (no allocation, no <c>PtrToStructure</c>) and hands
/// them to the observer, which keeps no key anywhere. It must be installed on a thread with a message loop (the UI thread) and, being
/// called on that thread, must stay far below the system's hook time limit: an exception is caught and ignored, never thrown into Windows.
/// </summary>
public sealed class LowLevelKeyboardHook : IKeyboardHook
{
    private readonly NativeMethods.LowLevelKeyboardProc _proc;
    private IntPtr _hook;
    private KeyObserver? _observer;

    /// <summary>Creates the hook object (not installed).</summary>
    public LowLevelKeyboardHook() => _proc = Callback; // a field: the delegate must live as long as the hook

    /// <inheritdoc />
    public bool IsInstalled => _hook != IntPtr.Zero;

    /// <inheritdoc />
    public bool Install(KeyObserver observer)
    {
        ArgumentNullException.ThrowIfNull(observer);

        if (IsInstalled)
        {
            _observer = observer;
            return true;
        }

        _observer = observer;
        _hook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, _proc, NativeMethods.GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero)
        {
            _observer = null;
            return false;
        }

        return true;
    }

    /// <inheritdoc />
    public void Uninstall()
    {
        var hook = _hook;
        _hook = IntPtr.Zero;
        _observer = null;
        if (hook != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(hook);
        }
    }

    /// <inheritdoc />
    public void Dispose() => Uninstall();

    private IntPtr Callback(int code, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (code == NativeMethods.HC_ACTION)
            {
                var message = wParam.ToInt32();
                var down = message is NativeMethods.WM_KEYDOWN or NativeMethods.WM_SYSKEYDOWN;
                if (down || message is NativeMethods.WM_KEYUP or NativeMethods.WM_SYSKEYUP)
                {
                    // KBDLLHOOKSTRUCT: vkCode at 0, scanCode at 4, flags at 8, time at 12. Nothing else is read.
                    var virtualKey = Marshal.ReadInt32(lParam, 0);
                    var flags = Marshal.ReadInt32(lParam, 8);
                    var time = unchecked((uint)Marshal.ReadInt32(lParam, 12));
                    _observer?.Invoke(virtualKey, down, (flags & NativeMethods.LLKHF_ALTDOWN) != 0, time);
                }
            }
        }
        catch (Exception)
        {
            // Never let an exception cross into Windows: the key goes on as if the hook were not there.
        }

        return NativeMethods.CallNextHookEx(_hook, code, wParam, lParam);
    }
}
