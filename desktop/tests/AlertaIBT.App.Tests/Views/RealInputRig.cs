using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using AlertaIBT.App.Services;
using AlertaIBT.App.Tests.ViewModels;
using AlertaIBT.App.ViewModels;
using AlertaIBT.App.Views;
using AlertaIBT.Core.Audio;
using AlertaIBT.Core.Diagnostics;
using AlertaIBT.Core.Player;
using AlertaIBT.Core.Settings;

namespace AlertaIBT.App.Tests.Views;

/// <summary>
/// The real main window on the Som tab, really shown (far off screen and never activated, so it cannot take the keyboard or flash on the monitor)
/// and driven by the real input system: the mouse is sent to the window as WM_MOUSEMOVE / WM_LBUTTONDOWN / WM_LBUTTONUP, so the events reach
/// the controls through hit testing, capture and routing as a real click does. No device, no file, no sound: the engines are the real ones with
/// the fakes of <see cref="SomRig"/> at the edges.
/// </summary>
internal sealed class RealInputRig : IDisposable
{
    private const uint WmMouseMove = 0x0200;
    private const uint WmLButtonDown = 0x0201;
    private const uint WmLButtonUp = 0x0202;
    private const uint WmKeyDown = 0x0100;
    private const uint WmKeyUp = 0x0101;

    private readonly SomRig _som;
    private Native.Point _cursor;
    private Native.Point? _placed; // where this rig last put the real cursor

    private static int _interference;

    /// <summary>The left edge of a real-desktop test window: the left corner of the work area, or the right one when a window of another program (the installed app) sits over the left one.</summary>
    public static double CornerLeft(double width, double height = 1000) => TestWindows.PickCornerLeft(width, height);

    /// <summary>How many times, in this process, the real cursor was found away from where a rig had put it (a person moved the mouse during a test).</summary>
    public static int Interference => Volatile.Read(ref _interference);

    /// <summary>
    /// Runs a test body that drives the real cursor on the WPF thread. The real cursor is shared with whoever sits at the PC: when they move
    /// the mouse in the middle of a body, WPF re-reads that position and the interaction goes wrong. A failed attempt during which the
    /// cursor was found moved by someone else is tried again (a few times, with a fresh rig); a failure without any interference, or the
    /// last one, is thrown as it is, so nothing passes silently.
    /// </summary>
    public static void Run(Action body, int attempts = 5)
    {
        for (var attempt = 1; ; attempt++)
        {
            var before = Interference;
            Volatile.Write(ref _cover, null); // the reason named on a failure is the one of THIS attempt
            try
            {
                WpfTestHost.Run(body);
                return;
            }
            catch (Exception) when (attempt < attempts && Interference != before)
            {
                Thread.Sleep(300); // let the person finish moving, the display wake
            }
            catch (Exception ex) when (Interference != before)
            {
                throw new InvalidOperationException($"Failed {attempts} times with the real desktop in the way ({LastCover ?? "the cursor was moved by someone else"}): {ex.Message}", ex);
            }
        }
    }

    /// <param name="topmost">
    /// The window stays above every other program's (the installed app, a chat), which would otherwise sit over the place the real cursor clicks. Only for tests
    /// that do not open a popup: the popup of a list box is a window of its own and goes behind a topmost owner.
    /// </param>
    public RealInputRig(bool playing = true, bool paused = false, double width = 1100, double height = 1400, bool topmost = false)
    {
        var initial = new AppSettings
        {
            Audio = new AudioSettings { OutputMatch = AudioSettings.DefaultOutputWords },
            Player = new PlayerSettings
            {
                Playlists = [new PlaylistEntry("Igreja", @"C:\Music\Igreja"), new PlaylistEntry("Louvor", @"D:\Louvor")],
                Alarms =
                [
                    AlarmEntry.CreateDefault("domingo", "Igreja"),
                    AlarmEntry.CreateDefault("quarta", "Louvor") with { Start = "19:30", Latest = "19:55", Days = 0b0001000 },
                ],
                Enabled = true,
                FadeOutSeconds = paused ? 0 : 5,
            },
        };
        _som = new SomRig(initial, withLoudness: true);
        _som.Audio.Devices.Add(new AudioDevice("id-2", "Alto-falantes (WING-USB Out 1-2)"));
        _som.Audio.Devices.Add(new AudioDevice("id-3", "Alto-falantes (WING-USB Out 3-4)"));
        _som.PlayerView.ReloadOutputDevices();
        _som.BootAudio();
        if (playing || paused)
        {
            _som.Player.Play();
            Listen(75);
            if (paused)
            {
                _som.Player.Pause();
                Listen(0.2);
            }

            _som.Settings.Clock.Advance(TimeSpan.FromMilliseconds(600));
        }

        var settings = _som.Settings;
        var gateway = new FakeGateway(settings.Clock);
        var composer = new QuickPanelViewModel(gateway, settings.Hub, clock: settings.Clock);
        var home = new HomeViewModel(gateway, composer, clock: settings.Clock);
        var correction = new CorrectionViewModel(settings.Settings, settings.Protector, settings.Hub, settings.Saver);
        var diagnostics = new DiagnosticsViewModel(new LogFiles(System.IO.Path.Combine(settings.Folder, "logs")), _ => { }, () => null);
        var updates = new FakeUpdateStatus();
        var page = new SettingsViewModel(settings.Settings, settings.Protector, settings.Hub, settings.Saver, diagnostics, () => Task.CompletedTask, updates: updates);
        ViewModel = new MainViewModel(settings.Hub, home, correction, page, settings.Saver, updates: updates, som: _som.Som);
        Window = new MainWindow(ViewModel, null)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = CornerLeft(Math.Min(width, SystemParameters.WorkArea.Width - 80), Math.Min(height, SystemParameters.WorkArea.Height - 60)), // fully inside the work area of the primary monitor, whatever its size
            Top = SystemParameters.WorkArea.Top + 20,
            Width = Math.Min(width, SystemParameters.WorkArea.Width - 80),
            Height = Math.Min(height, SystemParameters.WorkArea.Height - 60),
            ShowActivated = false,
            ShowInTaskbar = false,
            Topmost = topmost,
        };
        Target = Window;
        GetCursorPos(out _cursor);
        ViewModel.SelectTabCommand.Execute(MainTab.Som);
        Window.Show();
        Pump();
        RaiseAboveOtherPrograms(); // shown without activation, the window sits BEHIND the program the person works in
    }

    // The window above every other program's normal windows (see TestWindows.RaiseAboveOtherPrograms); done again when something is found over it.
    private void RaiseAboveOtherPrograms() => TestWindows.RaiseAboveOtherPrograms(new WindowInteropHelper(Window).Handle);

    public MainWindow Window { get; }

    public MainViewModel ViewModel { get; }

    public SomRig Som => _som;

    public SomPage Page => Window.Som;

    public PlayerViewModel Player => _som.PlayerView;

    /// <summary>The sound card pulls <paramref name="seconds"/> of sound (the fake output).</summary>
    public void Listen(double seconds)
    {
        var block = new float[9600]; // 100 ms of 48 kHz stereo
        for (var i = 0; i < (int)(seconds * 10); i++)
        {
            _som.Backend.Outputs[0].Source.Read(block, 0, block.Length);
        }
    }

    /// <summary>Lets the dispatcher run what is waiting (layout, bindings, input) without waiting for time.</summary>
    public static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    /// <summary>The place at (<paramref name="fx"/>, <paramref name="fy"/>) of the size of <paramref name="element"/>, in window coordinates.</summary>
    public Point At(FrameworkElement element, double fx, double fy) =>
        Window.PointFromScreen(element.PointToScreen(new Point(element.ActualWidth * fx, element.ActualHeight * fy))); // also right for a popup, a window of its own

    public Point CenterOf(FrameworkElement element) => At(element, 0.5, 0.5);

    /// <summary>A click on a place of the screen (in device pixels), for the windows of the test other than the main one.</summary>
    public void ClickAtScreen(Point screen)
    {
        MouseMoveAtScreen(screen);
        MouseDown(Target.PointFromScreen(screen));
        MouseUp(Target.PointFromScreen(screen));
    }

    public void MouseMoveAtScreen(Point screen) => MouseMove(Target.PointFromScreen(screen));

    /// <summary>The window that gets the mouse (the main window unless a test works in a window of its own).</summary>
    public Window Target { get; set; } = null!;

    public void MouseMove(Point windowPoint, bool leftDown = false) => Send(WmMouseMove, leftDown ? 1 : 0, windowPoint);

    public void MouseDown(Point windowPoint) => Send(WmLButtonDown, 1, windowPoint);

    public void MouseUp(Point windowPoint) => Send(WmLButtonUp, 0, windowPoint);

    /// <summary>A whole click: move there, press, let go.</summary>
    public void Click(Point windowPoint)
    {
        MouseMove(windowPoint);
        MouseDown(windowPoint);
        MouseUp(windowPoint);
    }

    public void Key(int virtualKey)
    {
        var hwnd = new WindowInteropHelper(Window).Handle;
        SendMessage(hwnd, WmKeyDown, virtualKey, 0);
        SendMessage(hwnd, WmKeyUp, virtualKey, unchecked((nint)0xC0000001));
    }

    // WPF ignores mouse window messages that do not come from the system queue, so the report that the Win32 provider would make is made here and
    // handed to the same input site (hit testing, over, capture and routing are the real ones from there on).
    private void Send(uint message, nint wParam, Point windowPoint)
    {
        var hwnd = new WindowInteropHelper(Target).Handle;
        var source = HwndSource.FromHwnd(hwnd);
        var device = source.CompositionTarget.TransformToDevice.Transform(windowPoint);
        var x = (int)Math.Round(device.X);
        var y = (int)Math.Round(device.Y);
        var screen = Target.PointToScreen(windowPoint);
        CheckCursor();
        CheckNotCovered(screen);
        var wanted = new Native.Point { X = (int)Math.Round(screen.X), Y = (int)Math.Round(screen.Y) };
        SetCursorPos(wanted.X, wanted.Y); // WPF re-reads the real cursor when the capture changes
        _placed = wanted;
        var bf = BindingFlags.NonPublic | BindingFlags.Instance;
        var provider = typeof(HwndSource).GetField("_mouse", bf)!.GetValue(source)!;
        var site = provider.GetType().GetField("_site", bf)!.GetValue(provider)!;
        // Always announce the window as the active mouse target first (it costs nothing when it already is): the real cursor really moves to
        // the places clicked (SetCursorPos), and when it goes over a window of another kind (a list box's popup is a window of its own) Windows
        // sends the main window a real WM_MOUSELEAVE; WPF then treats the mouse as gone and ignores every later move until the next Activate.
        // That is why a click on a list box worked on a tall window and failed from the next list box on whenever the popup went past the window.
        Report(site, source, "Activate", x, y);

        var action = message switch
        {
            WmLButtonDown => "Button1Press",
            WmLButtonUp => "Button1Release",
            _ => "AbsoluteMove",
        };
        Report(site, source, action, x, y);
    }

    private static void Report(object site, HwndSource source, string action, int x, int y)
    {
        var assembly = typeof(InputManager).Assembly;
        var type = assembly.GetType("System.Windows.Input.RawMouseInputReport")!;
        var actions = Enum.Parse(assembly.GetType("System.Windows.Input.RawMouseActions")!, action);
        var ctor = type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).Single();
        var report = ctor.Invoke([InputMode.Foreground, Environment.TickCount, source, actions, x, y, 0, IntPtr.Zero]);
        site.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).Single(m => m.Name == "ReportInput" && m.GetParameters().Length == 1).Invoke(site, [report]);
    }

    // A window of some other process on top of the test window (the Windows dim overlay of an idle PC, somebody's always-on-top window) takes the
    // mouse from WPF. Counted like a moved cursor, so the attempt is repeated (after a nudge that wakes the display), and named when it keeps failing.
    private void CheckNotCovered(Point screen)
    {
        var point = new Native.Point { X = (int)Math.Round(screen.X), Y = (int)Math.Round(screen.Y) };
        var hit = WindowFromPoint(point);
        if (hit == 0)
        {
            return;
        }

        var root = GetAncestor(hit, 2); // GA_ROOT
        GetWindowThreadProcessId(root, out var pid);
        if (pid == (uint)Environment.ProcessId)
        {
            return;
        }

        // Somebody's window got in front (the person clicked it, an editor was raised): put the test window back over it and look again before calling it interference.
        RaiseAboveOtherPrograms();
        hit = WindowFromPoint(point);
        if (hit == 0 || GetWindowThreadProcessId(GetAncestor(hit, 2), out var againPid) != 0 && againPid == (uint)Environment.ProcessId)
        {
            return;
        }

        var name = new System.Text.StringBuilder(128);
        GetClassName(root, name, name.Capacity);
        Interlocked.Increment(ref _interference);
        Volatile.Write(ref _cover, $"a window of another program is over the test window: {name} (process {pid}) at {screen}");
        DesktopAwake.Poke();
    }

    private static string? _cover;

    /// <summary>What covered the test window when the last attempt was repeated, or null.</summary>
    public static string? LastCover => Volatile.Read(ref _cover);

    /// <summary>Notes (for <see cref="Run"/>) when the real cursor is not where this rig left it.</summary>
    public void CheckCursor()
    {
        if (_placed is { } placed && GetCursorPos(out var now) && (Math.Abs(now.X - placed.X) > 1 || Math.Abs(now.Y - placed.Y) > 1))
        {
            Interlocked.Increment(ref _interference);
            _placed = null;
        }
    }

    public void Dispose()
    {
        Window.Hide();
        CheckCursor();
        if (_placed is not null)
        {
            SetCursorPos(_cursor.X, _cursor.Y); // only when nobody moved it meanwhile: never yank the mouse from a person's hand
        }

        ViewModel.Dispose();
        _som.Dispose();
    }

    private static class Native
    {
        public struct Point
        {
            public int X;
            public int Y;
        }
    }

    [DllImport("user32.dll")]
    private static extern nint WindowFromPoint(Native.Point point);

    [DllImport("user32.dll")]
    private static extern nint GetAncestor(nint hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint hwnd, System.Text.StringBuilder text, int max);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Native.Point point);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern nint SendMessage(nint hwnd, uint message, nint wParam, nint lParam);
}
