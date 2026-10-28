using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using AlertaIBT.App.Hosting;
using AlertaIBT.App.Platform;
using AlertaIBT.App.Services;
using AlertaIBT.App.ViewModels;
using AlertaIBT.Core.Audio;
using AlertaIBT.Core.Services;
using AlertaIBT.Core.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AlertaIBT.App.Tests.Services;

// The master switch "Alertas ligados" on the real graph of the app (real DI, a real HTTP listener on a free port, files in a temp folder):
// off starts nothing of the alert side, answers the overlay routes "alertas desligados", leaves the player's API alone, and on starts it all again.
public sealed class AlertsRuntimeTests : IDisposable
{
    private const string Disabled = """{"success":false,"disabled":true,"message":"alertas desligados"}""";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "alerta-ibt-alerts-" + Guid.NewGuid().ToString("N"));
    private readonly int _port = FreePort();
    private readonly HttpClient _http = new();

    public AlertsRuntimeTests() => Directory.CreateDirectory(_dir);

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

    private IHost Host(bool alertsOn)
    {
        var paths = new AppPaths(_dir);
        new SettingsStore(paths.SettingsFile).Save(new AppSettings { System = new SystemSettings { AlertsEnabled = alertsOn } });
        return AppHost.Build(paths, new StartupArguments(Tray: true, TestLed: false), _port);
    }

    private async Task<string> Get(string path)
    {
        using var response = await _http.GetAsync($"http://127.0.0.1:{_port}{path}", TestContext.Current.CancellationToken);
        return await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public void With_the_alerts_off_nothing_of_the_alert_side_starts_and_the_api_says_so_but_the_player_api_still_answers()
    {
        WpfTestHost.RunAsync(async () =>
        {
            using var host = Host(alertsOn: false);
            var runtime = host.Services.GetRequiredService<AlertRuntime>();
            runtime.Start();
            try
            {
                Assert.False(runtime.AlertsOn);
                Assert.False(runtime.Status.AlertsOn);
                Assert.Equal(Disabled, await Get("/overlay/status"));
                Assert.Equal(Disabled, await Get("/overlay/off"));
                Assert.Equal(Disabled, await Get("/overlay/on"));
                Assert.False(runtime.Alerts.IsPaused); // "off" did not pause anything
                Assert.Contains("\"success\":true", await Get("/player/status"), StringComparison.Ordinal);
                Assert.True(host.Services.GetRequiredService<HttpControlServer>().IsListening); // the API stays up, to answer "alertas desligados"
            }
            finally
            {
                runtime.Dispose();
            }

            return 0;
        });
    }

    [Fact]
    public void Turning_it_on_starts_everything_and_the_routes_answer_as_in_v7_and_turning_it_off_stops_it_again()
    {
        WpfTestHost.RunAsync(async () =>
        {
            using var host = Host(alertsOn: false);
            var runtime = host.Services.GetRequiredService<AlertRuntime>();
            runtime.Start();
            try
            {
                runtime.SetAlertsEnabled(true);
                await runtime.AlertsSettledAsync();

                Assert.True(runtime.AlertsOn);
                Assert.True(runtime.Status.AlertsOn);
                Assert.Equal("""{"success":true,"overlayEnabled":true}""", await Get("/overlay/status"));
                Assert.Equal("""{"success":true,"overlayEnabled":false}""", await Get("/overlay/off"));
                Assert.True(runtime.Alerts.IsPaused);
                Assert.Equal("""{"success":true,"overlayEnabled":true}""", await Get("/overlay/on"));

                runtime.SetAlertsEnabled(false);
                await runtime.AlertsSettledAsync();

                Assert.False(runtime.AlertsOn);
                Assert.False(runtime.Status.AlertsOn);
                Assert.Equal(Disabled, await Get("/overlay/status"));
                Assert.Equal(StatusLight.Gray, runtime.Status.Telegram.Light);
                Assert.Equal("Desligado", runtime.Status.Bar.Text);

                runtime.SetAlertsEnabled(true);
                await runtime.AlertsSettledAsync();

                Assert.True(runtime.AlertsOn);
                Assert.Equal("""{"success":true,"overlayEnabled":true}""", await Get("/overlay/status"));
            }
            finally
            {
                runtime.Dispose();
            }

            return 0;
        });
    }

    [Fact]
    public void With_the_alerts_on_at_start_everything_runs_as_before()
    {
        WpfTestHost.RunAsync(async () =>
        {
            using var host = Host(alertsOn: true);
            var runtime = host.Services.GetRequiredService<AlertRuntime>();
            runtime.Start();
            try
            {
                Assert.True(runtime.AlertsOn);
                Assert.Equal("""{"success":true,"overlayEnabled":true}""", await Get("/overlay/status"));
            }
            finally
            {
                runtime.Dispose();
            }

            return 0;
        });
    }

    [Fact]
    public void The_sound_graph_is_wired_the_audio_controller_waits_for_the_shell_and_the_som_tab_is_in_the_main_view_model()
    {
        WpfTestHost.Run(() =>
        {
            using var host = Host(alertsOn: true);
            var services = host.Services;

            var audio = services.GetRequiredService<AudioController>();

            Assert.Same(audio, services.GetRequiredService<AppShell>().Audio);
            Assert.False(audio.Status.Known); // nothing is read or written before the shell starts it
            Assert.IsType<WindowsAppOutputPolicy>(services.GetRequiredService<IAppOutputPolicy>());
            Assert.Same(services.GetRequiredService<SomViewModel>(), services.GetRequiredService<MainViewModel>().Som);
            Assert.Same(services.GetRequiredService<PlayerViewModel>(), services.GetRequiredService<SomViewModel>().Player);
        });
    }
}
