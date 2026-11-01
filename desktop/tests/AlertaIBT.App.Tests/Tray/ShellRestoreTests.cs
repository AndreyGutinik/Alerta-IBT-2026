using System.Net;
using System.Net.Sockets;
using AlertaIBT.App.Hosting;
using AlertaIBT.App.Platform;
using AlertaIBT.App.Tests.Platform;
using AlertaIBT.App.Tests.ViewModels;
using AlertaIBT.App.Tray;
using AlertaIBT.Core.Input;
using AlertaIBT.Core.Ndi;
using AlertaIBT.Core.Settings;
using Microsoft.Extensions.DependencyInjection;

namespace AlertaIBT.App.Tests.Tray;

// Batch 39, startup order: the real graph of the app with the keyboard (both hooks) and the cursor faked. The switches the shell puts back (presentation
// mode, NDI layer and color bars) are applied only AFTER the services exist, through the real code paths, and the tray follows them.
public sealed class ShellRestoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "alerta-ibt-shellrestore-" + Guid.NewGuid().ToString("N"));
    private readonly FakeKeyboardGate _gate = new();

    public ShellRestoreTests() => Directory.CreateDirectory(_dir);

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

    private ServiceProvider Provider(AppSettings saved)
    {
        var paths = new AppPaths(_dir);
        new SettingsStore(paths.SettingsFile).Save(saved);
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        var services = new ServiceCollection();
        services.AddLogging();
        AppHost.ConfigureServices(services, paths, new StartupArguments(Tray: true, TestLed: false), port);
        services.AddSingleton<INativeCursor>(new FakeCursor()); // the last registration wins
        services.AddSingleton<IKeyboardHook>(new FakeKeyboardHook());
        services.AddSingleton<IKeyboardGate>(_gate);
        return services.BuildServiceProvider();
    }

    private static AppSettings WithToken(AppSettings settings) => settings.WithHolyricsToken("token-de-teste", new DpapiSecretProtector());

    [Fact]
    public void Nothing_is_applied_when_the_graph_is_built_only_when_the_shell_restores()
    {
        WpfTestHost.Run(() =>
        {
            using var provider = Provider(WithToken(new AppSettings
            {
                Holyrics = new HolyricsSettings { PresentationMode = true },
                Ndi = new NdiSettings { LayerOn = true, TestPattern = true },
            }));
            var presentation = provider.GetRequiredService<PresentationModeController>();
            var ndi = provider.GetRequiredService<NdiService>();

            Assert.False(presentation.IsOn);
            Assert.False(_gate.IsInstalled);
            Assert.False(ndi.LayerOn);
            Assert.False(ndi.Switcher.TestPattern);
        });
    }

    [Fact]
    public void The_shell_puts_the_presentation_mode_and_the_ndi_switches_back_and_the_tray_follows()
    {
        WpfTestHost.Run(() =>
        {
            using var provider = Provider(WithToken(new AppSettings
            {
                Holyrics = new HolyricsSettings { PresentationMode = true },
                Ndi = new NdiSettings { LayerOn = true, TestPattern = true },
            }));
            var shell = provider.GetRequiredService<AppShell>();
            var tray = provider.GetRequiredService<TrayService>();
            var presentation = provider.GetRequiredService<PresentationModeController>();
            var ndi = provider.GetRequiredService<NdiService>();
            shell.WireTray();

            shell.RestoreSavedSwitches();

            Assert.True(presentation.IsOn);
            Assert.True(_gate.IsInstalled); // the keyboard hook really exists
            Assert.True(tray.PresentationItem.IsChecked);
            Assert.True(tray.IsPresentationOn);
            Assert.True(ndi.LayerOn);
            Assert.True(ndi.Switcher.TestPattern);
        });
    }

    [Fact]
    public void A_first_install_restores_nothing_and_the_hook_is_never_installed()
    {
        WpfTestHost.Run(() =>
        {
            using var provider = Provider(new AppSettings());
            var shell = provider.GetRequiredService<AppShell>();
            shell.WireTray();

            shell.RestoreSavedSwitches();

            Assert.False(provider.GetRequiredService<PresentationModeController>().IsOn);
            Assert.False(_gate.IsInstalled);
            Assert.False(provider.GetRequiredService<NdiService>().LayerOn);
        });
    }

    [Fact]
    public void A_saved_presentation_mode_without_a_token_does_not_stop_the_other_switches_and_does_not_crash()
    {
        WpfTestHost.Run(() =>
        {
            using var provider = Provider(new AppSettings
            {
                Holyrics = new HolyricsSettings { PresentationMode = true },
                Ndi = new NdiSettings { LayerOn = true },
            });
            var shell = provider.GetRequiredService<AppShell>();
            var tray = provider.GetRequiredService<TrayService>();
            var presentation = provider.GetRequiredService<PresentationModeController>();
            shell.WireTray();

            shell.RestoreSavedSwitches();

            Assert.False(presentation.IsOn);
            Assert.Equal(PresentationModeController.NotConfiguredText, presentation.Reason);
            Assert.False(tray.PresentationItem.IsChecked);
            Assert.False(_gate.IsInstalled);
            Assert.True(provider.GetRequiredService<NdiService>().LayerOn);
        });
    }
}
