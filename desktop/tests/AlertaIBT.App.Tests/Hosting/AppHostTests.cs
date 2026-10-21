using AlertaIBT.App.Hosting;
using AlertaIBT.App.Overlay;
using AlertaIBT.App.Platform;
using AlertaIBT.Core.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AlertaIBT.App.Tests.Hosting;

public sealed class AppHostTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "alerta-ibt-host-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Serilog.Log.CloseAndFlush();
        try
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }
        catch (IOException)
        {
            // A log file still held by the logger: the OS temp cleaner takes it.
        }
    }

    [Fact]
    public void The_graph_resolves_and_never_touches_the_real_registry_in_a_development_build()
    {
        WpfTestHost.Run(() =>
        {
            using var host = AppHost.Build(new AppPaths(_dir), new StartupArguments(Tray: true, TestLed: true));

            var shell = host.Services.GetRequiredService<AppShell>();
            var settings = host.Services.GetRequiredService<SettingsService>();
            var protector = host.Services.GetRequiredService<ISecretProtector>();

            Assert.NotNull(shell);
            Assert.NotNull(host.Services.GetRequiredService<UpdateService>());
            Assert.NotNull(host.Services.GetRequiredService<LegacyAppDetector>());
            Assert.False(host.Services.GetRequiredService<IUpdateClient>().IsInstalled); // a test run is not an installed copy
            Assert.Same(settings, host.Services.GetRequiredService<SettingsService>());
            Assert.IsType<DpapiSecretProtector>(protector);
            Assert.True(Directory.Exists(Path.Combine(_dir, "logs")));

#if DEBUG
            // A debug build must not write the HKCU Run value for its own build folder.
            var registration = host.Services.GetRequiredService<StartupRegistration>();
            Assert.Equal(StartupRegistrationResult.Disabled, registration.Apply());
#endif
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_bar_services_are_wired_and_test_led_reaches_the_watcher(bool testLed)
    {
        WpfTestHost.Run(() =>
        {
            using var host = AppHost.Build(new AppPaths(_dir), new StartupArguments(true, testLed));

            var watcher = host.Services.GetRequiredService<LedWatcher>();
            var controller = host.Services.GetRequiredService<OverlayController>();

            Assert.Same(watcher, host.Services.GetRequiredService<ILedSource>());
            Assert.Equal(testLed, watcher.TestMode);
            Assert.False(controller.CaptureExcluded); // the window is only made at Start
            Assert.IsType<CompositionFrameSource>(host.Services.GetRequiredService<IFrameSource>());
        });
    }

    [Fact]
    public void A_first_run_leaves_a_settings_file_behind()
    {
        WpfTestHost.Run(() =>
        {
            using var host = AppHost.Build(new AppPaths(_dir), new StartupArguments(true, false));
            var settings = host.Services.GetRequiredService<SettingsService>();

            settings.SaveIfMissing();

            Assert.True(File.Exists(Path.Combine(_dir, "settings.json")));
        });
    }
}
