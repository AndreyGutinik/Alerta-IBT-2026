using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using AlertaIBT.Core.Audio;
using AlertaIBT.Core.Http;
using AlertaIBT.Core.Input;
using AlertaIBT.Core.Player;
using AlertaIBT.Core.Services;
using AlertaIBT.Core.Settings;
using AlertaIBT.Core.Tests.Services;

namespace AlertaIBT.Core.Tests.Player;

// The /player routes of the local API (through the real HTTP server on a free port), and the media keys, against a fake player.
public sealed class PlayerApiTests : IDisposable
{
    private sealed class FakeOverlay : IOverlayControl
    {
        public bool IsPaused { get; private set; }

        public int Pauses { get; private set; }

        public void Pause()
        {
            Pauses++;
            IsPaused = true;
        }

        public void Resume() => IsPaused = false;
    }

    private sealed class FakePlayer : IPlayerControl
    {
        public List<string> Calls { get; } = [];

        public bool Enabled { get; set; } = true;

        public PlayerState State { get; set; } = PlayerState.Stopped;

        public bool Fading { get; set; }

        public PlayerResult Play() => Do("play", PlayerState.Playing);

        public PlayerResult Pause() => Do("pause", PlayerState.Paused);

        public PlayerResult Stop() => Do("stop", PlayerState.Stopped);

        public PlayerResult Next() => Do("next", PlayerState.Playing);

        public PlayerResult Previous() => Do("previous", PlayerState.Playing);

        public PlayerStatus GetStatus() => new(
            Enabled,
            State,
            Fading,
            State == PlayerState.Playing ? new TrackInfo("/m/Amazing Grace - Josh.flac", "Amazing Grace - Josh.flac", "Amazing Grace", "Josh") : null,
            TimeSpan.FromSeconds(12.34),
            TimeSpan.FromSeconds(150),
            "Igreja",
            237,
            80,
            false,
            true,
            PlayerRepeat.All,
            new OutputStatus("Sa\U000000EDda do player: WING-USB (Out 3-4)", OutputChoiceKind.ByRule, "WING-USB (Out 3-4)"),
            null);

        private PlayerResult Do(string call, PlayerState next)
        {
            Calls.Add(call);
            if (!Enabled)
            {
                return PlayerResult.Disabled;
            }

            State = next;
            return PlayerResult.Ok;
        }
    }

    private readonly FakeOverlay _overlay = new();
    private readonly FakePlayer _player = new();
    private readonly HttpClient _http = new();
    private readonly List<HttpControlServer> _servers = [];
    private readonly int _port = FreePort();

    public void Dispose()
    {
        _http.Dispose();
        foreach (var server in _servers)
        {
            server.Dispose();
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

    private void Start(bool withPlayer = true)
    {
        var server = new HttpControlServer(
            _overlay,
            new StatusHub(),
            new CapturingLogger(),
            _port,
            null,
            withPlayer ? _player : null,
            () => "Pr\U000000F3ximo despertador: domingo 18:00");
        _servers.Add(server);
        Assert.True(server.Start());
    }

    private async Task<(HttpResponseMessage Response, JsonElement Body)> Call(string method, string path)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), $"http://127.0.0.1:{_port}{path}");
        var response = await _http.SendAsync(request, TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return (response, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    // ---- routes ----

    [Theory]
    [InlineData("GET", "/player/play", "play")]
    [InlineData("POST", "/player/play", "play")]
    [InlineData("GET", "/player/pause", "pause")]
    [InlineData("POST", "/player/pause/", "pause")]
    [InlineData("GET", "/player/stop?x=1", "stop")]
    [InlineData("GET", "/player/next", "next")]
    [InlineData("GET", "/player/previous", "previous")]
    [InlineData("GET", "/PLAYER/Pause", "pause")]
    public async Task Each_route_runs_its_command_and_answers_with_success_and_the_status(string method, string path, string call)
    {
        Start();

        var (response, body) = await Call(method, path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal([call], _player.Calls);
        Assert.True(body.GetProperty("success").GetBoolean());
        Assert.Equal("http://127.0.0.1", response.Headers.GetValues("Access-Control-Allow-Origin").Single());
    }

    [Fact]
    public async Task Status_reports_the_whole_player_and_changes_nothing()
    {
        _player.State = PlayerState.Playing;
        Start();

        var (response, body) = await Call("GET", "/player/status");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(_player.Calls);
        Assert.True(body.GetProperty("success").GetBoolean());
        Assert.True(body.GetProperty("enabled").GetBoolean());
        Assert.Equal("playing", body.GetProperty("state").GetString());
        var track = body.GetProperty("track");
        Assert.Equal("Amazing Grace", track.GetProperty("title").GetString());
        Assert.Equal("Josh", track.GetProperty("artist").GetString());
        Assert.Equal("Amazing Grace - Josh.flac", track.GetProperty("file").GetString());
        Assert.Equal(12.3, body.GetProperty("positionSeconds").GetDouble());
        Assert.Equal(150.0, body.GetProperty("durationSeconds").GetDouble());
        Assert.Equal("Igreja", body.GetProperty("playlist").GetString());
        Assert.Equal(80, body.GetProperty("volume").GetInt32());
        Assert.True(body.GetProperty("shuffle").GetBoolean());
        Assert.Equal("all", body.GetProperty("repeat").GetString());
        Assert.Equal("WING-USB (Out 3-4)", body.GetProperty("output").GetProperty("device").GetString());
        Assert.Equal("Pr\U000000F3ximo despertador: domingo 18:00", body.GetProperty("nextAlarm").GetString());
    }

    [Fact]
    public async Task With_the_player_off_a_command_answers_success_false_player_desligado()
    {
        _player.Enabled = false;
        Start();

        var (response, body) = await Call("GET", "/player/pause");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(body.GetProperty("success").GetBoolean());
        Assert.Equal("player desligado", body.GetProperty("error").GetString());
        Assert.False(body.GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task An_unknown_player_route_is_404_with_the_same_body_as_every_other_unknown_route()
    {
        Start();

        var (response, body) = await Call("GET", "/player/explode");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.False(body.GetProperty("success").GetBoolean());
        Assert.Equal("not_found", body.GetProperty("error").GetString());
        Assert.Equal("/player/explode", body.GetProperty("received").GetProperty("url").GetString());
        Assert.Empty(_player.Calls);
    }

    [Fact]
    public async Task Without_a_player_the_routes_are_404_and_the_overlay_routes_still_work()
    {
        Start(withPlayer: false);

        var (missing, _) = await Call("GET", "/player/pause");
        var (overlay, body) = await Call("GET", "/overlay/off");

        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(HttpStatusCode.OK, overlay.StatusCode);
        Assert.False(body.GetProperty("overlayEnabled").GetBoolean());
        Assert.Equal(1, _overlay.Pauses);
    }

    [Fact]
    public async Task The_overlay_routes_do_not_touch_the_player()
    {
        Start();

        await Call("GET", "/overlay/off");
        await Call("GET", "/overlay/status");

        Assert.Empty(_player.Calls);
        Assert.True(_overlay.IsPaused);
    }

    [Fact]
    public async Task Options_is_204_for_a_player_path_and_a_put_is_404()
    {
        Start();

        var (preflight, _) = await Call("OPTIONS", "/player/pause");
        var (put, _) = await Call("PUT", "/player/pause");

        Assert.Equal(HttpStatusCode.NoContent, preflight.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, put.StatusCode);
        Assert.Empty(_player.Calls);
    }

    [Fact]
    public void Only_the_player_prefix_is_the_players()
    {
        Assert.True(PlayerIntent.IsPlayerPath("/player"));
        Assert.True(PlayerIntent.IsPlayerPath("/player/x?y=1"));
        Assert.False(PlayerIntent.IsPlayerPath("/players"));
        Assert.False(PlayerIntent.IsPlayerPath("/overlay/off"));
        Assert.Equal(PlayerIntentKind.NotFound, PlayerIntent.Parse("DELETE", "/player/play"));
    }

    // ---- media keys ----

    [Fact]
    public void The_four_media_keys_are_recognized_once_per_press_and_ignore_auto_repeat()
    {
        var detector = new MediaKeyDetector();

        Assert.Equal(MediaKey.PlayPause, detector.Feed(0xB3, true));
        Assert.Null(detector.Feed(0xB3, true)); // held: repeat
        Assert.Null(detector.Feed(0xB3, false));
        Assert.Equal(MediaKey.PlayPause, detector.Feed(0xB3, true));
        Assert.Equal(MediaKey.Next, detector.Feed(0xB0, true));
        Assert.Equal(MediaKey.Previous, detector.Feed(0xB1, true));
        Assert.Equal(MediaKey.Stop, detector.Feed(0xB2, true));
        Assert.Null(detector.Feed(0x41, true)); // "A"
        Assert.Null(detector.Feed(0x11, true)); // Ctrl
    }

    [Fact]
    public void Play_pause_pauses_what_is_playing_and_plays_otherwise_even_during_a_fade_out()
    {
        _player.State = PlayerState.Playing;
        MediaKeyRouting.Execute(MediaKey.PlayPause, _player);

        _player.State = PlayerState.Paused;
        MediaKeyRouting.Execute(MediaKey.PlayPause, _player);

        _player.State = PlayerState.Playing;
        _player.Fading = true; // a pause is still fading out: the key brings the music back
        MediaKeyRouting.Execute(MediaKey.PlayPause, _player);

        MediaKeyRouting.Execute(MediaKey.Next, _player);
        MediaKeyRouting.Execute(MediaKey.Previous, _player);
        MediaKeyRouting.Execute(MediaKey.Stop, _player);

        Assert.Equal(["pause", "play", "play", "next", "previous", "stop"], _player.Calls);
    }
}
