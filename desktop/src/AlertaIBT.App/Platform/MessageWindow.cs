using System.Windows.Interop;

namespace AlertaIBT.App.Platform;

/// <summary>
/// Hidden top-level window (not message-only, so it also gets broadcasts) that receives
/// WM_HOTKEY and display changes. Create it on the UI thread.
/// </summary>
public sealed class MessageWindow : IDisposable
{
    private readonly HwndSource _source;

    /// <summary>Creates the hidden window; call it on the UI thread.</summary>
    public MessageWindow()
    {
        var parameters = new HwndSourceParameters("AlertaIBT.MessageWindow")
        {
            WindowStyle = NativeMethods.WS_POPUP,
            Width = 0,
            Height = 0,
            PositionX = -32000,
            PositionY = -32000,
        };
        _source = new HwndSource(parameters);
        _source.AddHook(Hook);
    }

    /// <summary>Raised with the id of a registered hotkey when it is pressed.</summary>
    public event Action<int>? HotkeyPressed;

    /// <summary>Raised when monitors or their resolution change (<c>WM_DISPLAYCHANGE</c>) or the work area changes.</summary>
    public event Action? DisplayChanged;

    /// <summary>The window handle, for <c>RegisterHotKey</c>.</summary>
    public IntPtr Handle => _source.Handle;

    /// <inheritdoc />
    public void Dispose()
    {
        _source.RemoveHook(Hook);
        _source.Dispose();
    }

    private IntPtr Hook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (message)
        {
            case NativeMethods.WM_HOTKEY:
                HotkeyPressed?.Invoke(wParam.ToInt32());
                handled = true;
                break;
            case NativeMethods.WM_DISPLAYCHANGE:
                DisplayChanged?.Invoke();
                break;
            case NativeMethods.WM_SETTINGCHANGE when wParam.ToInt32() == NativeMethods.SPI_SETWORKAREA:
                DisplayChanged?.Invoke();
                break;
        }

        return IntPtr.Zero;
    }
}
