using AlertaIBT.Core.Control;
using AlertaIBT.Core.Diagnostics;
using AlertaIBT.Core.Holyrics;
using AlertaIBT.Core.Settings;
using AlertaIBT.Core.Tests.Control;
using AlertaIBT.Core.Tests.Services;
using AlertaIBT.Core.Tests.Settings;

namespace AlertaIBT.Core.Tests.Holyrics;

// Everything the client says to Holyrics goes over a real socket to a fake of its API server.
public sealed class HolyricsClientTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ibt-holyrics-" + Guid.NewGuid().ToString("N"));
    private readonly FakeHolyricsServer _server = new();
    private readonly FakeProtector _protector = new();
    private readonly CapturingLogger _log = new();
    private readonly SettingsService _settings;
    private HolyricsClient? _client;

    public HolyricsClientTests()
    {
        Directory.CreateDirectory(_dir);
        _settings = new SettingsService(new SettingsStore(Path.Combine(_dir, "settings.json")));
        Configure();
    }

    public void Dispose()
    {
        _client?.Dispose();
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

    private HolyricsClient Client => _client ??= new HolyricsClient(_settings, _protector, _log, minInterval: TimeSpan.Zero);

    private void Configure(string host = "127.0.0.1", int? port = null, string? token = FakeHolyricsServer.GoodToken) =>
        _settings.Update(s =>
        {
            var withToken = s.WithHolyricsToken(token, _protector);
            return withToken with { Holyrics = withToken.Holyrics with { Host = host, Port = port ?? _server.Port } };
        });

    private static HolyricsCommand Cmd(HolyricsActionKind kind, bool on = false) => new(kind, on);

    private static async Task Until(Func<bool> condition, int milliseconds = 5000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (!condition() && DateTime.UtcNow < until)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Next_and_previous_post_an_empty_json_body_with_the_token_in_the_query()
    {
        var next = await Client.ExecuteAsync(Cmd(HolyricsActionKind.SlideNext), TestContext.Current.CancellationToken);
        var previous = await Client.ExecuteAsync(Cmd(HolyricsActionKind.SlidePrevious), TestContext.Current.CancellationToken);

        Assert.True(next.Success);
        Assert.True(previous.Success);
        Assert.Equal(["ActionNext", "ActionPrevious"], _server.Actions);
        Assert.All(_server.Requests, r =>
        {
            Assert.Equal("POST", r.Method);
            Assert.Equal(FakeHolyricsServer.GoodToken, r.Token);
            Assert.Equal("{}", r.Body);
            Assert.Equal("application/json", r.ContentType);
        });
    }

    [Fact]
    public async Task Video_play_pause_reads_the_player_then_plays_when_stopped_and_pauses_when_playing()
    {
        _server.Playing = false;
        await Client.ExecuteAsync(Cmd(HolyricsActionKind.VideoPlayPause), TestContext.Current.CancellationToken);
        await Client.ExecuteAsync(Cmd(HolyricsActionKind.VideoPlayPause), TestContext.Current.CancellationToken); // the fake is "playing" now

        Assert.Equal(["GetMediaPlayerInfo", "MediaPlayerAction", "GetMediaPlayerInfo", "MediaPlayerAction"], _server.Actions);
        Assert.Equal("{\"action\":\"play\"}", _server.Requests[1].Body);
        Assert.Equal("{\"action\":\"pause\"}", _server.Requests[3].Body);
    }

    [Fact]
    public async Task The_logo_toggle_flips_the_apps_own_state_and_sends_enable_and_hide_together()
    {
        Assert.False(Client.LogoShown);

        await Client.ExecuteAsync(Cmd(HolyricsActionKind.LogoToggle), TestContext.Current.CancellationToken);
        Assert.True(Client.LogoShown);
        await Client.ExecuteAsync(Cmd(HolyricsActionKind.LogoToggle), TestContext.Current.CancellationToken);
        Assert.False(Client.LogoShown);

        Assert.Equal(["SetLogo", "SetLogo"], _server.Actions);
        Assert.Equal("{\"enable\":true,\"hide\":false}", _server.Requests[0].Body);
        Assert.Equal("{\"enable\":false,\"hide\":true}", _server.Requests[1].Body);
    }

    [Fact]
    public async Task An_absolute_logo_state_is_sent_as_is_and_remembered_even_when_it_repeats()
    {
        await Client.ExecuteAsync(Cmd(HolyricsActionKind.LogoSet, true), TestContext.Current.CancellationToken);
        await Client.ExecuteAsync(Cmd(HolyricsActionKind.LogoSet, true), TestContext.Current.CancellationToken);

        Assert.True(Client.LogoShown);
        Assert.Equal(2, _server.Requests.Count);

        await Client.ExecuteAsync(Cmd(HolyricsActionKind.LogoSet, false), TestContext.Current.CancellationToken);
        Assert.False(Client.LogoShown);
    }

    [Fact]
    public async Task A_failed_logo_call_leaves_the_remembered_state_alone()
    {
        _server.Replies["SetLogo"] = HolyricsReply.Error("boom");

        var result = await Client.ExecuteAsync(Cmd(HolyricsActionKind.LogoToggle), TestContext.Current.CancellationToken);

        Assert.Equal(HolyricsOutcome.Error, result.Outcome);
        Assert.False(Client.LogoShown);
    }

    [Fact]
    public async Task No_presentation_open_is_not_a_connection_failure()
    {
        _server.Replies["ActionNext"] = HolyricsReply.Error("No presentation available");

        var result = await Client.ExecuteAsync(Cmd(HolyricsActionKind.SlideNext), TestContext.Current.CancellationToken);

        Assert.Equal(HolyricsOutcome.NoPresentation, result.Outcome);
        Assert.Equal(HolyricsLink.Connected, Client.Status.Link);
    }

    [Fact]
    public async Task A_wrong_token_is_reported_as_unauthorized()
    {
        _server.AcceptedToken = "another-token-1234";

        var result = await Client.ExecuteAsync(Cmd(HolyricsActionKind.SlideNext), TestContext.Current.CancellationToken);

        Assert.Equal(HolyricsOutcome.Unauthorized, result.Outcome);
        Assert.Equal(HolyricsLink.Unauthorized, Client.Status.Link);
    }

    [Fact]
    public async Task An_action_the_token_may_not_run_is_unauthorized_even_with_status_200()
    {
        _server.Replies["SetLogo"] = new HolyricsReply(200, "{\"status\":\"error\",\"error\":\"unauthorized action\"}");

        var result = await Client.ExecuteAsync(Cmd(HolyricsActionKind.LogoToggle), TestContext.Current.CancellationToken);

        Assert.Equal(HolyricsOutcome.Unauthorized, result.Outcome);
    }

    [Fact]
    public async Task Nothing_listening_is_reported_as_unreachable()
    {
        Configure(port: FreePortNobodyListensOn());

        var result = await Client.ExecuteAsync(Cmd(HolyricsActionKind.SlideNext), TestContext.Current.CancellationToken);

        Assert.Equal(HolyricsOutcome.Unreachable, result.Outcome);
        Assert.Equal(HolyricsLink.Unreachable, Client.Status.Link);
    }

    [Fact]
    public async Task A_server_that_is_too_slow_times_out_instead_of_hanging()
    {
        _server.Replies["ActionNext"] = new HolyricsReply(200, "{\"status\":\"ok\"}", DelayMs: 3000);
        using var client = new HolyricsClient(_settings, _protector, _log, timeout: TimeSpan.FromMilliseconds(300), minInterval: TimeSpan.Zero);

        var started = DateTime.UtcNow;
        var result = await client.ExecuteAsync(Cmd(HolyricsActionKind.SlideNext), TestContext.Current.CancellationToken);

        Assert.Equal(HolyricsOutcome.Unreachable, result.Outcome);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Something_that_is_not_holyrics_on_that_port_is_an_error_not_a_crash()
    {
        _server.Replies["ActionNext"] = new HolyricsReply(200, "<html>hello</html>");

        var result = await Client.ExecuteAsync(Cmd(HolyricsActionKind.SlideNext), TestContext.Current.CancellationToken);

        Assert.Equal(HolyricsOutcome.Error, result.Outcome);
    }

    [Fact]
    public void Without_a_token_nothing_is_configured_and_nothing_is_sent()
    {
        Configure(token: null);

        Assert.False(Client.IsConfigured);
        Assert.False(Client.ArrowsActive);
        Assert.False(Client.Post(Cmd(HolyricsActionKind.SlideNext)));
        Thread.Sleep(100);
        Assert.Empty(_server.Requests);
    }

    [Fact]
    public void The_arrows_are_active_only_with_the_switch_on_and_the_connection_set_up()
    {
        Assert.True(Client.ArrowsActive);

        _settings.Update(s => s with { Holyrics = s.Holyrics with { ArrowsEnabled = false } });
        Assert.False(Client.ArrowsActive);
        Assert.True(Client.IsConfigured);
    }

    [Fact]
    public async Task Post_returns_at_once_and_the_request_goes_out_in_the_background()
    {
        _server.Replies["ActionNext"] = new HolyricsReply(200, "{\"status\":\"ok\"}", DelayMs: 400);

        var started = DateTime.UtcNow;
        Assert.True(Client.Post(Cmd(HolyricsActionKind.SlideNext)));
        var returned = DateTime.UtcNow - started;

        Assert.True(returned < TimeSpan.FromMilliseconds(200), "Post must not wait for Holyrics");
        await Until(() => _server.Requests.Count == 1);
        Assert.Equal(["ActionNext"], _server.Actions);
    }

    [Fact]
    public async Task A_held_button_does_not_flood_holyrics()
    {
        _server.Replies["ActionNext"] = new HolyricsReply(200, "{\"status\":\"ok\"}", DelayMs: 150);

        for (var i = 0; i < 60; i++)
        {
            Client.Post(Cmd(HolyricsActionKind.SlideNext));
        }

        await Task.Delay(800, TestContext.Current.CancellationToken);
        Assert.InRange(_server.Requests.Count, 1, 2); // one in flight and one waiting; the rest were dropped
    }

    [Fact]
    public async Task The_same_action_again_within_the_debounce_window_is_dropped_but_another_action_is_not()
    {
        using var client = new HolyricsClient(_settings, _protector, _log, minInterval: TimeSpan.FromSeconds(5));

        client.Post(Cmd(HolyricsActionKind.SlideNext));
        await Until(() => _server.Requests.Count == 1);
        client.Post(Cmd(HolyricsActionKind.SlideNext)); // too soon
        client.Post(Cmd(HolyricsActionKind.SlidePrevious)); // another action: goes
        await Until(() => _server.Requests.Count == 2);
        await Task.Delay(150, TestContext.Current.CancellationToken);

        Assert.Equal(["ActionNext", "ActionPrevious"], _server.Actions);
    }

    [Fact]
    public async Task Commands_go_one_after_another_in_the_order_they_were_given()
    {
        for (var i = 0; i < 3; i++)
        {
            Client.Post(Cmd(HolyricsActionKind.SlideNext));
            Client.Post(Cmd(HolyricsActionKind.SlidePrevious));
            await Until(() => _server.Requests.Count == (i + 1) * 2);
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        Assert.Equal(["ActionNext", "ActionPrevious", "ActionNext", "ActionPrevious", "ActionNext", "ActionPrevious"], _server.Actions);
    }

    [Fact]
    public async Task The_newest_logo_state_waiting_replaces_the_older_one()
    {
        _server.Replies["SetLogo"] = new HolyricsReply(200, "{\"status\":\"ok\"}", DelayMs: 200);

        Client.Post(Cmd(HolyricsActionKind.LogoSet, true)); // in flight
        await Until(() => _server.Requests.Count == 1);
        Client.Post(Cmd(HolyricsActionKind.LogoSet, false));
        Client.Post(Cmd(HolyricsActionKind.LogoSet, true));
        Client.Post(Cmd(HolyricsActionKind.LogoSet, false)); // the wish that counts
        await Until(() => _server.Requests.Count == 2 && !Client.LogoShown);
        await Task.Delay(300, TestContext.Current.CancellationToken);

        Assert.Equal(2, _server.Requests.Count);
        Assert.Equal("{\"enable\":false,\"hide\":true}", _server.Requests[1].Body);
        Assert.False(Client.LogoShown);
    }

    [Fact]
    public async Task Test_says_connected_and_whether_the_video_plays()
    {
        _server.Playing = true;

        var result = await Client.TestAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HolyricsLink.Connected, result.Link);
        Assert.Equal("conectado", result.Message);
        Assert.True(result.Playing);
        Assert.Equal(["CheckPermissions", "GetMediaPlayerInfo"], _server.Actions);
        Assert.Equal("{\"actions\":\"" + HolyricsClient.RequiredActions + "\"}", _server.Requests[0].Body);
        Assert.Equal(HolyricsLink.Connected, Client.Status.Link);
    }

    [Fact]
    public async Task Test_lists_the_actions_the_token_may_not_run()
    {
        _server.Replies["CheckPermissions"] = new HolyricsReply(401, "{\"status\":\"error\",\"error\":{\"unauthorized_actions\":\"SetLogo,MediaPlayerAction\",\"request_status\":\"pending\"}}");

        var result = await Client.TestAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HolyricsLink.Unauthorized, result.Link);
        Assert.Equal("permiss\U000000E3o pendente no Holyrics para SetLogo, MediaPlayerAction", result.Message);
        Assert.Equal(["SetLogo", "MediaPlayerAction"], result.MissingPermissions);
    }

    [Fact]
    public async Task Test_with_a_token_holyrics_does_not_know_says_it_is_not_accepted()
    {
        _server.AcceptedToken = "something-else-9999";

        var result = await Client.TestAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HolyricsLink.Unauthorized, result.Link);
        Assert.Equal("token n\U000000E3o aceito (HTTP 401)", result.Message);
    }

    [Fact]
    public async Task Test_without_an_answer_says_so()
    {
        Configure(port: FreePortNobodyListensOn());

        var result = await Client.TestAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HolyricsLink.Unreachable, result.Link);
        Assert.StartsWith("sem resposta: conex\U000000E3o recusada em 127.0.0.1:", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Changing_the_token_or_the_address_forgets_the_old_verdict()
    {
        await Client.TestAsync(TestContext.Current.CancellationToken);
        Assert.Equal(HolyricsLink.Connected, Client.Status.Link);

        Configure(token: "a-new-token-5678");

        Assert.Equal(HolyricsLink.Unknown, Client.Status.Link);
    }

    [Fact]
    public async Task The_token_never_reaches_the_log_or_the_breadcrumbs_even_when_the_server_echoes_it()
    {
        var token = FakeHolyricsServer.GoodToken;
        _server.Replies["ActionNext"] = HolyricsReply.Error("bad request for token " + token);
        _server.Replies["ActionPrevious"] = new HolyricsReply(200, "{\"status\":\"error\",\"error\":\"" + Uri.EscapeDataString(token) + " is wrong\"}");

        await Client.ExecuteAsync(Cmd(HolyricsActionKind.SlideNext), TestContext.Current.CancellationToken);
        await Client.ExecuteAsync(Cmd(HolyricsActionKind.SlidePrevious), TestContext.Current.CancellationToken);
        Configure(port: FreePortNobodyListensOn());
        await Client.ExecuteAsync(Cmd(HolyricsActionKind.SlideNext), TestContext.Current.CancellationToken);
        await Client.TestAsync(TestContext.Current.CancellationToken);

        Assert.NotEmpty(_log.Entries);
        Assert.All(_log.Entries, entry => Assert.DoesNotContain(token, entry.Message, StringComparison.Ordinal));
        Assert.DoesNotContain(token, BreadcrumbTrail.Default.Format(DateTimeOffset.UtcNow), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_result_message_of_a_server_error_has_the_token_redacted()
    {
        var token = FakeHolyricsServer.GoodToken;
        _server.Replies["ActionNext"] = HolyricsReply.Error("nope " + token);

        var result = await Client.ExecuteAsync(Cmd(HolyricsActionKind.SlideNext), TestContext.Current.CancellationToken);

        Assert.Contains("***", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(token, result.Message, StringComparison.Ordinal);
    }

    private static int FreePortNobodyListensOn()
    {
        var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        var port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}
