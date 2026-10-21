using AlertaIBT.Core.Input;

namespace AlertaIBT.App.Platform;

/// <summary>
/// The global hotkey that opens the quick panel (<c>Ctrl+Alt+Space</c> by default, from the settings). Registering can
/// fail when another program already owns the combination; the caller logs it and the tray click still works.
/// </summary>
public sealed class HotkeyService : IHotkeyRegistrar, IDisposable
{
    private const int QuickPanelHotkeyId = 0xB11;

    private readonly MessageWindow _window;
    private bool _registered;

    /// <param name="window">The hidden window that receives <c>WM_HOTKEY</c>.</param>
    public HotkeyService(MessageWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);

        _window = window;
        _window.HotkeyPressed += OnHotkey;
    }

    /// <summary>Raised on the UI thread when the quick-panel hotkey is pressed.</summary>
    public event Action? QuickPanelPressed;

    /// <summary>The hotkey currently registered, or null when none is.</summary>
    public HotkeyGesture? Current { get; private set; }

    /// <summary>
    /// Registers <paramref name="gesture"/> for the quick panel, replacing the previous one. Returns false (and leaves no
    /// hotkey) when Windows refuses, usually because another program owns the combination.
    /// </summary>
    public bool Register(HotkeyGesture gesture)
    {
        ArgumentNullException.ThrowIfNull(gesture);

        Unregister();
        if (!NativeMethods.RegisterHotKey(_window.Handle, QuickPanelHotkeyId, (uint)gesture.Modifiers | NativeMethods.MOD_NOREPEAT, (uint)gesture.VirtualKey))
        {
            return false;
        }

        _registered = true;
        Current = gesture;
        return true;
    }

    /// <summary>Gives the hotkey back.</summary>
    public void Unregister()
    {
        if (_registered)
        {
            NativeMethods.UnregisterHotKey(_window.Handle, QuickPanelHotkeyId);
            _registered = false;
        }

        Current = null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _window.HotkeyPressed -= OnHotkey;
        Unregister();
    }

    private void OnHotkey(int id)
    {
        if (id == QuickPanelHotkeyId)
        {
            QuickPanelPressed?.Invoke();
        }
    }
}
