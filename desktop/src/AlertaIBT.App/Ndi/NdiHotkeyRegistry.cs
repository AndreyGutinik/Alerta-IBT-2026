using AlertaIBT.App.Platform;
using AlertaIBT.Core.Input;
using AlertaIBT.Core.Ndi;

namespace AlertaIBT.App.Ndi;

/// <summary>Registers the NDI shortcuts (one id each) on the app's hidden message window and forwards the presses.</summary>
public sealed class NdiHotkeyRegistry : INdiHotkeyRegistry, IDisposable
{
    private readonly MessageWindow _window;
    private readonly HashSet<int> _registered = [];

    /// <param name="window">The hidden window that receives <c>WM_HOTKEY</c>.</param>
    public NdiHotkeyRegistry(MessageWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);

        _window = window;
        _window.HotkeyPressed += OnPressed;
    }

    /// <summary>Raised on the UI thread with the id of a pressed shortcut of this registry.</summary>
    public event Action<int>? Pressed;

    /// <inheritdoc />
    public bool Register(int id, HotkeyModifiers modifiers, int virtualKey)
    {
        if (!NativeMethods.RegisterHotKey(_window.Handle, id, (uint)modifiers | NativeMethods.MOD_NOREPEAT, (uint)virtualKey))
        {
            return false;
        }

        _registered.Add(id);
        return true;
    }

    /// <inheritdoc />
    public void Unregister(int id)
    {
        if (_registered.Remove(id))
        {
            NativeMethods.UnregisterHotKey(_window.Handle, id);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _window.HotkeyPressed -= OnPressed;
        foreach (var id in _registered.ToList())
        {
            Unregister(id);
        }
    }

    private void OnPressed(int id)
    {
        if (_registered.Contains(id))
        {
            Pressed?.Invoke(id);
        }
    }
}
