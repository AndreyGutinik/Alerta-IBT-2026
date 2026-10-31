using AlertaIBT.Core.Control;
using AlertaIBT.Core.Holyrics;
using AlertaIBT.Core.Settings;
using AlertaIBT.Core.Tests.Control;
using AlertaIBT.Core.Tests.Settings;

namespace AlertaIBT.Core.Tests.Holyrics;

// The rows 13 to 16 of the Controle map, from Art-Net packets to requests on the wire.
public sealed class HolyricsMapIntegrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ibt-holyrics-map-" + Guid.NewGuid().ToString("N"));
    private readonly FakeHolyricsServer _server = new();
    private readonly SettingsService _settings;
    private readonly HolyricsClient _client;
    private readonly FakeArtNetSource _source = new();
    private readonly ControlService _service;

    public HolyricsMapIntegrationTests()
    {
        Directory.CreateDirectory(_dir);
        var protector = new FakeProtector();
        _settings = new SettingsService(new SettingsStore(Path.Combine(_dir, "settings.json")));
        _settings.Update(s => s.WithHolyricsToken(FakeHolyricsServer.GoodToken, protector));
        _settings.Update(s => s with { Holyrics = s.Holyrics with { Port = _server.Port } });
        _client = new HolyricsClient(_settings, protector, minInterval: TimeSpan.Zero);
        var targets = new ControlTargets(new FakeControlNdi(), new FakePlayerControl(), new FakeControlVolumes(), _client);
        _service = new ControlService(_settings, targets, () => _source);
        _service.Start();
        _settings.Update(s => s with { Control = s.Control with { Enabled = true } });
    }

    public void Dispose()
    {
        _service.Dispose();
        _client.Dispose();
        _server.Dispose();
        _settings.Dispose();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // a late write of the settings
        }
    }

    private void Send(params (int Channel, byte Value)[] levels)
    {
        var data = new byte[512];
        foreach (var (channel, value) in levels)
        {
            data[channel - 1] = value;
        }

        _source.Raise(new ArtNetArrival(new ArtDmxPacket(0, 0, 99, data), System.Net.IPAddress.Loopback));
    }

    private async Task WaitFor(int requests)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (_server.Requests.Count < requests && DateTime.UtcNow < until)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Rows_13_to_16_of_the_map_reach_holyrics_through_the_api()
    {
        Send(); // baseline
        await Task.Delay(60, TestContext.Current.CancellationToken);

        Send((13, 255)); // slide next
        await WaitFor(1);
        Send((13, 255), (14, 255)); // slide previous
        await WaitFor(2);
        Send((13, 255), (14, 255), (15, 255)); // video play/pause: info + action
        await WaitFor(4);
        Send((13, 255), (14, 255), (15, 255), (16, 255)); // logo (Botao, any change: flips)
        await WaitFor(5);
        await Task.Delay(150, TestContext.Current.CancellationToken); // the engine debounces a channel for 100 ms
        Send((13, 255), (14, 255), (15, 255), (16, 0)); // logo again: flips back
        await WaitFor(6);

        Assert.Equal(["ActionNext", "ActionPrevious", "GetMediaPlayerInfo", "MediaPlayerAction", "SetLogo", "SetLogo"], _server.Actions);
        Assert.Equal("{\"enable\":true,\"hide\":false}", _server.Requests[4].Body);
        Assert.Equal("{\"enable\":false,\"hide\":true}", _server.Requests[5].Body);
    }

    [Fact]
    public async Task The_logo_row_in_state_mode_follows_the_channel()
    {
        _settings.Update(s => s with { Control = s.Control with { Rows = s.Control.Rows.Select(r => r.Action == ControlAction.LogoToggle ? r with { Mode = ControlMode.State } : r).ToList() } });
        Send();
        await Task.Delay(60, TestContext.Current.CancellationToken);

        Send((16, 255));
        await WaitFor(1);
        Send((16, 0));
        await WaitFor(2);

        Assert.Equal("{\"enable\":true,\"hide\":false}", _server.Requests[0].Body);
        Assert.Equal("{\"enable\":false,\"hide\":true}", _server.Requests[1].Body);
    }

    [Fact]
    public async Task The_test_button_of_a_row_sends_the_same_request()
    {
        var row = _settings.Current.Control.Rows.First(r => r.Action == ControlAction.SlideNext);

        _service.Test(row);
        await WaitFor(1);

        Assert.Equal(["ActionNext"], _server.Actions);
    }
}
