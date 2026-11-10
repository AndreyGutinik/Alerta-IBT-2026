using System.Runtime.InteropServices;
using AlertaIBT.Core.Diagnostics;
using System.Windows.Interop;

namespace AlertaIBT.App.Platform;

/// <summary>
/// Hidden top-level window (not message-only, so it also gets broadcasts) that receives
/// WM_HOTKEY, display changes and the display power state (on, off, dimmed). Create it on the UI thread.
/// </summary>
public sealed class MessageWindow : IDisposable
{
    private readonly HwndSource _source;
    private IntPtr _displayPowerNotification;

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

    /// <summary>Raised when the display goes on, off or dim (only after <see cref="WatchDisplayPower"/>).</summary>
    public event Action<DisplayPower>? DisplayPowerChanged;

    /// <summary>The window handle, for <c>RegisterHotKey</c>.</summary>
    public IntPtr Handle => _source.Handle;

    /// <summary>Asks Windows to tell this window when the display goes on or off (GUID_CONSOLE_DISPLAY_STATE). False when Windows refuses; nothing else depends on it.</summary>
    public bool WatchDisplayPower()
    {
        if (_displayPowerNotification != IntPtr.Zero)
        {
            return true;
        }

        var setting = NativeMethods.ConsoleDisplayState;
        _displayPowerNotification = NativeMethods.RegisterPowerSettingNotification(_source.Handle, ref setting, NativeMethods.DEVICE_NOTIFY_WINDOW_HANDLE);
        return _displayPowerNotification != IntPtr.Zero;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_displayPowerNotification != IntPtr.Zero)
        {
            NativeMethods.UnregisterPowerSettingNotification(_displayPowerNotification);
            _displayPowerNotification = IntPtr.Zero;
        }

        _source.RemoveHook(Hook);
        _source.Dispose();
    }

    /// <summary>The display state of a <c>PBT_POWERSETTINGCHANGE</c> value: 0 off, 1 on, 2 dimmed (anything else is unknown).</summary>
    internal static DisplayPower DisplayPowerOf(int value) => value switch
    {
        0 => DisplayPower.Off,
        1 => DisplayPower.On,
        2 => DisplayPower.Dimmed,
        _ => DisplayPower.Unknown,
    };

    private IntPtr Hook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (message)
        {
            case NativeMethods.WM_HOTKEY:
                Breadcrumbs.Add("atalho global " + wParam.ToInt32().ToString("X", System.Globalization.CultureInfo.InvariantCulture));
                HotkeyPressed?.Invoke(wParam.ToInt32());
                handled = true;
                break;
            case NativeMethods.WM_DISPLAYCHANGE:
                DisplayChanged?.Invoke();
                break;
            case NativeMethods.WM_SETTINGCHANGE when wParam.ToInt32() == NativeMethods.SPI_SETWORKAREA:
                DisplayChanged?.Invoke();
                break;
            case NativeMethods.WM_POWERBROADCAST when wParam.ToInt32() == NativeMethods.PBT_POWERSETTINGCHANGE && lParam != IntPtr.Zero:
                ReadDisplayPower(lParam);
                break;
        }

        return IntPtr.Zero;
    }

    // POWERBROADCAST_SETTING: a GUID (16 bytes), the data length (4 bytes), then the data (here one integer).
    private void ReadDisplayPower(IntPtr lParam)
    {
        try
        {
            var setting = Marshal.PtrToStructure<Guid>(lParam);
            if (setting != NativeMethods.ConsoleDisplayState || Marshal.ReadInt32(lParam, 16) < sizeof(int))
            {
                return;
            }

            var power = DisplayPowerOf(Marshal.ReadInt32(lParam, 20));
            Breadcrumbs.Add("tela: " + power);
            DisplayPowerChanged?.Invoke(power);
        }
        catch (Exception ex) when (ex is ArgumentException or AccessViolationException or NullReferenceException)
        {
            // A malformed notification is ignored: the display state stays what it was.
        }
    }
}
