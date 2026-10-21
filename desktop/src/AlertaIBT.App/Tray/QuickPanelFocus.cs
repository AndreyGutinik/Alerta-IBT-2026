using System.Text;
using AlertaIBT.App.Platform;

namespace AlertaIBT.App.Tray;

/// <summary>
/// Decides which window gets the keyboard back when the quick panel closes by itself (Esc, a send, the hotkey again). After a
/// click on the tray icon the foreground window is the taskbar: giving the focus back to that would be worse than letting
/// Windows pick the program the user was in, so shell windows are never "restored".
/// </summary>
internal static class QuickPanelFocus
{
    /// <summary>Whether a window of this class belongs to the Windows shell (taskbar, notification-area overflow, desktop).</summary>
    public static bool IsShellWindow(string? className)
    {
        if (string.IsNullOrEmpty(className))
        {
            return true; // unknown: do not fight for it
        }

        return className.StartsWith("Shell_", StringComparison.Ordinal)
            || className.Contains("Overflow", StringComparison.Ordinal)
            || className.Contains("XamlExplorerHostIslandWindow", StringComparison.Ordinal)
            || className is "Progman" or "WorkerW";
    }

    /// <summary>The window to give the focus back to later, or zero when there is none worth restoring.</summary>
    /// <param name="foreground">The foreground window when the panel opened.</param>
    /// <param name="panel">The panel's own window.</param>
    public static IntPtr WindowToRestore(IntPtr foreground, IntPtr panel)
    {
        if (foreground == IntPtr.Zero || foreground == panel)
        {
            return IntPtr.Zero;
        }

        return IsShellWindow(ClassOf(foreground)) ? IntPtr.Zero : foreground;
    }

    private static string ClassOf(IntPtr window)
    {
        var buffer = new StringBuilder(256);
        return NativeMethods.GetClassName(window, buffer, buffer.Capacity) > 0 ? buffer.ToString() : string.Empty;
    }
}
