using System.Runtime.InteropServices;
using AlertaIBT.Core.Screens;

namespace AlertaIBT.App.Platform;

/// <summary>The connected monitors in physical pixels, with the stable device path of each (adapted from App Flash).</summary>
public static class MonitorService
{
    /// <summary>Lists the monitors Windows reports now, in the order it lists them.</summary>
    public static IReadOnlyList<ScreenInfo> Enumerate()
    {
        var screens = new List<ScreenInfo>();
        NativeMethods.MonitorEnumProc callback = (IntPtr monitor, IntPtr hdc, ref NativeMethods.RECT rect, IntPtr data) =>
        {
            var info = new NativeMethods.MONITORINFOEX { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFOEX>(), szDevice = string.Empty };
            if (NativeMethods.GetMonitorInfo(monitor, ref info))
            {
                var bounds = info.rcMonitor;
                screens.Add(new ScreenInfo(
                    DevicePathFor(info.szDevice),
                    info.szDevice,
                    bounds.Left,
                    bounds.Top,
                    bounds.Right - bounds.Left,
                    bounds.Bottom - bounds.Top,
                    (info.dwFlags & NativeMethods.MONITORINFOF_PRIMARY) != 0));
            }

            return true;
        };
        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
        GC.KeepAlive(callback);
        return screens;
    }

    private static string DevicePathFor(string adapterName)
    {
        var device = NewDisplayDevice();
        for (uint index = 0; NativeMethods.EnumDisplayDevices(adapterName, index, ref device, NativeMethods.EDD_GET_DEVICE_INTERFACE_NAME); index++)
        {
            if ((device.StateFlags & NativeMethods.DISPLAY_DEVICE_ACTIVE) != 0 && !string.IsNullOrEmpty(device.DeviceID))
            {
                return device.DeviceID;
            }

            device = NewDisplayDevice();
        }

        return string.Empty;
    }

    private static NativeMethods.DISPLAY_DEVICE NewDisplayDevice() => new()
    {
        cb = Marshal.SizeOf<NativeMethods.DISPLAY_DEVICE>(),
        DeviceName = string.Empty,
        DeviceString = string.Empty,
        DeviceID = string.Empty,
        DeviceKey = string.Empty,
    };
}
