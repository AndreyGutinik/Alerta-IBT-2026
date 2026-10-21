using System.Net;
using System.Net.Sockets;
using AlertaIBT.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace AlertaIBT.Core.Tests.Services;

public sealed class HttpControlServerTests : IDisposable
{
    private sealed class FakeOverlay : IOverlayControl
    {
        private int _pauses;
        private int _resumes;

        public bool IsPaused { get; private set; }

        public int Pauses => Volatile.Read(ref _pauses);

        public int Resumes => Volatile.Read(ref _resumes);

        public Exception? Throw { get; set; }

        public void Pause()
        {
            if (Throw is { } error)
            {
                throw error;
            }

            Interlocked.Increment(ref _pauses);
            IsPaused = true;
        }

        public void Resume()
        {
            Interlocked.Increment(ref _resumes);
            IsPaused = false;
        }
    }

    private readonly FakeOverlay _overlay = new();
    private readonly StatusHub _hub = new();
    private readonly CapturingLogger _log = new();
    private readonly FakeTimeProvider _clock = new();
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

    // A port the OS just handed out; never 8787 in a test.
    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private HttpControlServer Server(int? port = null)
    {
        var server = new HttpControlServer(_overlay, _hub, _log, port ?? _port, _clock);
        _servers.Add(server);
        return server;
    }

    private HttpControlServer Started()
    {
        var server = Server();
        Assert.True(server.Start());
        return server;
    }

    private Uri Url(string path, string host = "127.0.0.1") => new($"http://{host}:{_port}{path}");

    private async Task<(HttpResponseMessage Response, string Body)> Call(string method, string path, string host = "127.0.0.1")
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), Url(path, host));
        var response = await _http.SendAsync(request, TestContext.Current.CancellationToken);
        return (response, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    // ---- the routes, identical to the old app ----

    [Fact]
    public async Task Status_answers_the_exact_json_of_the_old_app()
    {
        Started();

        var (response, body) = await Call("GET", "/overlay/status");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("""{"success":true,"overlayEnabled":true}""", body);
        Assert.Equal("application/json; charset=utf-8", response.Content.Headers.ContentType?.ToString());
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task Off_pauses_on_resumes_and_status_follows_for_get_and_post()
    {
        Started();

        var off = await Call("GET", "/overlay/off");
        Assert.Equal("""{"success":true,"overlayEnabled":false}""", off.Body);
        Assert.True(_overlay.IsPaused);
        Assert.Equal("""{"success":true,"overlayEnabled":false}""", (await Call("GET", "/overlay/status")).Body);

        var on = await Call("POST", "/overlay/on");
        Assert.Equal("""{"success":true,"overlayEnabled":true}""", on.Body);
        Assert.False(_overlay.IsPaused);

        var postOff = await Call("POST", "/overlay/off/?x=1");
        Assert.Equal("""{"success":true,"overlayEnabled":false}""", postOff.Body);
        var getOn = await Call("GET", "/overlay/on/");
        Assert.Equal("""{"success":true,"overlayEnabled":true}""", getOn.Body);
        Assert.Equal(2, _overlay.Pauses);
        Assert.Equal(2, _overlay.Resumes);
    }

    [Theory]
    [InlineData("GET", "/overlay/pause", false, "fallback-off")]
    [InlineData("POST", "/overlay/disable", false, "fallback-off")]
    [InlineData("GET", "/overlay?cmd=off", false, "fallback-off")]
    [InlineData("GET", "/overlay/x/off", false, "fallback-off")]
    [InlineData("GET", "/overlay/resume", true, "fallback-on")]
    [InlineData("POST", "/overlay/enable", true, "fallback-on")]
    [InlineData("GET", "/overlay?state=on", true, "fallback-on")]
    public async Task The_fallback_understands_the_words_in_the_path_and_says_it_matched(string method, string path, bool enabled, string matched)
    {
        Started();
        if (enabled)
        {
            _overlay.Pause();
        }

        var (response, body) = await Call(method, path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal($$"""{"success":true,"overlayEnabled":{{(enabled ? "true" : "false")}},"matched":"{{matched}}"}""", body);
    }

    [Fact]
    public async Task Off_wins_when_a_fallback_url_says_both()
    {
        Started();

        var (_, body) = await Call("GET", "/overlay/on/off");

        Assert.Contains("fallback-off", body);
        Assert.True(_overlay.IsPaused);
    }

    [Theory]
    [InlineData("GET", "/")]
    [InlineData("GET", "/overlay")]
    [InlineData("GET", "/overlay/offline")]
    [InlineData("POST", "/overlay/status")]
    [InlineData("PUT", "/overlay/off")]
    [InlineData("DELETE", "/overlay/on")]
    [InlineData("GET", "/other/off")]
    public async Task Anything_else_is_a_404_with_what_was_received(string method, string path)
    {
        Started();

        var (response, body) = await Call(method, path);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal($$$"""{"success":false,"error":"not_found","received":{"method":"{{{method}}}","url":"{{{path}}}"}}""", body);
        Assert.False(_overlay.IsPaused);
        Assert.Equal(0, _overlay.Pauses + _overlay.Resumes);
    }

    // ---- CORS ----

    [Theory]
    [InlineData("/overlay/off")]
    [InlineData("/overlay/status")]
    [InlineData("/whatever/else")]
    [InlineData("/")]
    public async Task The_preflight_is_a_204_with_cors_headers_for_any_path_and_changes_nothing(string path)
    {
        Started();

        var (response, body) = await Call("OPTIONS", path);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(string.Empty, body);
        Assert.Equal("http://127.0.0.1", response.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Equal("GET, POST, OPTIONS", response.Headers.GetValues("Access-Control-Allow-Methods").Single());
        Assert.Equal("Content-Type", response.Headers.GetValues("Access-Control-Allow-Headers").Single());
        Assert.False(_overlay.IsPaused);
        Assert.Equal(0, _overlay.Pauses);
    }

    [Fact]
    public async Task Every_answer_carries_the_cors_headers()
    {
        Started();

        foreach (var path in new[] { "/overlay/status", "/overlay/on", "/nope" })
        {
            var (response, _) = await Call("GET", path);
            Assert.Equal("http://127.0.0.1", response.Headers.GetValues("Access-Control-Allow-Origin").Single());
            Assert.Equal("GET, POST, OPTIONS", response.Headers.GetValues("Access-Control-Allow-Methods").Single());
        }
    }

    [Fact]
    public async Task The_api_also_answers_when_called_through_localhost()
    {
        Started();

        var (response, body) = await Call("GET", "/overlay/off", host: "localhost");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("""{"success":true,"overlayEnabled":false}""", body);
    }

    // ---- never dies ----

    [Fact]
    public async Task A_failure_inside_a_handler_is_a_500_json_and_the_server_keeps_serving()
    {
        Started();
        _overlay.Throw = new InvalidOperationException("overlay exploded");

        var (response, body) = await Call("GET", "/overlay/off");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("""{"success":false,"error":"overlay exploded"}""", body);
        Assert.Contains("overlay exploded", _hub.LastError);
        Assert.Contains(_log.Entries, e => e.Level == LogLevel.Error);

        _overlay.Throw = null;
        Assert.Equal("""{"success":true,"overlayEnabled":false}""", (await Call("GET", "/overlay/off")).Body);
    }

    [Fact]
    public async Task Many_requests_at_once_are_all_answered()
    {
        Started();

        var calls = await Task.WhenAll(Enumerable.Range(0, 60).Select(i => Call("GET", i % 2 == 0 ? "/overlay/status" : "/overlay/off")));

        Assert.All(calls, c => Assert.Equal(HttpStatusCode.OK, c.Response.StatusCode));
    }

    [Fact]
    public void Only_the_loopback_address_is_allowed()
    {
        Assert.True(HttpControlServer.IsLocal(new IPEndPoint(IPAddress.Loopback, 5)));
        Assert.True(HttpControlServer.IsLocal(new IPEndPoint(IPAddress.IPv6Loopback, 5)));
        Assert.True(HttpControlServer.IsLocal(new IPEndPoint(IPAddress.Parse("::ffff:127.0.0.1"), 5)));
        Assert.False(HttpControlServer.IsLocal(new IPEndPoint(IPAddress.Parse("192.168.0.7"), 5)));
        Assert.False(HttpControlServer.IsLocal(new IPEndPoint(IPAddress.Parse("8.8.8.8"), 5)));
        Assert.False(HttpControlServer.IsLocal(new IPEndPoint(IPAddress.Any, 5)));
        Assert.False(HttpControlServer.IsLocal(null));
    }

    // ---- the port ----

    [Fact]
    public void Starting_reports_listening_and_starting_again_is_harmless()
    {
        var server = Started();

        Assert.True(server.IsListening);
        Assert.True(server.Start());
        Assert.Equal(HttpApiState.Listening, ReadHttpState());
        Assert.Equal($"127.0.0.1:{_port}", _hub.Http.Detail);
    }

    [Fact]
    public async Task A_busy_port_is_logged_once_with_the_old_app_hint_and_shown_as_busy_then_taken_when_it_frees()
    {
        var first = Started();
        var second = Server();

        Assert.False(second.Start());

        Assert.False(second.IsListening);
        Assert.Equal(StatusLight.Red, _hub.Http.Light);
        Assert.Contains("antigo", _hub.Http.Detail);
        Assert.Contains(_port.ToString(), _hub.Http.Detail);
        var errors = _log.Entries.Where(e => e.Level == LogLevel.Error).ToArray();
        Assert.Single(errors);
        Assert.Contains("antigo", errors[0].Message);

        _clock.Advance(HttpControlServer.RetryInterval);
        _clock.Advance(HttpControlServer.RetryInterval);
        Assert.Single(_log.Entries, e => e.Level == LogLevel.Error); // not logged again on every retry
        Assert.False(second.IsListening);

        first.Dispose();
        _clock.Advance(HttpControlServer.RetryInterval);

        Assert.True(second.IsListening);
        Assert.Equal(StatusLight.Green, _hub.Http.Light);
        Assert.Equal("""{"success":true,"overlayEnabled":true}""", (await Call("GET", "/overlay/status")).Body);
    }

    [Fact]
    public async Task Disposing_stops_the_listener_and_marks_the_api_stopped()
    {
        var server = Started();

        server.Dispose();

        Assert.False(server.IsListening);
        Assert.Equal(StatusLight.Gray, _hub.Http.Light);
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => Call("GET", "/overlay/status"));
        Assert.False(server.Start()); // a disposed server stays down
        server.Dispose();
    }

    [Fact]
    public void The_port_must_be_a_real_port()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new HttpControlServer(_overlay, _hub, _log, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new HttpControlServer(_overlay, _hub, _log, 70000));
        Assert.Equal(8787, HttpControlServer.DefaultPort);
    }

    private HttpApiState ReadHttpState() => _hub.Http.Light switch
    {
        StatusLight.Green => HttpApiState.Listening,
        StatusLight.Red => HttpApiState.PortBusy,
        _ => HttpApiState.Stopped,
    };
}
