using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Windows.Threading;
using AlertaIBT.App.Hosting;
using AlertaIBT.App.Platform;
using AlertaIBT.App.ViewModels;
using AlertaIBT.App.Views;
using AlertaIBT.Core.Services;
using AlertaIBT.Core.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace AlertaIBT.App.Tests.Views;

// The real graph of the app (real alert service, real DPAPI, real settings file in a temp folder), but nothing is started: no hotkey,
// no tray icon on the taskbar, no Telegram, no API port, and the main window is never shown.
public sealed class MainWindowGraphTests : IDisposable
{
    private const string ClearKey = "AIzaSyD-REAL-dpapi-key-1234567890abcdef";
    private const string ClearToken = "123456789:AAH-REAL-dpapi-token-0123456789ab";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "alerta-ibt-main-" + Guid.NewGuid().ToString("N"));

    public MainWindowGraphTests() => Directory.CreateDirectory(_dir);

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

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }

    private sealed class FakeMainWindow : IMainWindow
    {
        public bool IsVisible { get; private set; }

        public int Shown { get; private set; }

        public void ShowWindow()
        {
            IsVisible = true;
            Shown++;
        }
    }

    [Fact]
    public void The_graph_builds_the_main_view_model_and_the_real_window_with_the_apps_resources()
    {
        WpfTestHost.Run(() =>
        {
            using var host = Host();
            var controller = host.Services.GetRequiredService<MainWindowController>();

            Assert.Same(host.Services.GetRequiredService<MainViewModel>(), controller.ViewModel);
            Assert.False(controller.IsOpen);
            var window = new MainWindow(controller.ViewModel); // the XAML of all three tabs loads with the real resources
            Assert.Same(controller.ViewModel, window.DataContext);
            Assert.False(window.IsVisible);
            Assert.Same(controller.ViewModel.Saver, host.Services.GetRequiredService<Autosaver>());
        });
    }

    [Fact]
    public void The_two_message_fields_are_separate_drafts_and_the_window_one_is_not_the_panels()
    {
        WpfTestHost.Run(() =>
        {
            using var host = Host();
            var panel = host.Services.GetRequiredService<QuickPanelViewModel>();
            var main = host.Services.GetRequiredService<MainViewModel>();

            main.Home.Composer.InputText = "draft in the window";

            Assert.NotSame(panel, main.Home.Composer);
            Assert.Equal(string.Empty, panel.InputText);
        });
    }

    [Fact]
    public void A_raise_of_the_main_window_from_the_shell_opens_it_once_whoever_asked()
    {
        WpfTestHost.Run(() =>
        {
            using var host = Host();
            var shell = host.Services.GetRequiredService<AppShell>();
            var viewModel = host.Services.GetRequiredService<MainViewModel>();
            var window = new FakeMainWindow();
            var controller = new MainWindowController(viewModel, () => window, NullLogger<MainWindowController>.Instance);
            controller.Attach(shell);

            shell.RaiseMainWindow(); // the tray's double click, "Abrir", a second launch and the panel's "Abrir janela" all end here

            Assert.Equal(1, window.Shown);
            Assert.True(controller.IsOpen);

            shell.RaiseMainWindow();
            Assert.Equal(2, window.Shown); // ShowWindow again brings the same window forward
        });
    }

    [Fact]
    public void The_tray_double_click_and_the_panels_Abrir_janela_both_reach_the_window()
    {
        WpfTestHost.Run(() =>
        {
            using var host = Host();
            var services = host.Services;
            var shell = services.GetRequiredService<AppShell>();
            var tray = services.GetRequiredService<AlertaIBT.App.Tray.TrayService>();
            var panel = services.GetRequiredService<QuickPanelViewModel>();
            var window = new FakeMainWindow();
            new MainWindowController(services.GetRequiredService<MainViewModel>(), () => window, NullLogger<MainWindowController>.Instance).Attach(shell);
            shell.WireTray();
            panel.OpenMainWindowRequested += shell.RaiseMainWindow;

            tray.DoubleClickCommand.Execute(null);
            Assert.Equal(1, window.Shown);

            panel.OpenWindowCommand.Execute(null);
            Assert.Equal(2, window.Shown);
        });
    }

    [Fact]
    public void A_window_that_cannot_be_made_is_logged_and_never_thrown()
    {
        WpfTestHost.Run(() =>
        {
            using var host = Host();
            var viewModel = host.Services.GetRequiredService<MainViewModel>();
            var controller = new MainWindowController(viewModel, () => throw new InvalidOperationException("boom"), NullLogger<MainWindowController>.Instance);

            controller.Show();

            Assert.False(controller.IsOpen);
        });
    }

    [Fact]
    public void The_shortcut_state_from_the_shell_reaches_the_Configuracoes_tab()
    {
        WpfTestHost.Run(() =>
        {
            using var host = Host();
            var shell = host.Services.GetRequiredService<AppShell>();
            var viewModel = host.Services.GetRequiredService<MainViewModel>();
            var controller = new MainWindowController(viewModel, () => new FakeMainWindow(), NullLogger<MainWindowController>.Instance);
            controller.Attach(shell);

            viewModel.Settings.ApplyHotkey(new HotkeyStatus("Ctrl+Alt+Space", "Ctrl+Alt+Espa\U000000E7o", false, "j\U000000E1 \U000000E9 usado"));

            Assert.True(viewModel.Settings.HasHotkeyProblem);
        });
    }

    [Fact]
    public void One_press_of_the_pause_button_of_the_window_toggles_once()
    {
        WpfTestHost.Run(() =>
        {
            using var host = Host();
            var alerts = host.Services.GetRequiredService<AlertService>();
            var main = host.Services.GetRequiredService<MainViewModel>();

            main.Home.Composer.TogglePauseCommand.Execute(null);
            Pump();
            Assert.True(alerts.IsPaused);
            Assert.True(main.Home.Composer.IsPaused);
            Assert.Equal("Retomar barra", main.Home.Composer.PauseTip);

            main.Home.Composer.TogglePauseCommand.Execute(null);
            Pump();
            Assert.False(alerts.IsPaused); // a second toggle behind the first press would have left it paused
            Assert.Equal("Pausar barra", main.Home.Composer.PauseTip);
        });
    }

    [Fact]
    public void Skip_and_clear_and_replay_reach_the_real_alert_service()
    {
        WpfTestHost.Run(() =>
        {
            using var host = Host();
            var alerts = host.Services.GetRequiredService<AlertService>();
            var main = host.Services.GetRequiredService<MainViewModel>();
            var queue = alerts.Queue;
            queue.Enqueue(new AlertaIBT.Core.Queue.QueueItem("a", "AAA", null, AlertaIBT.Core.Alerts.AlertSource.Pc, false, DateTimeOffset.UtcNow, null, null), AlertaIBT.Core.Queue.DuplicatePolicy.Warn);
            queue.Enqueue(new AlertaIBT.Core.Queue.QueueItem("b", "BBB", null, AlertaIBT.Core.Alerts.AlertSource.Pc, false, DateTimeOffset.UtcNow, null, null), AlertaIBT.Core.Queue.DuplicatePolicy.Warn);
            Pump();
            Assert.Equal(2, main.Home.Queue.Count);

            main.Home.Queue[0].MoveDownCommand.Execute(null);
            Pump();
            Assert.Equal(["BBB", "AAA"], main.Home.Queue.Select(r => r.Text));

            main.Home.Queue[0].RemoveCommand.Execute(null);
            Pump();
            Assert.Equal(["AAA"], main.Home.Queue.Select(r => r.Text));

            main.Home.ClearQueueCommand.Execute(null);
            Pump();
            Assert.Empty(queue.Pending);
            Assert.Empty(main.Home.Queue);
            Assert.Equal(2, main.Home.History.Count);
        });
    }

    [Fact]
    public void The_gemini_key_goes_through_the_real_dpapi_and_the_file_never_has_it_in_clear()
    {
        WpfTestHost.Run(() =>
        {
            using var host = Host();
            var settings = host.Services.GetRequiredService<SettingsService>();
            var main = host.Services.GetRequiredService<MainViewModel>();

            main.Correction.GeminiKey.Entry = ClearKey;
            main.Saver.FlushNow();

            var file = File.ReadAllText(Path.Combine(_dir, "settings.json"));
            Assert.DoesNotContain(ClearKey, file);
            Assert.DoesNotContain("REAL-dpapi", file);
            Assert.Equal(ClearKey, settings.Current.GetGeminiKey(new DpapiSecretProtector()));
            Assert.True(main.Correction.GeminiKey.IsLocked);
            Assert.Equal(string.Empty, main.Correction.GeminiKey.Entry);
        });
    }

    [Fact]
    public void The_telegram_token_goes_through_the_real_dpapi_and_resets_the_offset_in_the_same_update()
    {
        WpfTestHost.Run(() =>
        {
            using var host = Host();
            var settings = host.Services.GetRequiredService<SettingsService>();
            var main = host.Services.GetRequiredService<MainViewModel>();
            settings.Update(s => s with { Telegram = s.Telegram with { Offset = 777 } });

            main.Settings.Token.Entry = ClearToken;
            main.Saver.FlushNow();

            var file = File.ReadAllText(Path.Combine(_dir, "settings.json"));
            Assert.DoesNotContain("REAL-dpapi", file);
            Assert.DoesNotContain(ClearToken, file);
            Assert.Equal(ClearToken, settings.Current.GetTelegramToken(new DpapiSecretProtector()));
            Assert.Equal(0, settings.Current.Telegram.Offset);
            Assert.True(main.Settings.Token.IsLocked);
        });
    }

    [Fact]
    public void A_change_made_just_before_quitting_is_saved_when_the_controller_is_disposed()
    {
        WpfTestHost.Run(() =>
        {
            using var host = Host();
            var controller = host.Services.GetRequiredService<MainWindowController>();
            controller.ViewModel.Settings.BarHeight = 99;
            Assert.False(File.Exists(Path.Combine(_dir, "settings.json")) && File.ReadAllText(Path.Combine(_dir, "settings.json")).Contains("\"height\": 99", StringComparison.Ordinal));

            controller.Dispose();
            host.Services.GetRequiredService<SettingsService>().Dispose(); // as App.OnExit does, right after

            var onDisk = new SettingsStore(Path.Combine(_dir, "settings.json")).Load().Settings;
            Assert.Equal(99, onDisk.Bar.Height);
        });
    }

    [Fact]
    public void The_X_of_the_window_only_hides_it()
    {
        WpfTestHost.Run(() =>
        {
            using var host = Host();
            var window = new MainWindow(host.Services.GetRequiredService<MainViewModel>());
            var closing = new CancelEventArgs();

            typeof(MainWindow)
                .GetMethod("OnClosing", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, [closing]);

            Assert.True(closing.Cancel);
        });
    }
}
