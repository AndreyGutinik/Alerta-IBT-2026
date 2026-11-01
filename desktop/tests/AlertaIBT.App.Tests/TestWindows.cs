using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;

namespace AlertaIBT.App.Tests;

/// <summary>
/// Where the windows of the tests go. Batch 39: a test that only needs a window to measure, arrange or render (the layout tests, the render-to-PNG
/// tests, the dialogs and the quick panel) shows it OFF-SCREEN, never activated and without a taskbar button, so nothing appears on the monitors of the
/// person who runs the suite. A test that really needs the screen (the real mouse, the real pixels of the NDI window, the work-area placement; the
/// <see cref="RealDesktopCollection"/>) uses <see cref="PlaceOnScreen"/>: one fixed corner of the work area of the primary monitor, never cascading.
/// <see cref="WindowGuard"/> checks all of it for the whole run.
/// </summary>
internal static class TestWindows
{
    /// <summary>
    /// Far to the left of every monitor. Not -32000: Windows uses -32000,-32000 for minimized windows. The top stays inside the vertical range of the primary
    /// monitor so Windows gives the window that monitor's DPI (a window nowhere near a monitor takes the DPI of the nearest one, and the layout would change).
    /// </summary>
    public const double OffScreenLeft = -20000;

    /// <summary>The top of an off-screen window (see <see cref="OffScreenLeft"/>).</summary>
    public const double OffScreenTop = 100;

    /// <summary>How far from the top-left corner of the work area a real-desktop test window sits.</summary>
    public const double CornerMargin = 20;

    /// <summary>Makes <paramref name="window"/> appear off every monitor, without taking the focus or a taskbar button, once it is shown.</summary>
    public static void PlaceOffScreen(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = OffScreenLeft;
        window.Top = OffScreenTop;
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
    }

    /// <summary>Shows <paramref name="window"/> off-screen (see <see cref="PlaceOffScreen"/>) and checks that it really is.</summary>
    public static void ShowOffScreen(Window window)
    {
        PlaceOffScreen(window);
        window.Show();
        AssertOffScreen(window);
    }

    /// <summary>
    /// A real-desktop test window: top-left corner of the work area of the primary monitor, the same place every time, not activated, no taskbar button.
    /// </summary>
    public static void PlaceOnScreen(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        var work = SystemParameters.WorkArea;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = work.Left + CornerMargin;
        window.Top = work.Top + CornerMargin;
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
    }

    /// <summary>
    /// The left edge (in points) for a real-desktop test window of this size: the left corner of the work area (<see cref="CornerMargin"/> or 40 from the edge, like every
    /// other real-desktop window), or the right corner when an always-on-top window of another program (the installed app the person has open) covers more of the left one.
    /// A window of another program over the place where the real cursor clicks takes the mouse from the test.
    /// </summary>
    public static double PickCornerLeft(double width, double height)
    {
        var work = SystemParameters.WorkArea;
        var left = work.Left + 40;
        var right = Math.Max(work.Left, work.Right - width - 40);
        var top = work.Top + 20;
        var scale = GetDpiForSystem() / 96.0;
        var overLeft = OverlapWithOtherPrograms(left, top, width, height, scale);
        var overRight = OverlapWithOtherPrograms(right, top, width, height, scale);
        return overLeft <= overRight ? left : right;
    }

    private static double OverlapWithOtherPrograms(double left, double top, double width, double height, double scale)
    {
        var mine = (uint)Environment.ProcessId;
        double total = 0;
        EnumWindows(
            (window, _) =>
            {
                GetWindowThreadProcessId(window, out var pid);
                // Only windows that stay over a test window count: always-on-top ones, and the windows of the installed app itself (a test window is shown without taking the focus,
                // so the window the person is using stays in front of it).
                if (pid == mine || !IsWindowVisible(window) || IsIconic(window) || !GetWindowRect(window, out var r))
                {
                    return true;
                }

                if ((GetWindowLong(window, -20) & 0x8) == 0 && !IsInstalledApp(pid))
                {
                    return true;
                }

                // Only windows people work in: the desktop, the taskbar and the shell's own surfaces cover everything and do not count.
                var name = new StringBuilder(64);
                GetClassName(window, name, name.Capacity);
                if (name.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "Windows.UI.Core.CoreWindow" or "XamlExplorerHostIslandWindow")
                {
                    return true;
                }

                var w = Math.Min(r.Right, (left + width) * scale) - Math.Max(r.Left, left * scale);
                var h = Math.Min(r.Bottom, (top + height) * scale) - Math.Max(r.Top, top * scale);
                if (w > 0 && h > 0)
                {
                    total += w * h;
                }

                return true;
            },
            IntPtr.Zero);
        return total;
    }

    private static bool IsInstalledApp(uint pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById((int)pid);
            return string.Equals(process.ProcessName, "AlertaIBT", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private delegate bool WindowEnumProc(IntPtr window, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(WindowEnumProc callback, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr window);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder text, int count);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr window, int index);

    /// <summary>Where a real-desktop test puts a screen rectangle (a stand-in LED, a camera spot): the top-left corner of the work area, in pixels.</summary>
    public static (int Left, int Top) CornerPixels()
    {
        var work = SystemParameters.WorkArea;
        return ((int)(work.Left + CornerMargin), (int)(work.Top + CornerMargin));
    }

    /// <summary>Fails when the window is shown and any part of it is on a monitor.</summary>
    public static void AssertOffScreen(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle != IntPtr.Zero && IsOnAnyMonitor(handle, out var rect))
        {
            throw new InvalidOperationException($"A test window ('{window.Title}', {window.GetType().Name}) is on a monitor at {rect.Describe()}: layout tests must show windows with TestWindows.ShowOffScreen.");
        }
    }

    /// <summary>Moves an existing window (a popup, a helper) off every monitor without resizing, activating or changing its stacking.</summary>
    public static void MoveOffScreen(IntPtr handle) =>
        SetWindowPos(handle, IntPtr.Zero, (int)OffScreenLeft, (int)OffScreenTop, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);

    /// <summary>The rectangles of every monitor (virtual ones too), in pixels.</summary>
    public static IReadOnlyList<Rect32> Monitors()
    {
        var list = new List<Rect32>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr monitor, IntPtr dc, ref Rect32 rect, IntPtr data) =>
        {
            list.Add(rect);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// <summary>Whether <paramref name="rect"/> overlaps any monitor, even by one pixel.</summary>
    public static bool IntersectsAnyMonitor(Rect32 rect, IReadOnlyList<Rect32>? monitors = null) =>
        (monitors ?? Monitors()).Any(m => rect.Left < m.Right && rect.Right > m.Left && rect.Top < m.Bottom && rect.Bottom > m.Top);

    /// <summary>Whether the window of <paramref name="handle"/> overlaps any monitor.</summary>
    public static bool IsOnAnyMonitor(IntPtr handle, out Rect32 rect) =>
        GetWindowRect(handle, out rect) && IntersectsAnyMonitor(rect);

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect32
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public readonly string Describe() => $"{Left},{Top},{Right},{Bottom}";
    }

    internal delegate bool MonitorEnumProc(IntPtr monitor, IntPtr dc, ref Rect32 rect, IntPtr data);

    [DllImport("user32.dll")]
    internal static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    internal static extern bool GetWindowRect(IntPtr window, out Rect32 rect);
}

/// <summary>
/// The self-check of the test infrastructure: while a test body runs on the WPF host, every visible top-level window of this process that overlaps a monitor is
/// reported, with the name of the test that was running. A test that is not in the <see cref="RealDesktopCollection"/> must not put a window on a monitor: the
/// body then fails with the list (set <c>ALERTAIBT_WINDOW_GUARD=report</c> to only write <c>alertaibt-window-guard.txt</c> in the temp folder instead).
/// </summary>
internal static class WindowGuard
{
    private const int WsExToolWindow = 0x80;
    private const int GwlExStyle = -20;

    // A window must be seen on a monitor in two samples to count: a popup that WPF opens for a moment on a monitor and the test moves away at once is not a violation.
    private const int HitsToCount = 2;

    private static readonly object Gate = new();
    private static readonly Dictionary<long, int> Hits = [];
    private static readonly List<string> Reported = [];
    private static Thread? _thread;
    private static volatile string? _test;
    private static volatile bool _exempt;
    private static List<string>? _violations;

    private delegate bool EnumProc(IntPtr window, IntPtr data);

    /// <summary>Every violation of the whole run, for the report file.</summary>
    public static IReadOnlyList<string> All
    {
        get
        {
            lock (Gate)
            {
                return Reported.ToList();
            }
        }
    }

    /// <summary>Whether the guard only writes a report (the environment variable above).</summary>
    public static bool Reporting => string.Equals(Environment.GetEnvironmentVariable("ALERTAIBT_WINDOW_GUARD"), "report", StringComparison.OrdinalIgnoreCase);

    /// <summary>Starts the sampler thread (once).</summary>
    public static void Start()
    {
        if (string.Equals(Environment.GetEnvironmentVariable("ALERTAIBT_WINDOW_GUARD"), "off", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        lock (Gate)
        {
            if (_thread is not null)
            {
                return;
            }

            _thread = new Thread(() =>
            {
                while (true)
                {
                    Thread.Sleep(20);
                    try
                    {
                        Sample();
                    }
                    catch (Exception)
                    {
                        // A diagnostic thread never ends the test run.
                    }
                }
            })
            { IsBackground = true, Name = "window guard", Priority = ThreadPriority.AboveNormal };
            _thread.Start();
        }
    }

    /// <summary>A test body begins on the host: from now on windows on a monitor are attributed to this test.</summary>
    public static void Begin()
    {
        var context = TestContext.Current;
        Begin(context?.Test?.TestDisplayName ?? "(outside a test)", IsRealDesktop(context));
    }

    /// <summary>Like <see cref="Begin()"/> with the test name and the real-desktop permission given (the guard's own tests).</summary>
    public static void Begin(string test, bool exempt)
    {
        _exempt = exempt;
        _test = test;
        lock (Gate)
        {
            _violations = [];
        }
    }

    /// <summary>The body threw: nothing more to check (the exception is the failure).</summary>
    public static void Abort()
    {
        _test = null;
        lock (Gate)
        {
            _violations = null;
        }
    }

    /// <summary>The body ended: one last look (twice, when a window is on a monitor right now), then fails the test when a window of a non-real-desktop test was on a monitor.</summary>
    public static void End()
    {
        try
        {
            if (Sample() > 0)
            {
                Thread.Sleep(30);
                Sample();
            }
        }
        catch (Exception)
        {
            // See Start.
        }

        List<string>? found;
        lock (Gate)
        {
            found = _violations;
            _violations = null;
        }

        var exempt = _exempt;
        _test = null;
        if (found is { Count: > 0 } && !exempt && !Reporting)
        {
            throw new InvalidOperationException("Test windows were on a monitor (layout tests must use TestWindows.ShowOffScreen, real-desktop tests the [Collection(RealDesktopCollection.Name)]):\n  " + string.Join("\n  ", found));
        }
    }

    // One look at every visible top-level window of the process that touches a monitor; returns how many there are now.
    private static int Sample()
    {
        var test = _test;
        if (test is null)
        {
            return 0;
        }

        var monitors = TestWindows.Monitors();
        var pid = (uint)Environment.ProcessId;
        var onMonitor = 0;
        EnumWindows(
            (window, _) =>
            {
                GetWindowThreadProcessId(window, out var owner);
                if (owner != pid || !IsWindowVisible(window) || !TestWindows.GetWindowRect(window, out var rect) || !TestWindows.IntersectsAnyMonitor(rect, monitors))
                {
                    return true;
                }

                onMonitor++;
                lock (Gate)
                {
                    var key = window.ToInt64();
                    var hits = Hits.GetValueOrDefault(key) + 1;
                    Hits[key] = hits;
                    if (hits != HitsToCount)
                    {
                        return true;
                    }

                    var title = new StringBuilder(128);
                    GetWindowText(window, title, title.Capacity);
                    var kind = HwndSource.FromHwnd(window)?.RootVisual?.GetType().Name ?? "(no visual)";
                    var tool = (GetWindowLong(window, GwlExStyle) & WsExToolWindow) != 0;
                    var line = $"{test} -> '{title}' [{kind}{(tool ? ", tool" : string.Empty)}] at {rect.Describe()}{(_exempt ? " (real desktop: allowed)" : string.Empty)}";
                    Reported.Add(line);
                    if (Reporting)
                    {
                        File.AppendAllText(Path.Combine(Path.GetTempPath(), "alertaibt-window-guard.txt"), line + Environment.NewLine);
                    }

                    _violations?.Add(line);
                }

                return true;
            },
            IntPtr.Zero);
        return onMonitor;
    }

    private static bool IsRealDesktop(ITestContext? context)
    {
        var name = context?.TestClass?.TestClassName;
        var type = name is null ? null : typeof(WindowGuard).Assembly.GetType(name);
        return type?.GetCustomAttributes<CollectionAttribute>(inherit: true).Any(a => a.Name == RealDesktopCollection.Name) == true;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumProc callback, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, StringBuilder text, int count);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr window, int index);
}
