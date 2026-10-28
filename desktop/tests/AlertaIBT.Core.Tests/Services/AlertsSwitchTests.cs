using System.Net;
using System.Net.Sockets;
using AlertaIBT.Core.Services;
using AlertaIBT.Core.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace AlertaIBT.Core.Tests.Services;

// The master switch "Alertas ligados": the start and stop of the parts, the setting, the dots, and what the local API answers while it is off.
public sealed class AlertsSwitchTests : IDisposable
{
    private readonly List<string> _events = [];
    private readonly StatusHub _hub = new();
    private readonly List<HttpControlServer> _servers = [];
    private readonly HttpClient _http = new();
    private bool _wanted = true;
    private AlertsSwitch? _switch;

    public void Dispose()
    {
        _switch?.Dispose();
        _http.Dispose();
        foreach (var server in _servers)
        {
            server.Dispose();
        }
    }

    private AlertsPart Part(string name, bool failOnStart = false, bool failOnStop = false) =>
        new(
            name,
            () =>
            {
                _events.Add("start " + name);
                if (failOnStart)
                {
                    throw new InvalidOperationException("boom");
                }
            },
            async () =>
            {
                await Task.Yield();
                _events.Add("stop " + name);
                if (failOnStop)
                {
                    throw new InvalidOperationException("boom");
                }
            });

    private AlertsSwitch Make(params AlertsPart[] parts)
    {
        _switch = new AlertsSwitch(() => _wanted, parts, _hub, NullLogger.Instance);
        return _switch;
    }

    private async Task Flip(bool on)
    {
        _wanted = on;
        _switch!.Request();
        await _switch.SettledAsync();
    }

    // ---- start and stop ----

    [Fact]
    public void With_the_setting_on_the_parts_start_in_order_before_initialize_returns()
    {
        var alerts = Make(Part("fila"), Part("telegram"), Part("site"));

        alerts.Initialize();

        Assert.Equal(["start fila", "start telegram", "start site"], _events);
        Assert.True(alerts.IsOn);
        Assert.True(_hub.AlertsOn);
    }

    [Fact]
    public void With_the_setting_off_nothing_starts_at_the_beginning_and_the_hub_says_off()
    {
        _wanted = false;
        var alerts = Make(Part("fila"), Part("telegram"), Part("site"));

        alerts.Initialize();

        Assert.Empty(_events);
        Assert.False(alerts.IsOn);
        Assert.False(_hub.AlertsOn);
    }

    [Fact]
    public async Task Turning_it_off_stops_the_parts_in_reverse_order_and_turning_it_on_starts_them_again()
    {
        var alerts = Make(Part("fila"), Part("telegram"), Part("site"));
        alerts.Initialize();
        _events.Clear();
        var changes = new List<bool>();
        alerts.Changed += changes.Add;

        await Flip(false);

        Assert.Equal(["stop site", "stop telegram", "stop fila"], _events);
        Assert.False(alerts.IsOn);
        Assert.False(_hub.AlertsOn);

        _events.Clear();
        await Flip(true);

        Assert.Equal(["start fila", "start telegram", "start site"], _events);
        Assert.True(alerts.IsOn);
        Assert.True(_hub.AlertsOn);
        Assert.Equal([false, true], changes);
    }

    [Fact]
    public async Task Asking_again_for_the_state_it_already_has_does_nothing()
    {
        var alerts = Make(Part("fila"));
        alerts.Initialize();
        _events.Clear();

        await Flip(true);
        await Flip(true);

        Assert.Empty(_events);
    }

    [Fact]
    public async Task Quick_flips_end_in_the_last_value_and_never_start_a_part_twice_in_a_row()
    {
        var alerts = Make(Part("fila"));
        alerts.Initialize();
        _events.Clear();

        _wanted = false;
        alerts.Request();
        _wanted = true;
        alerts.Request();
        _wanted = false;
        alerts.Request();
        await alerts.SettledAsync();
        await Task.Delay(50, TestContext.Current.CancellationToken);
        await alerts.SettledAsync();

        Assert.False(alerts.IsOn);
        Assert.False(_hub.AlertsOn);
        Assert.Equal("stop fila", _events[^1]);
        for (var i = 1; i < _events.Count; i++)
        {
            Assert.NotEqual(_events[i - 1], _events[i]); // never two starts or two stops in a row
        }
    }

    [Fact]
    public async Task A_part_that_fails_is_reported_and_the_others_still_start_and_stop()
    {
        var alerts = Make(Part("fila"), Part("telegram", failOnStart: true, failOnStop: true), Part("site"));

        alerts.Initialize();
        Assert.Equal(["start fila", "start telegram", "start site"], _events);
        Assert.Contains("Alertas", _hub.LastError, StringComparison.Ordinal);

        _events.Clear();
        await Flip(false);
        Assert.Equal(["stop site", "stop telegram", "stop fila"], _events);
        Assert.False(alerts.IsOn);
    }

    [Fact]
    public void The_telegram_site_and_bar_dots_are_gray_desligado_while_it_is_off_and_come_back_when_on()
    {
        _hub.SetTelegram(AlertaIBT.Core.Telegram.TelegramState.Online);
        _hub.SetSite(AlertaIBT.Core.Firebase.SiteState.Online);
        Assert.Equal(StatusLight.Green, _hub.Telegram.Light);
        var changed = new List<string>();
        _hub.PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? string.Empty);

        _hub.SetAlertsOn(false);

        Assert.Equal(StatusLight.Gray, _hub.Telegram.Light);
        Assert.Equal("Desligado", _hub.Telegram.Text);
        Assert.Equal(StatusLight.Gray, _hub.Site.Light);
        Assert.Equal(StatusLight.Gray, _hub.Bar.Light);
        Assert.Equal("Desligado", _hub.Bar.Text);
        Assert.Contains(nameof(StatusHub.AlertsOn), changed);
        Assert.Contains(nameof(StatusHub.Telegram), changed);

        _hub.SetAlertsOn(true);

        Assert.Equal(StatusLight.Green, _hub.Telegram.Light);
        Assert.Equal(StatusLight.Green, _hub.Site.Light);
    }

    // ---- the setting ----

    [Fact]
    public void The_setting_is_on_by_default_survives_a_save_and_an_old_file_loads_as_on()
    {
        Assert.True(new AppSettings().System.AlertsEnabled);

        var off = new AppSettings { System = new SystemSettings { AlertsEnabled = false } };
        var (loaded, corrupt) = SettingsJson.Load(SettingsJson.Save(off));
        Assert.False(corrupt);
        Assert.False(loaded.System.AlertsEnabled);

        var (old, _) = SettingsJson.Load("""{ "system": { "hotkey": "Ctrl+Alt+Space" } }""");
        Assert.True(old.System.AlertsEnabled);
        var (empty, _) = SettingsJson.Load("{}");
        Assert.True(empty.System.AlertsEnabled);
    }

    // ---- the local API ----

    private sealed class FakeOverlay : IOverlayControl
    {
        public bool IsPaused { get; private set; }

        public int Calls { get; private set; }

        public void Pause()
        {
            Calls++;
            IsPaused = true;
        }

        public void Resume()
        {
            Calls++;
            IsPaused = false;
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

    private async Task<(int Port, FakeOverlay Overlay)> StartApi(Func<bool>? alertsOn)
    {
        var overlay = new FakeOverlay();
        var port = FreePort();
        var server = new HttpControlServer(overlay, _hub, NullLogger.Instance, port, new FakeTimeProvider(), alertsOn: alertsOn);
        _servers.Add(server);
        Assert.True(server.Start());
        await Task.CompletedTask;
        return (port, overlay);
    }

    private async Task<(HttpStatusCode Status, string Body)> Call(int port, string method, string path)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), new Uri($"http://127.0.0.1:{port}{path}"));
        var response = await _http.SendAsync(request, TestContext.Current.CancellationToken);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("GET", "/overlay/on")]
    [InlineData("GET", "/overlay/off")]
    [InlineData("POST", "/overlay/off")]
    [InlineData("GET", "/overlay/status")]
    [InlineData("GET", "/overlay/pause")]
    public async Task With_the_alerts_off_the_overlay_routes_answer_200_disabled_and_touch_nothing(string method, string path)
    {
        var (port, overlay) = await StartApi(() => false);

        var (status, body) = await Call(port, method, path);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("""{"success":false,"disabled":true,"message":"alertas desligados"}""", body);
        Assert.Equal(0, overlay.Calls);
    }

    [Fact]
    public async Task With_the_alerts_off_an_unknown_route_is_still_404_and_options_still_204()
    {
        var (port, _) = await StartApi(() => false);

        var (status, body) = await Call(port, "GET", "/nada");
        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.Contains("not_found", body, StringComparison.Ordinal);

        var (options, _) = await Call(port, "OPTIONS", "/overlay/on");
        Assert.Equal(HttpStatusCode.NoContent, options);
    }

    [Fact]
    public async Task With_the_alerts_on_or_unset_the_routes_keep_the_v7_answers()
    {
        var on = true;
        var (port, overlay) = await StartApi(() => on);

        Assert.Equal("""{"success":true,"overlayEnabled":false}""", (await Call(port, "GET", "/overlay/off")).Body);
        Assert.Equal("""{"success":true,"overlayEnabled":true}""", (await Call(port, "GET", "/overlay/on")).Body);
        Assert.Equal(2, overlay.Calls);

        on = false;
        Assert.Contains("\"disabled\":true", (await Call(port, "GET", "/overlay/status")).Body, StringComparison.Ordinal);
        on = true; // turned on again: answers as before
        Assert.Equal("""{"success":true,"overlayEnabled":true}""", (await Call(port, "GET", "/overlay/status")).Body);

        var (portNone, _) = await StartApi(null);
        Assert.Equal("""{"success":true,"overlayEnabled":true}""", (await Call(portNone, "GET", "/overlay/status")).Body);
    }
}
