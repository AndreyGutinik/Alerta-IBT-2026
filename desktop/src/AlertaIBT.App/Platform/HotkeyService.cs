using AlertaIBT.Core.Input;

namespace AlertaIBT.App.Platform;

/// <summary>
/// The global hotkey that opens the quick panel (<c>Ctrl+Alt+Space</c> by default, from the settings). Registering can
/// fail when another program already owns the combination; the caller logs it and the tray click still works.
/// </summary>
public sealed class HotkeyService : IHotkeyRegistrar, IDisposable
{
    /// <summary>The id of the quick-panel hotkey.</summary>
    public const int QuickPanelHotkeyId = 0xB11;

    /// <summary>The id of the mouse-lock hotkey.</summary>
    public const int MouseLockHotkeyId = 0xB12;

    private readonly MessageWindow _window;
    private readonly int _id;
    private bool _registered;

    /// <param name="window">The hidden window that receives <c>WM_HOTKEY</c>.</param>
    /// <param name="id">The hotkey id: each hotkey of the app has its own, on the same window.</param>
    public HotkeyService(MessageWindow window, int id = QuickPanelHotkeyId)
    {
        ArgumentNullException.ThrowIfNull(window);

        _window = window;
        _id = id;
        _window.HotkeyPressed += OnHotkey;
    }

    /// <summary>Raised on the UI thread when the hotkey is pressed.</summary>
    public event Action? Pressed;

    /// <summary>Raised on the UI thread when the quick-panel hotkey is pressed (the same event as <see cref="Pressed"/>).</summary>
    public event Action? QuickPanelPressed
    {
        add => Pressed += value;
        remove => Pressed -= value;
    }

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
        if (!NativeMethods.RegisterHotKey(_window.Handle, _id, (uint)gesture.Modifiers | NativeMethods.MOD_NOREPEAT, (uint)gesture.VirtualKey))
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
            NativeMethods.UnregisterHotKey(_window.Handle, _id);
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
        if (id == _id)
        {
            Pressed?.Invoke();
        }
    }
}
