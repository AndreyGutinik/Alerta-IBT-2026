using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using AlertaIBT.App.Hosting;
using AlertaIBT.App.Services;
using AlertaIBT.Core.Alerts;
using AlertaIBT.Core.Firebase;
using AlertaIBT.Core.Queue;
using AlertaIBT.Core.Services;
using AlertaIBT.Core.Settings;
using AlertaIBT.Core.Telegram;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AlertaIBT.App.Tests.Services;

// The whole alert pipeline built the way the app builds it (real DI graph, real HTTP listener on a free port, real files in a
// temporary folder), without the tray and without a Telegram token: nothing leaves the machine.
public sealed class AlertRuntimeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "alerta-ibt-rt-" + Guid.NewGuid().ToString("N"));
    private readonly int _port = FreePort();
    private readonly HttpClient _http = new();

    public AlertRuntimeTests() => Directory.CreateDirectory(_dir);

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

    private IHost Host() => AppHost.Build(new AppPaths(_dir), new StartupArguments(Tray: true, TestLed: false), _port);

    private async Task<string> Get(string path)
    {
        using var response = await _http.GetAsync($"http://127.0.0.1:{_port}{path}", TestContext.Current.CancellationToken);
        return await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public void The_alert_graph_resolves_and_shares_one_instance_of_each_part()
    {
        WpfTestHost.Run(() =>
        {
            using var host = Host();
            var services = host.Services;

            var alerts = services.GetRequiredService<AlertService>();
            Assert.Same(alerts, services.GetRequiredService<IOverlayControl>());
            Assert.Same(services.GetRequiredService<OverlayDisplay>(), services.GetRequiredService<IAlertDisplay>());
            Assert.Same(services.GetRequiredService<TelegramGroupNotifier>(), services.GetRequiredService<IGroupNotifier>());
            Assert.Same(services.GetRequiredService<AlertQueue>(), alerts.Queue);
            Assert.Equal(_port, services.GetRequiredService<HttpControlServer>().Port);
            Assert.NotNull(services.GetRequiredService<TelegramEngine>());
            Assert.NotNull(services.GetRequiredService<AlertRuntime>());
            Assert.Same(services.GetRequiredService<StatusHub>(), services.GetRequiredService<AlertRuntime>().Status);
        });
    }

    [Fact]
    public void The_api_port_defaults_to_the_one_holyrics_already_calls()
    {
        WpfTestHost.Run(() =>
        {
            using var host = AppHost.Build(new AppPaths(_dir), new StartupArguments(Tray: true, TestLed: false));

            Assert.Equal(8787, host.Services.GetRequiredService<HttpControlServer>().Port);
        });
    }

    [Fact]
    public void Holyrics_off_pauses_the_service_and_on_resumes_it()
    {
        WpfTestHost.RunAsync(async () =>
        {
            using var host = Host();
            var runtime = host.Services.GetRequiredService<AlertRuntime>();
            runtime.Start();
            try
            {
                Assert.Equal("""{"success":true,"overlayEnabled":true}""", await Get("/overlay/status"));

                Assert.Equal("""{"success":true,"overlayEnabled":false}""", await Get("/overlay/off"));
                Assert.True(runtime.Alerts.IsPaused);
                Assert.True(runtime.Status.Paused);
                Assert.Equal(AlertPill.Paused, runtime.Status.Pill);
                Assert.Equal(StatusLight.Green, runtime.Status.Http.Light);

                Assert.Equal("""{"success":true,"overlayEnabled":true}""", await Get("/overlay/on"));
                Assert.False(runtime.Alerts.IsPaused);
            }
            finally
            {
                runtime.Dispose();
            }

            return 0;
        });
    }

    [Fact]
    public void A_pc_alert_submitted_with_no_led_waits_in_the_queue_and_is_saved_to_disk()
    {
        WpfTestHost.RunAsync(async () =>
        {
            using var host = Host();
            var runtime = host.Services.GetRequiredService<AlertRuntime>();
            runtime.Start();
            try
            {
                var outcome = await runtime.Alerts.SubmitAsync("corolla prata abc1d23 vidro aberto", AlertSource.Pc, SenderRole.Operator, false, null, TestContext.Current.CancellationToken);

                Assert.Equal(SubmitKind.Queued, outcome.Kind);
                Assert.Equal(AlertPill.LedOff, runtime.Status.Pill); // this machine has no 2560x768 LED and the watcher is not started here
            }
            finally
            {
                runtime.Dispose(); // saves the queue one last time
            }

            var saved = new QueueStore(Path.Combine(_dir, "queue.json")).Load();
            Assert.Equal(["COROLLA PRATA | PLACA: ABC-1D23 | VIDRO ABERTO"], saved.Pending.Select(p => p.Text));
            return 0;
        });
    }

    [Fact]
    public void A_queue_saved_by_the_last_run_is_restored_and_a_pause_is_not()
    {
        File.WriteAllText(Path.Combine(_dir, "queue.json"), """
            { "version": 1, "pending": [ { "id": "old", "text": "GOL BRANCO", "source": "pc", "urgent": false, "createdAt": "2099-01-01T00:00:00+00:00" } ], "history": [], "paused": true }
            """);

        WpfTestHost.Run(() =>
        {
            using var host = Host();

            var queue = host.Services.GetRequiredService<AlertQueue>();

            Assert.Equal(["old"], queue.Pending.Select(p => p.Id));
            Assert.False(queue.IsPaused);
        });
    }

    [Fact]
    public void A_corrupt_queue_file_does_not_stop_the_app_from_starting()
    {
        File.WriteAllText(Path.Combine(_dir, "queue.json"), "{ this is not json");

        WpfTestHost.Run(() =>
        {
            using var host = Host();

            var queue = host.Services.GetRequiredService<AlertQueue>();

            Assert.Empty(queue.Pending);
            Assert.Single(Directory.GetFiles(_dir, "queue.bad-*.json"));
        });
    }

    [Fact]
    public void The_correction_dot_follows_the_settings()
    {
        WpfTestHost.Run(() =>
        {
            using var host = Host();
            var runtime = host.Services.GetRequiredService<AlertRuntime>();
            var settings = host.Services.GetRequiredService<SettingsService>();
            runtime.Start();
            try
            {
                settings.Update(s => s with { Correction = s.Correction with { Mode = "offline" } });
                Assert.Equal(StatusLight.Blue, runtime.Status.Gemini.Light);

                settings.Update(s => s with { Correction = s.Correction with { Enabled = false } });
                Assert.Equal(StatusLight.Gray, runtime.Status.Gemini.Light);

                settings.Update(s => s with { Correction = s.Correction with { Enabled = true, Mode = "gemini" } });
                Assert.Equal(StatusLight.Gray, runtime.Status.Gemini.Light); // Gemini mode, not asked yet
            }
            finally
            {
                runtime.Dispose();
            }
        });
    }

    [Fact]
    public void A_busy_api_port_does_not_stop_the_rest_and_is_shown_in_the_status()
    {
        using var squatter = new HttpListener();
        squatter.Prefixes.Add($"http://127.0.0.1:{_port}/");
        squatter.Start();

        WpfTestHost.RunAsync(async () =>
        {
            using var host = Host();
            var runtime = host.Services.GetRequiredService<AlertRuntime>();
            runtime.Start();
            try
            {
                Assert.Equal(StatusLight.Red, runtime.Status.Http.Light);
                var outcome = await runtime.Alerts.SubmitAsync("gol branco aaa1111", AlertSource.Pc, SenderRole.Operator, false, null, TestContext.Current.CancellationToken);
                Assert.Equal(SubmitKind.Queued, outcome.Kind);
            }
            finally
            {
                runtime.Dispose();
            }

            return 0;
        });
    }

    [Fact]
    public void Toggling_the_pause_and_starting_or_disposing_twice_are_safe()
    {
        WpfTestHost.Run(() =>
        {
            using var host = Host();
            var runtime = host.Services.GetRequiredService<AlertRuntime>();
            runtime.Start();
            runtime.Start(); // starting twice changes nothing

            runtime.TogglePause();
            Assert.True(runtime.Alerts.IsPaused);
            runtime.TogglePause();
            Assert.False(runtime.Alerts.IsPaused);

            runtime.Dispose();
            runtime.Dispose();
        });
    }

    [Fact]
    public void The_site_starts_and_stops_with_the_settings_and_a_changed_password_connects_again()
    {
        WpfTestHost.RunAsync(async () =>
        {
            using var host = Host();
            var runtime = host.Services.GetRequiredService<AlertRuntime>();
            var settings = host.Services.GetRequiredService<SettingsService>();
            var protector = host.Services.GetRequiredService<ISecretProtector>();
            runtime.Start();
            try
            {
                // Off and empty: the dot is gray and nothing connects.
                Assert.Equal(SiteState.NotConfigured, runtime.Status.SiteState);

                // Filled in and switched on (an address on this computer where nothing listens: no real service is called).
                settings.Update(s => (s with { Site = new SiteSettings { Enabled = true, DatabaseUrl = "http://127.0.0.1:1", ApiKey = "AIzaSyTestKey123", DeviceEmail = "pc@x.com" } })
                    .WithSitePassword("first-password-1", protector));
                var connector = host.Services.GetRequiredService<SiteConnector>();
                await connector.SettledAsync();
                Assert.NotEqual(SiteState.NotConfigured, runtime.Status.SiteState);

                // A new password is a new sign-in; the same settings again are not.
                settings.Update(s => s.WithSitePassword("second-password-2", protector));
                await connector.SettledAsync();
                Assert.NotEqual(SiteState.NotConfigured, runtime.Status.SiteState);

                // Switched off: the connection stops and the dot goes gray again.
                settings.Update(s => s with { Site = s.Site with { Enabled = false } });
                await connector.SettledAsync();
                Assert.Equal(SiteState.NotConfigured, runtime.Status.SiteState);
                Assert.Equal(StatusLight.Gray, runtime.Status.Site.Light);
            }
            finally
            {
                runtime.Dispose();
            }

            return 0;
        });
    }

    [Fact]
    public void Settings_saved_with_the_site_on_start_it_with_the_app_and_stop_it_on_exit()
    {
        WpfTestHost.RunAsync(async () =>
        {
            using var host = Host();
            var runtime = host.Services.GetRequiredService<AlertRuntime>();
            var settings = host.Services.GetRequiredService<SettingsService>();
            settings.Update(s => (s with { Site = new SiteSettings { Enabled = true, DatabaseUrl = "http://127.0.0.1:1", ApiKey = "AIzaSyTestKey123", DeviceEmail = "pc@x.com" } })
                .WithSitePassword("first-password-1", host.Services.GetRequiredService<ISecretProtector>()));

            runtime.Start();
            await host.Services.GetRequiredService<SiteConnector>().SettledAsync();

            Assert.NotEqual(SiteState.NotConfigured, runtime.Status.SiteState);
            runtime.Dispose(); // must return: the engine stops with it
            return 0;
        });
    }

    [Fact]
    public void The_history_clearing_is_wired_and_the_first_start_only_begins_the_weekly_count()
    {
        WpfTestHost.RunAsync(async () =>
        {
            using var host = Host();
            var runtime = host.Services.GetRequiredService<AlertRuntime>();
            var settings = host.Services.GetRequiredService<SettingsService>();
            Assert.Equal(Path.Combine(_dir, "telegram-journal.json"), host.Services.GetRequiredService<TelegramJournal>().FilePath);
            Assert.Null(settings.Current.System.LastHistoryClearAt);
            var queue = host.Services.GetRequiredService<AlertQueue>();
            queue.AddRejected(new QueueItem("old", "X", null, AlertSource.Pc, false, DateTimeOffset.UtcNow, null, "x"), HistoryReasons.TooLong);

            runtime.Start();
            try
            {
                Assert.NotNull(settings.Current.System.LastHistoryClearAt);
                Assert.True(settings.Current.System.ClearHistoryWeekly);
                Assert.Single(queue.History); // the first run starts the count; it wipes nothing

                var result = await host.Services.GetRequiredService<IAlertGateway>().ClearHistoryAsync(TestContext.Current.CancellationToken);

                Assert.Equal(new HistoryClearResult(1, SiteClearOutcome.NotConfigured, 0, 0, 0), result);
                Assert.Empty(queue.History);
            }
            finally
            {
                runtime.Dispose();
            }

            return 0;
        });
    }
}
