using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using AlertaIBT.App.Overlay;
using AlertaIBT.Core.Screens;
using AlertaIBT.Core.Settings;
using Microsoft.Extensions.Logging;

namespace AlertaIBT.App.Platform;

/// <summary>
/// The two rules every window of the app (not the bar) follows, because the LED is what the congregation sees and what OBS and
/// NDI capture. (1) Capture: the window can be captured by a screenshot (Win+Shift+S, Print Screen) while it is away from the LED
/// monitor, and is excluded from capture (<c>WDA_EXCLUDEFROMCAPTURE</c>) as soon as it overlaps the LED, or always when the setting
/// "Janelas do app aparecem em prints" is off. It is re-evaluated when the window moves, resizes, changes DPI, is shown or activated, when
/// the setting changes and when the displays change; the exclusion happens before the first pixel. (2) Place: it never opens on the LED
/// monitor (the monitor is chosen by <see cref="WindowMonitorChooser"/>: the one under the mouse, else the primary, else any other; only
/// when the LED is the one monitor there is does a window open on it, and then it stays excluded). The bar itself does not use this class:
/// it is always excluded.
/// </summary>
public sealed class AppWindowPolicy
{
    private readonly Func<ScreenInfo?> _led;
    private readonly ILogger? _logger;
    private readonly Func<IReadOnlyList<ScreenInfo>> _screens;
    private readonly Func<(int X, int Y)> _cursor;
    private readonly Func<bool> _inScreenshots;
    private readonly List<Tracked> _tracked = [];
    private bool? _lastLoggedMode;

    /// <param name="led">Gives the LED screen, or <c>null</c> when none is connected.</param>
    /// <param name="logger">Receives a failure to exclude a window from capture and the one line per change of state.</param>
    /// <param name="screens">Lists the monitors; the real ones by default.</param>
    /// <param name="cursor">Gives the mouse position in physical pixels; the real one by default.</param>
    /// <param name="inScreenshots">Whether the app windows may appear in screenshots away from the LED (the setting); on by default.</param>
    public AppWindowPolicy(
        Func<ScreenInfo?> led,
        ILogger? logger = null,
        Func<IReadOnlyList<ScreenInfo>>? screens = null,
        Func<(int X, int Y)>? cursor = null,
        Func<bool>? inScreenshots = null)
    {
        ArgumentNullException.ThrowIfNull(led);

        _led = led;
        _logger = logger;
        _screens = screens ?? MonitorService.Enumerate;
        _cursor = cursor ?? CursorPosition;
        _inScreenshots = inScreenshots ?? (() => true);
    }

    /// <summary>The policy of a window made without one (tests): no LED known, so nothing to avoid, and the windows can be captured.</summary>
    internal static AppWindowPolicy Default { get; } = new(() => null);

    /// <summary>Whether the app windows may be captured while away from the LED (the setting, right now).</summary>
    public bool WindowsInScreenshots => _inScreenshots();

    /// <summary>
    /// Follows the settings (the switch), the LED and the display changes: every open window is re-evaluated, and the mode is logged once now
    /// and once more each time the switch changes. Call it once, on the UI thread, at startup.
    /// </summary>
    public void Attach(SettingsService settings, ILedSource led)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(led);

        LogMode();
        settings.Changed += _ => Refresh();
        led.LedChanged += _ => Refresh();
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (_, _) => Refresh();
    }

    /// <summary>Re-evaluates every open window (any thread: it hops to the UI thread).</summary>
    public void Refresh()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(RefreshCore);
            return;
        }

        RefreshCore();
    }

    /// <summary>The monitor a window opens on now (see the class remarks); <c>null</c> only when Windows lists no monitor.</summary>
    public ScreenInfo? ChooseMonitor()
    {
        var (x, y) = _cursor();
        return WindowMonitorChooser.Choose(_screens(), _led(), x, y);
    }

    /// <summary>
    /// Makes the handle of <paramref name="window"/> now (hidden), puts its capture state right (before its first pixel) and keeps it right
    /// while the window lives. Returns whether the window is excluded from capture right now.
    /// </summary>
    internal bool Protect(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        var tracked = new Tracked(window, hwnd);
        _tracked.Add(tracked);
        Evaluate(tracked, first: true);

        void Check(object? sender, EventArgs e) => Evaluate(tracked, first: false);
        window.LocationChanged += Check;
        window.SizeChanged += (_, _) => Evaluate(tracked, first: false);
        window.DpiChanged += (_, _) => Evaluate(tracked, first: false);
        window.Activated += Check;
        window.StateChanged += Check;
        window.IsVisibleChanged += (_, _) => Evaluate(tracked, first: false);
        window.Closed += (_, _) => _tracked.Remove(tracked);
        return tracked.Excluded;
    }

    /// <summary>The same rule for a popup that has a handle but no <see cref="Window"/> (the tray menu), once, when it opens.</summary>
    internal bool ProtectHandle(IntPtr hwnd)
    {
        if (WantExcluded(hwnd))
        {
            return CaptureExclusion.Apply(hwnd);
        }

        CaptureExclusion.Clear(hwnd);
        return false;
    }

    /// <summary>Re-evaluates one window now (after it was moved by code); the events do it for the user's moves.</summary>
    internal void Reevaluate(Window window)
    {
        if (_tracked.FirstOrDefault(t => ReferenceEquals(t.Window, window)) is { } tracked)
        {
            Evaluate(tracked, first: false);
        }
    }

    // The rule: excluded when the setting is off, or when the window overlaps the LED monitor.
    private bool WantExcluded(IntPtr hwnd) => !_inScreenshots() || IsOnLed(hwnd);

    private void Evaluate(Tracked tracked, bool first)
    {
        var want = WantExcluded(tracked.Handle);
        if (!first && want == tracked.Excluded)
        {
            return; // cheap: one GetWindowRect and a comparison per event
        }

        var ok = want ? CaptureExclusion.Apply(tracked.Handle) : CaptureExclusion.Clear(tracked.Handle);
        if (!ok && want)
        {
            _logger?.LogError(
                "A janela {Window} NAO esta excluida da captura de tela (SetWindowDisplayAffinity falhou): o OBS poderia mostra-la",
                tracked.Window.GetType().Name);
        }

        var was = tracked.Excluded;
        tracked.Excluded = want && ok;
        if (!first && was != tracked.Excluded && _inScreenshots())
        {
            _logger?.LogInformation(
                tracked.Excluded
                    ? "Janela {Window} escondida de prints: esta sobre o monitor do LED"
                    : "Janela {Window} aparece em prints: saiu do monitor do LED",
                tracked.Window.GetType().Name);
        }
    }

    private void RefreshCore()
    {
        LogMode();
        foreach (var tracked in _tracked.ToArray())
        {
            Evaluate(tracked, first: false);
        }
    }

    // One INFO line at startup and one each time the setting flips (the LED rule logs its own line per window).
    private void LogMode()
    {
        var mode = _inScreenshots();
        if (_lastLoggedMode == mode)
        {
            return;
        }

        _lastLoggedMode = mode;
        _logger?.LogInformation(
            mode
                ? "Janelas do app em prints: aparecem em prints, exceto sobre o monitor do LED (a barra fica sempre escondida)"
                : "Janelas do app em prints: sempre escondidas (a barra tambem)");
    }

    private sealed class Tracked(Window window, IntPtr handle)
    {
        public Window Window { get; } = window;

        public IntPtr Handle { get; } = handle;

        public bool Excluded { get; set; }
    }

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
    /// <param name="window">The window; its handle must exist (see <see cref="Protect"/>).</param>
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
