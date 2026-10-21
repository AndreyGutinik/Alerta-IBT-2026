using System.Net;
using System.Net.Sockets;
using System.Windows.Controls;
using System.Windows.Threading;
using AlertaIBT.App.Hosting;
using AlertaIBT.App.Tray;
using AlertaIBT.App.ViewModels;
using AlertaIBT.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace AlertaIBT.App.Tests.Tray;

// The real graph of the app (real alert service, queue, shell, tray, quick-panel view-model), but nothing is started: no hotkey,
// no tray icon on the taskbar, no Telegram, no API port.
public sealed class PauseOnceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "alerta-ibt-pause-" + Guid.NewGuid().ToString("N"));

    public PauseOnceTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        Serilog.Log.CloseAndFlush();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // A log file still held by the logger: the OS temp cleaner takes it.
        }
    }

    private IHost Host()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return AppHost.Build(new AppPaths(_dir), new StartupArguments(Tray: true, TestLed: false), port);
    }

    private static void Click(MenuItem item) => item.RaiseEvent(new System.Windows.RoutedEventArgs(MenuItem.ClickEvent));

    // The panel hears about the hub through the dispatcher: let the queued work run.
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }

    [Fact]
    public void One_click_on_the_tray_item_toggles_the_pause_once_while_the_quick_panel_is_listening()
    {
        WpfTestHost.Run(() =>
        {
            using var host = Host();
            var services = host.Services;
            var shell = services.GetRequiredService<AppShell>();
            var tray = services.GetRequiredService<TrayService>();
            var alerts = services.GetRequiredService<AlertService>();
            var panel = services.GetRequiredService<QuickPanelViewModel>();
            shell.WireTray();
            var notified = 0;
            shell.PauseToggleRequested += () => notified++;
            var pauseItem = tray.Menu.Items.OfType<MenuItem>().Single(i => (string)i.Header is "Pausar" or "Retomar");

            Click(pauseItem);
            Pump();

            Assert.True(alerts.IsPaused);
            Assert.Equal(1, notified);
            Assert.True(panel.IsPaused);
            Assert.Equal("Pausada", panel.PillText);

            Click(pauseItem);
            Pump();

            Assert.False(alerts.IsPaused); // a second toggle behind the first click would have left it paused
            Assert.Equal(2, notified);
            Assert.False(panel.IsPaused);
        });
    }

    [Fact]
    public void One_press_of_the_panel_pause_button_toggles_once_and_the_tray_follows_it()
    {
        WpfTestHost.Run(() =>
        {
            using var host = Host();
            var services = host.Services;
            var alerts = services.GetRequiredService<AlertService>();
            var panel = services.GetRequiredService<QuickPanelViewModel>();

            panel.TogglePauseCommand.Execute(null);
            Pump();
            Assert.True(alerts.IsPaused);
            Assert.True(panel.IsPaused);
            Assert.Equal("LucidePlay", panel.PauseIconKey);

            panel.TogglePauseCommand.Execute(null);
            Pump();
            Assert.False(alerts.IsPaused);
            Assert.Equal("LucidePause", panel.PauseIconKey);
        });
    }

    [Fact]
    public void The_controller_attached_to_the_shell_opens_on_a_tray_click_and_the_window_button_raises_the_main_window()
    {
        WpfTestHost.Run(() =>
        {
            using var host = Host();
            var services = host.Services;
            var shell = services.GetRequiredService<AppShell>();
            var tray = services.GetRequiredService<TrayService>();
            var panel = services.GetRequiredService<QuickPanelViewModel>();
            var window = new FakeWindow();
            var controller = new QuickPanelController(panel, () => window, NullLogger<QuickPanelController>.Instance);
            controller.Attach(shell);
            shell.WireTray();
            var mainWindow = 0;
            shell.MainWindowRequested += () => mainWindow++;

            tray.LeftClickCommand.Execute(null);
            Assert.True(window.IsVisible);

            panel.OpenWindowCommand.Execute(null);

            Assert.Equal(1, mainWindow);
            Assert.False(window.IsVisible);
        });
    }

    [Fact]
    public void The_graph_builds_the_real_window_for_the_controller()
    {
        WpfTestHost.Run(() =>
        {
            using var host = Host();
            var controller = host.Services.GetRequiredService<QuickPanelController>();

            Assert.Same(host.Services.GetRequiredService<QuickPanelViewModel>(), controller.ViewModel);
            Assert.False(controller.IsOpen);
            var window = new QuickPanel(controller.ViewModel); // the XAML loads with the app's real resources
            Assert.Same(controller.ViewModel, window.DataContext);
        });
    }

    private sealed class FakeWindow : IQuickPanelWindow
    {
        public bool IsVisible { get; private set; }

        public event Action? Hidden;

        public void ShowPanel() => IsVisible = true;

        public void HidePanel()
        {
            IsVisible = false;
            Hidden?.Invoke();
        }
    }
}
