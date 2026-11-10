using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using AlertaIBT.App.Platform;
using AlertaIBT.Core.Diagnostics;
using AlertaIBT.Core.Overlay;
using AlertaIBT.Core.Screens;
using AlertaIBT.Core.Settings;

namespace AlertaIBT.App.Overlay;

/// <summary>
/// The bar over the LED: a borderless, transparent WPF window that is always on top, lets the mouse through, never takes
/// focus, stays out of the taskbar and Alt+Tab, and is excluded from screen capture
/// (<c>WDA_EXCLUDEFROMCAPTURE</c>, applied as soon as the handle exists and checked again before every alert).
/// Create it on the UI thread. It stays hidden between alerts.
/// </summary>
public partial class OverlayWindow : Window, IBarSurface
{
    private readonly DispatcherTimer _topmostTimer;
    private readonly IntPtr _hwnd;
    private BarLayout? _layout;
    private ScreenInfo? _screen;
    private bool _captureExcluded;
    private bool _disposed;
    private int _placements;

    /// <summary>Creates the window and its handle (not shown) and applies the native styles and the capture exclusion.</summary>
    public OverlayWindow()
    {
        InitializeComponent();

        // The handle is made now, hidden, so the styles and the capture exclusion are in place before any pixel is shown.
        _hwnd = new WindowInteropHelper(this).EnsureHandle();
        NativeMethods.AddExStyle(
            _hwnd,
            NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_TOPMOST);
        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
        ApplyCaptureExclusion();

        _topmostTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
        _topmostTimer.Tick += (_, _) => ReassertTopmost();
    }

    /// <inheritdoc />
    public event Action? CaptureExcludedChanged;

    /// <inheritdoc />
    public bool CaptureExcluded => _captureExcluded;

    /// <summary>The native window handle.</summary>
    internal IntPtr Handle => _hwnd;

    /// <summary>The bar with its text, for drawing a frame to a bitmap in tests.</summary>
    internal FrameworkElement BarElement => Bar;

    /// <summary>The layout of the last <see cref="Prepare"/>.</summary>
    internal BarLayout? Layout => _layout;

    /// <summary>The session state that pauses the window work (locked, display off, asleep); tests give it their own.</summary>
    internal SessionState State { get; set; } = SessionState.Default;

    /// <summary>How many times the window was really moved or resized with <c>SetWindowPos</c> (a placement that changes nothing is skipped).</summary>
    internal int Placements => _placements;

    /// <summary>
    /// After the window system failed (error 1816), or when the session is back (unlock, resume, display on): puts the bar where it belongs and repaints it, once.
    /// Does nothing while the bar is hidden (it is placed again at the next alert anyway).
    /// </summary>
    public void Reapply()
    {
        if (_disposed || !IsVisible || _layout is not { } layout)
        {
            return;
        }

        PlaceWindow(layout, force: true);
        InvalidateVisual();
    }

    /// <inheritdoc />
    public PreparedBar Prepare(string text, BarSettings bar, ScreenInfo screen)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(bar);
        ArgumentNullException.ThrowIfNull(screen);
        ObjectDisposedException.ThrowIf(_disposed, this);

        ApplyCaptureExclusion(); // "always on": checked before every alert

        // Placed in physical pixels. Moving to a monitor with another scale makes Windows send WM_DPICHANGED synchronously,
        // so the scale is read again afterwards and, when it changed, the window is placed once more with the right sizes.
        var layout = BarLayout.Compute(screen, bar, VisualTreeHelper.GetDpi(this).DpiScaleX);
        PlaceWindow(layout);
        var dpi = VisualTreeHelper.GetDpi(this);
        if (Math.Abs(dpi.DpiScaleX - layout.DpiScale) > 1e-6)
        {
            layout = BarLayout.Compute(screen, bar, dpi.DpiScaleX);
            PlaceWindow(layout);
        }

        _layout = layout;
        _screen = screen;

        Bar.Background = BrushFrom(bar.Background, Colors.Black);
        var foreground = BrushFrom(bar.Foreground, Color.FromRgb(0xFF, 0xF7, 0xE8));
        Marquee.Layout(
            text,
            MarbleFont.Bold(),
            MarbleFont.Fallback(),
            layout.FontSize,
            BarLayout.LetterSpacingEm,
            layout.PaddingX,
            layout.BarHeight,
            foreground,
            dpi.PixelsPerDip);

        // First frame: the bar slid out and see-through, the text waiting beyond the right edge. No flash of the text.
        Apply(new BarFrame(BarPhase.In, 1, 0, screen.Width));

        if (!IsVisible)
        {
            Show();
        }

        PlaceWindow(layout);
        _topmostTimer.Start();
        return new PreparedBar(Marquee.ContentWidth * layout.DpiScale);
    }

    /// <inheritdoc />
    public void Apply(BarFrame frame)
    {
        var layout = _layout;
        if (layout is null)
        {
            return;
        }

        BarSlide.Y = layout.SlideDistance(frame.Offset);
        Bar.Opacity = Math.Clamp(frame.Opacity, 0, 1);
        TextShift.X = frame.TextX / layout.DpiScale;
    }

    /// <inheritdoc />
    public new void Hide()
    {
        _topmostTimer.Stop();
        if (IsVisible)
        {
            base.Hide();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _topmostTimer.Stop();
        Close();
    }

    // Moves the window only when it is not already there (see PlacementGuard).
    private void PlaceWindow(BarLayout layout, bool force = false)
    {
        var wanted = new PixelRect(layout.WindowX, layout.WindowY, layout.WindowWidth, layout.WindowHeight);
        PixelRect? actual = NativeMethods.GetWindowRect(_hwnd, out var rect)
            ? new PixelRect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top)
            : null;
        if (!force && !PlacementGuard.NeedsMove(actual, wanted))
        {
            return;
        }

        _placements++;
        NativeMethods.SetWindowPos(
            _hwnd,
            NativeMethods.HWND_TOPMOST,
            wanted.X,
            wanted.Y,
            wanted.Width,
            wanted.Height,
            NativeMethods.SWP_NOACTIVATE);
    }

    // Every 2 s while the bar is visible: put it back on top, but only when something visible and topmost really sits above it (not while nobody can see the
    // screen: locked, display off, asleep), so the window system is not asked for a z-order change that changes nothing.
    internal void ReassertTopmost()
    {
        if (IsVisible && !State.ShouldPauseWindowWork && CoveredByTopmostWindow())
        {
            _placements++;
            NativeMethods.SetWindowPos(
                _hwnd,
                NativeMethods.HWND_TOPMOST,
                0,
                0,
                0,
                0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
        }
    }

    // A visible window with the topmost style above this one in the z-order (the first one found is enough).
    private bool CoveredByTopmostWindow()
    {
        var above = NativeMethods.GetWindow(_hwnd, NativeMethods.GW_HWNDPREV);
        for (var steps = 0; above != IntPtr.Zero && steps < 64; steps++)
        {
            if (NativeMethods.IsWindowVisible(above) && (NativeMethods.GetExStyle(above) & NativeMethods.WS_EX_TOPMOST) != 0)
            {
                return true;
            }

            above = NativeMethods.GetWindow(above, NativeMethods.GW_HWNDPREV);
        }

        return false;
    }

    // Excludes the window from every kind of screen capture and checks that Windows really took it.
    private void ApplyCaptureExclusion()
    {
        var applied = CaptureExclusion.Apply(_hwnd);
        if (applied != _captureExcluded)
        {
            _captureExcluded = applied;
            CaptureExcludedChanged?.Invoke();
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (message)
        {
            case NativeMethods.WM_MOUSEACTIVATE:
                handled = true;
                return (IntPtr)NativeMethods.MA_NOACTIVATE;
            case NativeMethods.WM_NCHITTEST:
                handled = true;
                return (IntPtr)NativeMethods.HTTRANSPARENT;
            default:
                return IntPtr.Zero;
        }
    }

    private static SolidColorBrush BrushFrom(string text, Color fallback)
    {
        Color color;
        try
        {
            color = ColorConverter.ConvertFromString(text) is Color parsed ? parsed : fallback;
        }
        catch (FormatException)
        {
            color = fallback;
        }

        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
