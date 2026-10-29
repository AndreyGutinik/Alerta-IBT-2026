using System.Net;
using System.Net.Sockets;
using System.Windows.Controls;
using AlertaIBT.App.Hosting;
using AlertaIBT.App.Platform;
using AlertaIBT.App.Tests.Platform;
using AlertaIBT.App.Tray;
using AlertaIBT.Core.Input;
using AlertaIBT.Core.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AlertaIBT.App.Tests.Tray;

// The real graph of the app with the cursor, the keyboard hook and the shortcut replaced by fakes: nothing here touches the real mouse.
public sealed class MouseLockShellTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "alerta-ibt-mouselock-" + Guid.NewGuid().ToString("N"));
    private readonly FakeCursor _cursor = new();
    private readonly FakeKeyboardHook _hook = new();

    public MouseLockShellTests() => Directory.CreateDirectory(_dir);

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

    private ServiceProvider Provider()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        var services = new ServiceCollection();
        services.AddLogging();
        AppHost.ConfigureServices(services, new AppPaths(_dir), new StartupArguments(Tray: true, TestLed: false), port);
        services.AddSingleton<INativeCursor>(_cursor); // the last registration wins
        services.AddSingleton<IKeyboardHook>(_hook);
        return services.BuildServiceProvider();
    }

    private static void Click(MenuItem item) => item.RaiseEvent(new System.Windows.RoutedEventArgs(MenuItem.ClickEvent));

    [Fact]
    public void The_tray_item_locks_and_unlocks_the_mouse_checks_itself_saves_the_choice_and_updates_the_tooltip()
    {
        WpfTestHost.Run(() =>
        {
            using var provider = Provider();
            var shell = provider.GetRequiredService<AppShell>();
            var tray = provider.GetRequiredService<TrayService>();
            var settings = provider.GetRequiredService<SettingsService>();
            shell.WireTray();
            shell.MouseLock.Start(new FakeHotkeyRegistrar());

            Click(tray.MouseLockItem);

            Assert.True(shell.MouseLock.IsLocked);
            Assert.Equal(new AlertaIBT.Core.Screens.ScreenRect(0, 0, 1920, 1080), _cursor.Clip);
            Assert.True(tray.MouseLockItem.IsChecked);
            Assert.Equal("IBT Controller - mouse travado", tray.ToolTip);
            Assert.True(settings.Current.MouseLock.Enabled);

            Click(tray.MouseLockItem);

            Assert.False(shell.MouseLock.IsLocked);
            Assert.False(tray.MouseLockItem.IsChecked);
            Assert.Equal("IBT Controller", tray.ToolTip);
            Assert.False(settings.Current.MouseLock.Enabled);
        });
    }

    [Fact]
    public void The_lock_hotkey_event_toggles_the_lock_like_the_tray_item()
    {
        WpfTestHost.Run(() =>
        {
            using var provider = Provider();
            var shell = provider.GetRequiredService<AppShell>();
            var tray = provider.GetRequiredService<TrayService>();
            shell.WireTray();
            shell.MouseLock.Start(new FakeHotkeyRegistrar());

            shell.MouseLock.Toggle(); // what HotkeyService.Pressed calls in the app

            Assert.True(shell.MouseLock.IsLocked);
            Assert.True(tray.IsMouseLocked);
        });
    }

    [Fact]
    public void A_shell_that_was_never_started_never_touches_the_cursor_even_when_clicked_or_disposed()
    {
        WpfTestHost.Run(() =>
        {
            var provider = Provider();
            var shell = provider.GetRequiredService<AppShell>();
            var tray = provider.GetRequiredService<TrayService>();
            shell.WireTray();

            Click(tray.MouseLockItem);
            provider.Dispose();

            Assert.Equal(0, _cursor.ClipCalls);
            Assert.Equal(0, _cursor.UnclipCalls);
            Assert.Empty(_cursor.Moves);
            Assert.Equal(0, _hook.Installs);
        });
    }

    [Fact]
    public void Disposing_the_shell_gives_the_cursor_and_the_keyboard_back()
    {
        WpfTestHost.Run(() =>
        {
            var provider = Provider();
            var shell = provider.GetRequiredService<AppShell>();
            shell.MouseLock.Start(new FakeHotkeyRegistrar());
            shell.MouseLock.Toggle();
            Assert.True(_hook.IsInstalled);

            provider.Dispose();

            Assert.Equal(1, _cursor.UnclipCalls);
            Assert.False(_hook.IsInstalled);
        });
    }

    [Fact]
    public void The_Configuracoes_tab_hears_the_lock_shortcut_state()
    {
        WpfTestHost.Run(() =>
        {
            using var provider = Provider();
            var shell = provider.GetRequiredService<AppShell>();
            var main = provider.GetRequiredService<AlertaIBT.App.ViewModels.MainViewModel>();
            var controller = new AlertaIBT.App.Views.MainWindowController(
                main,
                () => throw new InvalidOperationException("no window in this test"),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<AlertaIBT.App.Views.MainWindowController>.Instance);
            controller.Attach(shell);

            shell.MouseLock.Start(new FakeHotkeyRegistrar { Accept = false });

            Assert.True(main.Settings.HasMouseLockHotkeyProblem);
        });
    }
}
