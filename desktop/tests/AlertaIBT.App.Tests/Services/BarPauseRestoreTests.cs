using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using AlertaIBT.App.Hosting;
using AlertaIBT.App.Services;
using AlertaIBT.Core.Services;
using AlertaIBT.Core.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AlertaIBT.App.Tests.Services;

// "Pausar barra" is the owner's last position: saved when it changes, and put back (silently) when the alert side starts. Real graph, free port, temp folder.
public sealed class BarPauseRestoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "alerta-ibt-pause-" + Guid.NewGuid().ToString("N"));
    private readonly int _port = FreePort();
    private readonly HttpClient _http = new();

    public BarPauseRestoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _http.Dispose();
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

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private IHost Host(AppSettings? saved)
    {
        var paths = new AppPaths(_dir);
        if (saved is not null)
        {
            new SettingsStore(paths.SettingsFile).Save(saved);
        }

        return AppHost.Build(paths, new StartupArguments(Tray: true, TestLed: false), _port);
    }

    private async Task<string> Get(string path)
    {
        using var response = await _http.GetAsync($"http://127.0.0.1:{_port}{path}", TestContext.Current.CancellationToken);
        return await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
    }

    private static AppSettings Paused(bool alertsOn = true) =>
        new() { System = new SystemSettings { BarPaused = true, AlertsEnabled = alertsOn } };

    [Fact]
    public void A_first_install_starts_with_the_bar_running()
    {
        WpfTestHost.Run(() =>
        {
            using var host = Host(saved: null);
            var runtime = host.Services.GetRequiredService<AlertRuntime>();
            runtime.Start();
            try
            {
                Assert.False(runtime.Alerts.IsPaused);
                Assert.False(runtime.Status.Paused);
            }
            finally
            {
                runtime.Dispose();
            }
        });
    }

    [Fact]
    public void A_bar_left_paused_comes_back_paused_through_the_real_start_with_the_api_and_the_hub_agreeing()
    {
        WpfTestHost.RunAsync(async () =>
        {
            using var host = Host(Paused());
            var runtime = host.Services.GetRequiredService<AlertRuntime>();
            runtime.Start();
            try
            {
                Assert.True(runtime.Alerts.IsPaused);
                Assert.True(runtime.Status.Paused);
                Assert.Equal("""{"success":true,"overlayEnabled":false}""", await Get("/overlay/status"));
                Assert.True(host.Services.GetRequiredService<SettingsService>().Current.System.BarPaused); // the restore never rewrote it
            }
            finally
            {
                runtime.Dispose();
            }

            return 0;
        });
    }

    [Fact]
    public void Pausing_and_resuming_from_any_door_is_saved_and_the_next_start_follows()
    {
        WpfTestHost.Run(() =>
        {
            using (var host = Host(saved: null))
            {
                var runtime = host.Services.GetRequiredService<AlertRuntime>();
                var settings = host.Services.GetRequiredService<SettingsService>();
                runtime.Start();
                try
                {
                    runtime.TogglePause(); // the tray, the panels, Holyrics and Telegram all end in the alert service
                    Assert.True(settings.Current.System.BarPaused);
                    runtime.TogglePause();
                    Assert.False(settings.Current.System.BarPaused);
                    runtime.Alerts.Pause();
                    Assert.True(settings.Current.System.BarPaused);
                    settings.Flush();
                }
                finally
                {
                    runtime.Dispose();
                }
            }

            using var next = AppHost.Build(new AppPaths(_dir), new StartupArguments(Tray: true, TestLed: false), _port);
            var again = next.Services.GetRequiredService<AlertRuntime>();
            again.Start();
            try
            {
                Assert.True(again.Alerts.IsPaused);
            }
            finally
            {
                again.Dispose();
            }
        });
    }

    [Fact]
    public void With_the_alerts_switched_off_a_saved_pause_is_still_held_and_comes_back_when_they_turn_on()
    {
        WpfTestHost.RunAsync(async () =>
        {
            using var host = Host(Paused(alertsOn: false));
            var runtime = host.Services.GetRequiredService<AlertRuntime>();
            runtime.Start();
            try
            {
                Assert.False(runtime.AlertsOn);
                Assert.True(runtime.Alerts.IsPaused);

                runtime.SetAlertsEnabled(true);
                await runtime.AlertsSettledAsync();

                Assert.True(runtime.AlertsOn);
                Assert.True(runtime.Alerts.IsPaused);
            }
            finally
            {
                runtime.Dispose();
            }

            return 0;
        });
    }
}
