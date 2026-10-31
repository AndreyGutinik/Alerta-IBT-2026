using System.Net;
using System.Net.Sockets;
using AlertaIBT.Core.Holyrics;
using AlertaIBT.Core.Settings;
using AlertaIBT.Core.Tests.Services;
using AlertaIBT.Core.Tests.Settings;
using Microsoft.Extensions.Logging;

namespace AlertaIBT.Core.Tests.Holyrics;

// Every failure of a call to Holyrics says WHY (short, for the chip) and carries the facts (for the tooltip and the log), and never the token.
public sealed class HolyricsFailureReasonTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ibt-holyrics-why-" + Guid.NewGuid().ToString("N"));
    private readonly FakeHolyricsServer _server = new();
    private readonly FakeProtector _protector = new();
    private readonly CapturingLogger _log = new();
    private readonly SettingsService _settings;

    public HolyricsFailureReasonTests()
    {
        Directory.CreateDirectory(_dir);
        _settings = new SettingsService(new SettingsStore(Path.Combine(_dir, "settings.json")));
        Configure();
    }

    public void Dispose()
    {
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

    private static HolyricsCommand Next => new(HolyricsActionKind.SlideNext);

    private void Configure(string host = "127.0.0.1", int? port = null) =>
        _settings.Update(s =>
        {
            var withToken = s.WithHolyricsToken(FakeHolyricsServer.GoodToken, _protector);
            return withToken with { Holyrics = withToken.Holyrics with { Host = host, Port = port ?? _server.Port } };
        });

    private HolyricsClient NewClient(TimeSpan? timeout = null, TimeSpan? permissionTimeout = null, TimeSpan? waitingAfter = null, HttpMessageHandler? handler = null, TimeProvider? clock = null) =>
        new(_settings, _protector, _log, clock, timeout, TimeSpan.Zero, handler, permissionTimeout, waitingAfter);

    private static int ClosedPort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private void AssertNoToken(params string?[] texts)
    {
        var token = FakeHolyricsServer.GoodToken;
        foreach (var text in texts.Concat(_log.Entries.Select(e => e.Message)))
        {
            Assert.DoesNotContain(token, text ?? string.Empty, StringComparison.Ordinal);
            Assert.DoesNotContain("token=", text ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("?token", text ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task A_refused_connection_says_so_with_the_address_and_the_facts()
    {
        var port = ClosedPort();
        Configure(port: port);
        using var client = NewClient();

        var result = await client.ExecuteAsync(Next, TestContext.Current.CancellationToken);

        Assert.Equal(HolyricsOutcome.Unreachable, result.Outcome);
        Assert.Equal("sem resposta: conex\U000000E3o recusada em 127.0.0.1:" + port, result.Message);
        Assert.Contains("ActionNext", result.Detail, StringComparison.Ordinal);
        Assert.Contains("127.0.0.1:" + port, result.Detail, StringComparison.Ordinal);
        Assert.Contains("SocketException (ConnectionRefused)", result.Detail, StringComparison.Ordinal);
        Assert.Contains("tempo: ", result.Detail, StringComparison.Ordinal);
        Assert.Equal(HolyricsLink.Unreachable, client.Status.Link);
        Assert.Equal(result.Message, client.Status.Message);
        Assert.Equal(result.Detail, client.Status.Detail);
        Assert.Contains(_log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("conex\U000000E3o recusada", StringComparison.Ordinal) && e.Message.Contains("SlideNext", StringComparison.Ordinal));
        AssertNoToken(result.Message, result.Detail, client.Status.Message, client.Status.Detail);
    }

    [Fact]
    public async Task An_unknown_host_name_says_the_address_was_not_found()
    {
        Configure(host: "holyrics-que-nao-existe.invalid");
        using var client = NewClient(timeout: TimeSpan.FromSeconds(4));

        var result = await client.ExecuteAsync(Next, TestContext.Current.CancellationToken);

        Assert.Equal(HolyricsOutcome.Unreachable, result.Outcome);
        Assert.StartsWith("sem resposta: ", result.Message, StringComparison.Ordinal);
        Assert.Contains("endere\U000000E7o n\U000000E3o encontrado", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_slow_answer_says_how_long_the_app_waited()
    {
        _server.Replies["ActionNext"] = new HolyricsReply(200, "{\"status\":\"ok\"}", DelayMs: 3000);
        using var client = NewClient(timeout: TimeSpan.FromMilliseconds(300));

        var result = await client.ExecuteAsync(Next, TestContext.Current.CancellationToken);

        Assert.Equal("sem resposta: demorou mais de 0.3 s", result.Message);
        Assert.Contains("CanceledException", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_permission_check_that_is_never_answered_blames_the_approval_window()
    {
        _server.Replies["CheckPermissions"] = new HolyricsReply(200, "{\"status\":\"ok\"}", DelayMs: 3000);
        using var client = NewClient(permissionTimeout: TimeSpan.FromMilliseconds(400), waitingAfter: TimeSpan.FromMilliseconds(100));

        var result = await client.TestAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HolyricsLink.Unreachable, result.Link);
        Assert.StartsWith("sem resposta: demorou mais de 0.4 s", result.Message, StringComparison.Ordinal);
        Assert.Contains("aprovar", result.Message, StringComparison.Ordinal);
        Assert.Contains("CheckPermissions", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_slow_permission_check_shows_waiting_then_connected_when_the_owner_approves()
    {
        _server.Replies["CheckPermissions"] = new HolyricsReply(200, "{\"status\":\"ok\"}", DelayMs: 900);
        using var client = NewClient(permissionTimeout: TimeSpan.FromSeconds(10), waitingAfter: TimeSpan.FromMilliseconds(150));
        var seen = new List<HolyricsStatus>();
        client.StatusChanged += () =>
        {
            lock (seen)
            {
                seen.Add(client.Status);
            }
        };

        var result = await client.TestAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HolyricsLink.Connected, result.Link);
        lock (seen)
        {
            Assert.Contains(seen, s => s.Link == HolyricsLink.Waiting && s.Message == "aguardando voc\U000000EA aprovar no Holyrics");
        }

        Assert.Equal(HolyricsLink.Connected, client.Status.Link);
        Assert.Equal("conectado", client.Status.Message);
    }

    [Fact]
    public async Task The_permission_answers_pending_and_denied_name_the_actions()
    {
        _server.Replies["CheckPermissions"] = new HolyricsReply(401, "{\"status\":\"error\",\"error\":{\"unauthorized_actions\":\"SetLogo,ActionNext\",\"request_status\":\"pending\"}}");
        using var client = NewClient();

        var pending = await client.TestAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HolyricsLink.Unauthorized, pending.Link);
        Assert.Equal("permiss\U000000E3o pendente no Holyrics para SetLogo, ActionNext", pending.Message);

        _server.Replies["CheckPermissions"] = new HolyricsReply(401, "{\"status\":\"error\",\"error\":{\"unauthorized_actions\":\"SetLogo\",\"request_status\":\"denied\"}}");
        var denied = await client.TestAsync(TestContext.Current.CancellationToken);

        Assert.Equal("permiss\U000000E3o negada no Holyrics para SetLogo", denied.Message);
    }

    [Fact]
    public async Task A_rejected_token_says_http_401()
    {
        _server.AcceptedToken = "another-token-1234";
        using var client = NewClient();

        var result = await client.ExecuteAsync(Next, TestContext.Current.CancellationToken);

        Assert.Equal("token n\U000000E3o aceito (HTTP 401)", result.Message);
        Assert.Contains("HTTP: 401", result.Detail, StringComparison.Ordinal);
        Assert.Equal(HolyricsLink.Unauthorized, client.Status.Link);
        AssertNoToken(result.Message, result.Detail);
    }

    [Fact]
    public async Task An_unexpected_http_status_says_so()
    {
        _server.Replies["ActionNext"] = new HolyricsReply(500, "Internal Server Error");
        using var client = NewClient();

        var result = await client.ExecuteAsync(Next, TestContext.Current.CancellationToken);

        Assert.Equal(HolyricsOutcome.Error, result.Outcome);
        Assert.Equal("resposta inesperada (HTTP 500)", result.Message);
        Assert.Contains("HTTP: 500", result.Detail, StringComparison.Ordinal);
        Assert.Equal(HolyricsLink.Error, client.Status.Link);
        Assert.Equal("resposta inesperada (HTTP 500)", client.Status.Message);
    }

    [Fact]
    public async Task An_error_from_the_api_shows_its_own_message_and_the_body_in_the_detail()
    {
        _server.Replies["ActionNext"] = HolyricsReply.Error("slide locked");
        using var client = NewClient();

        var result = await client.ExecuteAsync(Next, TestContext.Current.CancellationToken);

        Assert.Equal("erro do Holyrics: slide locked", result.Message);
        Assert.Contains("resposta: {\"status\":\"error\",\"error\":\"slide locked\"}", result.Detail, StringComparison.Ordinal);
        Assert.Equal(HolyricsLink.Error, client.Status.Link);
    }

    [Fact]
    public async Task An_error_without_a_message_is_still_explained()
    {
        _server.Replies["ActionNext"] = new HolyricsReply(200, "{\"status\":\"error\"}");
        using var client = NewClient();

        var result = await client.ExecuteAsync(Next, TestContext.Current.CancellationToken);

        Assert.Equal("erro do Holyrics (HTTP 200, sem mensagem)", result.Message);
    }

    [Fact]
    public async Task Malformed_json_and_json_without_a_status_are_reported_not_thrown()
    {
        _server.Replies["ActionNext"] = new HolyricsReply(200, "{not json");
        _server.Replies["ActionPrevious"] = new HolyricsReply(200, "{\"hello\":1}");
        using var client = NewClient();

        var broken = await client.ExecuteAsync(Next, TestContext.Current.CancellationToken);
        var odd = await client.ExecuteAsync(new HolyricsCommand(HolyricsActionKind.SlidePrevious), TestContext.Current.CancellationToken);

        Assert.Contains("n\U000000E3o \U000000E9 do Holyrics", broken.Message, StringComparison.Ordinal);
        Assert.Equal("resposta inesperada (HTTP 200)", odd.Message);
    }

    [Fact]
    public async Task Exception_texts_that_carry_the_url_or_the_token_are_sanitized_everywhere()
    {
        var token = FakeHolyricsServer.GoodToken;
        var inner = new SocketException((int)SocketError.ConnectionRefused);
        var boom = new HttpRequestException(
            "Failed http://127.0.0.1:8091/api/ActionNext?token=" + token + "&x=1 and again " + Uri.EscapeDataString(token) + " " + new string('a', 40),
            inner);
        using var client = NewClient(handler: new ThrowingHandler(boom));

        var result = await client.ExecuteAsync(Next, TestContext.Current.CancellationToken);
        var test = await client.TestAsync(TestContext.Current.CancellationToken);

        Assert.Contains("***", result.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('a', 40), result.Detail, StringComparison.Ordinal);
        AssertNoToken(result.Message, result.Detail, test.Message, test.Detail, client.Status.Message, client.Status.Detail);
        Assert.NotEmpty(_log.Entries);
    }

    [Fact]
    public async Task The_same_failure_within_a_few_seconds_is_logged_once_and_the_next_one_counts_the_repeats()
    {
        Configure(port: ClosedPort());
        var clock = new StepClock();
        using var client = NewClient(clock: clock);

        for (var i = 0; i < 4; i++)
        {
            await client.ExecuteAsync(Next, TestContext.Current.CancellationToken);
        }

        Assert.Single(_log.Entries, e => e.Level == LogLevel.Warning);

        clock.Ticks += 6000; // 6 s
        await client.ExecuteAsync(Next, TestContext.Current.CancellationToken);

        var warnings = _log.Entries.Where(e => e.Level == LogLevel.Warning).ToList();
        Assert.Equal(2, warnings.Count);
        Assert.Contains("(repetido 3 vezes)", warnings[1].Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_different_failure_is_logged_at_once()
    {
        Configure(port: ClosedPort());
        using var client = NewClient(clock: new StepClock());

        await client.ExecuteAsync(Next, TestContext.Current.CancellationToken);
        await client.ExecuteAsync(new HolyricsCommand(HolyricsActionKind.SlidePrevious), TestContext.Current.CancellationToken);

        Assert.Equal(2, _log.Entries.Count(e => e.Level == LogLevel.Warning));
    }

    [Fact]
    public async Task A_proxy_in_the_environment_does_not_stand_between_the_app_and_holyrics()
    {
        var old = Environment.GetEnvironmentVariable("HTTP_PROXY");
        Environment.SetEnvironmentVariable("HTTP_PROXY", "http://127.0.0.1:9");
        try
        {
            using var client = NewClient(); // the default handler: UseProxy is off
            var result = await client.ExecuteAsync(Next, TestContext.Current.CancellationToken);
            Assert.True(result.Success);
        }
        finally
        {
            Environment.SetEnvironmentVariable("HTTP_PROXY", old);
        }
    }

    [Fact]
    public async Task The_request_goes_out_as_plain_application_json_without_a_charset_because_the_real_holyrics_answers_415_otherwise()
    {
        using var client = NewClient();

        var result = await client.ExecuteAsync(Next, TestContext.Current.CancellationToken);
        var test = await client.TestAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Message);
        Assert.Equal(HolyricsLink.Connected, test.Link);
        Assert.All(_server.Requests, r => Assert.Equal("application/json", r.ContentType));
    }

    [Fact]
    public async Task A_415_says_the_format_of_the_request_was_refused()
    {
        using var client = NewClient(handler: new StatusHandler(HttpStatusCode.UnsupportedMediaType, "{}"));

        var result = await client.ExecuteAsync(Next, TestContext.Current.CancellationToken);

        Assert.Equal(HolyricsOutcome.Error, result.Outcome);
        Assert.Contains("HTTP 415", result.Message, StringComparison.Ordinal);
        Assert.Contains("resposta: {}", result.Detail, StringComparison.Ordinal);
    }

    private sealed class StatusHandler(HttpStatusCode code, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent(body) });
    }

    private sealed class StepClock : TimeProvider
    {
        public long Ticks { get; set; }

        public override long TimestampFrequency => 1000;

        public override long GetTimestamp() => Ticks;
    }

    private sealed class ThrowingHandler(Exception error) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => throw error;
    }
}
