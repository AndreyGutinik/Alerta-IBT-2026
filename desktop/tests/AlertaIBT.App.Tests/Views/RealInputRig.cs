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

    public RealInputRig(bool playing = true, bool paused = false, double width = 1100, double height = 1400)
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
            Left = 40,
            Top = 20,
            Width = Math.Min(width, SystemParameters.WorkArea.Width - 80),
            Height = Math.Min(height, SystemParameters.WorkArea.Height - 60),
            ShowActivated = false,
            ShowInTaskbar = false,
        };
        Target = Window;
        GetCursorPos(out _cursor);
        ViewModel.SelectTabCommand.Execute(MainTab.Som);
        Window.Show();
        Pump();
    }

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
    private readonly HashSet<Window> _activated = [];

    private void Send(uint message, nint wParam, Point windowPoint)
    {
        var hwnd = new WindowInteropHelper(Target).Handle;
        var source = HwndSource.FromHwnd(hwnd);
        var device = source.CompositionTarget.TransformToDevice.Transform(windowPoint);
        var x = (int)Math.Round(device.X);
        var y = (int)Math.Round(device.Y);
        var screen = Target.PointToScreen(windowPoint);
        SetCursorPos((int)Math.Round(screen.X), (int)Math.Round(screen.Y)); // WPF re-reads the real cursor when the capture changes
        var bf = BindingFlags.NonPublic | BindingFlags.Instance;
        var provider = typeof(HwndSource).GetField("_mouse", bf)!.GetValue(source)!;
        var site = provider.GetType().GetField("_site", bf)!.GetValue(provider)!;
        if (_activated.Add(Target))
        {
            Report(site, source, "Activate", x, y);
        }

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

    public void Dispose()
    {
        Window.Hide();
        SetCursorPos(_cursor.X, _cursor.Y);
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
    private static extern bool GetCursorPos(out Native.Point point);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern nint SendMessage(nint hwnd, uint message, nint wParam, nint lParam);
}
