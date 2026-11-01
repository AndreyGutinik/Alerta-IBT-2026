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

// "Pausar barra" is the ONE switch that is not restored (owner, batch 40): the app always starts with the bar running, so the alerts work after the PC is turned on again. Real graph, free port, temp folder.
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

    // What an app of batch 39 left behind: the bar paused, and the pause saved in the file.
    private void OldFileWithPause(bool alertsOn = true)
    {
        var paths = new AppPaths(_dir);
        File.WriteAllText(paths.SettingsFile, "{\"system\":{\"barPaused\":true,\"alertsEnabled\":" + (alertsOn ? "true" : "false") + "}}");
    }

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
    public void An_old_file_with_the_bar_paused_always_starts_running_through_the_real_start_with_the_api_and_the_hub_agreeing()
    {
        WpfTestHost.RunAsync(async () =>
        {
            OldFileWithPause();
            using var host = Host(saved: null);
            var runtime = host.Services.GetRequiredService<AlertRuntime>();
            runtime.Start();
            try
            {
                Assert.False(runtime.Alerts.IsPaused);
                Assert.False(runtime.Status.Paused);
                Assert.Equal("""{"success":true,"overlayEnabled":true}""", await Get("/overlay/status"));

                // The old key is gone from the file at the next save; the rest of it is kept.
                var settings = host.Services.GetRequiredService<SettingsService>();
                settings.Update(s => s with { System = s.System with { AlertsEnabled = true } });
                settings.Flush();
                Assert.DoesNotContain("barPaused", File.ReadAllText(new AppPaths(_dir).SettingsFile));
            }
            finally
            {
                runtime.Dispose();
            }

            return 0;
        });
    }

    [Fact]
    public void Pausing_in_the_session_works_from_any_door_but_is_never_saved_so_the_next_start_runs()
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
                    Assert.True(runtime.Alerts.IsPaused);
                    Assert.True(runtime.Status.Paused);
                    runtime.TogglePause();
                    Assert.False(runtime.Alerts.IsPaused);
                    runtime.Alerts.Pause();
                    Assert.True(runtime.Alerts.IsPaused);
                    settings.Update(s => s with { System = s.System with { AlertsEnabled = true } });
                    settings.Flush();
                    Assert.DoesNotContain("barPaused", File.ReadAllText(new AppPaths(_dir).SettingsFile));
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
                Assert.False(again.Alerts.IsPaused);
            }
            finally
            {
                again.Dispose();
            }
        });
    }

    [Fact]
    public void With_the_alerts_switched_off_an_old_saved_pause_is_not_held_either()
    {
        WpfTestHost.RunAsync(async () =>
        {
            OldFileWithPause(alertsOn: false);
            using var host = Host(saved: null);
            var runtime = host.Services.GetRequiredService<AlertRuntime>();
            runtime.Start();
            try
            {
                Assert.False(runtime.AlertsOn);
                Assert.False(runtime.Alerts.IsPaused);

                runtime.SetAlertsEnabled(true);
                await runtime.AlertsSettledAsync();

                Assert.True(runtime.AlertsOn);
                Assert.False(runtime.Alerts.IsPaused);
            }
            finally
            {
                runtime.Dispose();
            }

            return 0;
        });
    }
}
