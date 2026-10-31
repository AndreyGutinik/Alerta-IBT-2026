using System.Runtime.InteropServices;

namespace AlertaIBT.App.Platform;

/// <summary>Decides, inside the hook, whether a key event is swallowed. Must be fast and must not call anything slow, touch the UI or throw.</summary>
/// <param name="virtualKey">The virtual-key code.</param>
/// <param name="keyDown">True for a key going down, false for it coming up.</param>
/// <returns>True to swallow the event: the program in front never sees it.</returns>
public delegate bool KeyConsumer(int virtualKey, bool keyDown);

/// <summary>A global keyboard hook that can swallow keys. The real one is <see cref="SwallowingKeyboardHook"/>; tests use a fake.</summary>
public interface IKeyboardGate : IDisposable
{
    /// <summary>Whether the hook is installed.</summary>
    bool IsInstalled { get; }

    /// <summary>Installs the hook; <paramref name="consumer"/> decides every key event. False when Windows refuses.</summary>
    bool Install(KeyConsumer consumer);

    /// <summary>
    /// Installs a fresh hook and only then removes the old one (no gap), so a hook that Windows silently dropped (it does that to a hook that was too slow once)
    /// is replaced. False when the new one could not be installed (the old one is kept).
    /// </summary>
    bool Refresh();

    /// <summary>Removes the hook. Does nothing when it is not installed.</summary>
    void Uninstall();
}

/// <summary>
/// A second <c>WH_KEYBOARD_LL</c> hook, separate from the passive one of <see cref="LowLevelKeyboardHook"/> (Ctrl x3, media keys), which stays as it is. This one exists
/// only while the presentation mode is on, so when the mode is off nothing of it runs. Its callback calls the <see cref="KeyConsumer"/> and returns 1 (the event goes
/// no further) when it says yes, else passes the event on. It reads three integers of the event (no allocation) and catches every exception (a failure passes the key
/// on). Install it on a thread with a message loop (the UI thread).
/// </summary>
public sealed class SwallowingKeyboardHook : IKeyboardGate
{
    private readonly NativeMethods.LowLevelKeyboardProc _proc;
    private IntPtr _hook;
    private KeyConsumer? _consumer;

    /// <summary>Creates the hook object (not installed).</summary>
    public SwallowingKeyboardHook() => _proc = Callback; // a field: the delegate must live as long as the hook

    /// <inheritdoc />
    public bool IsInstalled => _hook != IntPtr.Zero;

    /// <inheritdoc />
    public bool Install(KeyConsumer consumer)
    {
        ArgumentNullException.ThrowIfNull(consumer);

        if (IsInstalled)
        {
            _consumer = consumer;
            return true;
        }

        _consumer = consumer;
        _hook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, _proc, NativeMethods.GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero)
        {
            _consumer = null;
            return false;
        }

        return true;
    }

    /// <inheritdoc />
    public bool Refresh()
    {
        var old = _hook;
        if (old == IntPtr.Zero || _consumer is null)
        {
            return false;
        }

        var fresh = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, _proc, NativeMethods.GetModuleHandle(null), 0);
        if (fresh == IntPtr.Zero)
        {
            return false;
        }

        _hook = fresh;
        NativeMethods.UnhookWindowsHookEx(old);
        return true;
    }

    /// <inheritdoc />
    public void Uninstall()
    {
        var hook = _hook;
        _hook = IntPtr.Zero;
        _consumer = null;
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
                if ((down || message is NativeMethods.WM_KEYUP or NativeMethods.WM_SYSKEYUP) && _consumer is { } consumer)
                {
                    // KBDLLHOOKSTRUCT: vkCode at 0. Nothing else is read.
                    if (consumer(Marshal.ReadInt32(lParam, 0), down))
                    {
                        return 1;
                    }
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
