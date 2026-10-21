using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using AlertaIBT.Core.Screens;
using Microsoft.Extensions.Logging;

namespace AlertaIBT.App.Platform;

/// <summary>
/// The two rules every window of the app (not the bar) follows, because the LED is what the congregation sees and what OBS and
/// NDI capture: it is excluded from screen capture before its first pixel, and it never opens on the LED monitor (the monitor
/// is chosen by <see cref="WindowMonitorChooser"/>: the one under the mouse, else the primary, else any other; only when the LED
/// is the one monitor there is does a window open on it, hidden from capture anyway).
/// </summary>
public sealed class AppWindowPolicy
{
    private readonly Func<ScreenInfo?> _led;
    private readonly ILogger? _logger;
    private readonly Func<IReadOnlyList<ScreenInfo>> _screens;
    private readonly Func<(int X, int Y)> _cursor;

    /// <param name="led">Gives the LED screen, or <c>null</c> when none is connected.</param>
    /// <param name="logger">Receives a failure to exclude a window from capture.</param>
    /// <param name="screens">Lists the monitors; the real ones by default.</param>
    /// <param name="cursor">Gives the mouse position in physical pixels; the real one by default.</param>
    public AppWindowPolicy(
        Func<ScreenInfo?> led,
        ILogger? logger = null,
        Func<IReadOnlyList<ScreenInfo>>? screens = null,
        Func<(int X, int Y)>? cursor = null)
    {
        ArgumentNullException.ThrowIfNull(led);

        _led = led;
        _logger = logger;
        _screens = screens ?? MonitorService.Enumerate;
        _cursor = cursor ?? CursorPosition;
    }

    /// <summary>The policy of a window made without one (tests): no LED known, so nothing to avoid; capture exclusion still applies.</summary>
    internal static AppWindowPolicy Default { get; } = new(() => null);

    /// <summary>The monitor a window opens on now (see the class remarks); <c>null</c> only when Windows lists no monitor.</summary>
    public ScreenInfo? ChooseMonitor()
    {
        var (x, y) = _cursor();
        return WindowMonitorChooser.Choose(_screens(), _led(), x, y);
    }

    /// <summary>Excludes <paramref name="window"/> from capture before it is first shown.</summary>
    internal bool Exclude(Window window) => CaptureExclusion.Apply(window, _logger);

    /// <summary>Whether the window (hidden or not) is, even partly, on the LED monitor.</summary>
    internal bool IsOnLed(IntPtr hwnd) =>
        NativeMethods.GetWindowRect(hwnd, out var rect)
        && WindowMonitorChooser.OverlapsLed(_led(), rect.Left, rect.Top, rect.Right, rect.Bottom);

    /// <summary>Puts the window in the bottom right corner of the work area of the chosen monitor (where the tray lives).</summary>
    internal void PlaceBottomRight(IntPtr hwnd)
    {
        if (WorkAreaOfChosen() is not { } work || !NativeMethods.GetWindowRect(hwnd, out var rect))
        {
            return;
        }

        Move(hwnd, work.Right - (rect.Right - rect.Left), work.Bottom - (rect.Bottom - rect.Top));
    }

    /// <summary>Centers the window in the work area of the chosen monitor.</summary>
    internal void CenterOnChosen(IntPtr hwnd)
    {
        if (WorkAreaOfChosen() is not { } work || !NativeMethods.GetWindowRect(hwnd, out var rect))
        {
            return;
        }

        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        Move(
            hwnd,
            Math.Max(work.Left, work.Left + ((work.Right - work.Left - width) / 2)),
            Math.Max(work.Top, work.Top + ((work.Bottom - work.Top - height) / 2)));
    }

    /// <summary>
    /// Brings a window that is on the LED, or that never had a place, to the chosen monitor, centered. A window that is already
    /// on another monitor stays where the user left it.
    /// </summary>
    /// <param name="window">The window; its handle must exist (see <see cref="CaptureExclusion.Apply(Window, ILogger?)"/>).</param>
    /// <param name="neverPlaced">Whether this is the first time it opens.</param>
    internal void KeepOffLed(Window window, bool neverPlaced)
    {
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        if (neverPlaced || (IsOnLed(hwnd) && !ChosenIsLed()))
        {
            CenterOnChosen(hwnd);
        }
    }

    // Only the LED exists: moving a window "off" it would go nowhere.
    private bool ChosenIsLed() =>
        ChooseMonitor() is { } screen
        && WindowMonitorChooser.OverlapsLed(_led(), screen.X, screen.Y, screen.X + screen.Width, screen.Y + screen.Height);

    private NativeMethods.RECT? WorkAreaOfChosen()
    {
        if (ChooseMonitor() is not { } screen)
        {
            return null;
        }

        var center = new NativeMethods.POINT { X = screen.X + (screen.Width / 2), Y = screen.Y + (screen.Height / 2) };
        var monitor = NativeMethods.MonitorFromPoint(center, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var info = new NativeMethods.MONITORINFOEX { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFOEX>(), szDevice = string.Empty };
        return NativeMethods.GetMonitorInfo(monitor, ref info) ? info.rcWork : null;
    }

    private static void Move(IntPtr hwnd, int x, int y) =>
        NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);

    private static (int X, int Y) CursorPosition() =>
        NativeMethods.GetCursorPos(out var point) ? (point.X, point.Y) : (0, 0);
}
