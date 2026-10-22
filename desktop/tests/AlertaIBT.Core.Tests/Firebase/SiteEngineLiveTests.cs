using System.Net;
using System.Text.Json.Nodes;
using AlertaIBT.Core.Alerts;
using AlertaIBT.Core.Firebase;
using AlertaIBT.Core.Queue;
using AlertaIBT.Core.Settings;
using AlertaIBT.Core.Tests.Services;

namespace AlertaIBT.Core.Tests.Firebase;

// /live, the heartbeat, the start of a session, the cleaning of old requests and the states of the connection.
public sealed class SiteEngineLiveTests
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    private static int LivePuts(SiteRig rig) => rig.Fake.Writes("live").Count(c => c.Method == "PUT");

    private static int Heartbeats(SiteRig rig) => rig.Fake.Writes("live").Count(c => c.Method == "PATCH");

    [Fact]
    public async Task At_the_start_live_is_published_and_the_stale_showing_of_the_last_session_is_cleared()
    {
        await using var rig = new SiteRig();
        rig.Fake.Set("live", new JsonObject
        {
            ["heartbeatAt"] = 1,
            ["led"] = false,
            ["now"] = new JsonObject { ["id"] = "old", ["text"] = "OLD ALERT", ["source"] = "pc", ["startedAt"] = 1 },
        });

        await rig.StartOnlineAsync();
        await rig.WaitFor(() => rig.Live?["version"] is not null, "live published");

        var live = rig.Live!;
        Assert.Null(live["now"]);
        Assert.True(live["led"]!.GetValue<bool>());
        Assert.False(live["paused"]!.GetValue<bool>());
        Assert.Equal("8.0.0-test", live["version"]!.GetValue<string>());
        Assert.Equal("timestamp", live["heartbeatAt"]![".sv"]!.GetValue<string>());
        Assert.Equal("unknown", live["gemini"]!.GetValue<string>());
        var bar = live["bar"]!;
        Assert.Equal("top", bar["position"]!.GetValue<string>());
        Assert.Equal(56, bar["height"]!.GetValue<int>());
        Assert.Equal(90, bar["speed"]!.GetValue<int>());
        Assert.Null(bar["fontSize"]);
    }

    [Fact]
    public async Task Live_shows_the_current_alert_the_next_ones_and_the_recent_ones()
    {
        await using var rig = new SiteRig();
        rig.H.Settings = new AppSettings { Bar = new BarSettings { Position = "bottom", Height = 80, FontSize = 40, Speed = 120 } };
        await rig.StartOnlineAsync();
        _ = await rig.H.Pc("fiat uno azul pcp1a11");
        var first = await rig.H.Display.NextPass(1);

        rig.Send("r1", SiteRig.Good);
        rig.Send("r2", "hilux preta xyz9k88", urgent: false);
        await rig.WaitState("r2", "queued");
        await rig.WaitFor(() => (rig.Live?["queue"] as JsonArray)?.Count == 2, "queue in live");

        var live = rig.Live!;
        Assert.Contains("PCP-1A11", live["now"]!["text"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal("pc", live["now"]!["source"]!.GetValue<string>());
        Assert.Equal("PCP-1A11", live["now"]!["plate"]!.GetValue<string>());
        Assert.True(live["now"]!["startedAt"]!.GetValue<long>() > 0);
        var queue = live["queue"]!.AsArray();
        Assert.Equal("r1", queue[0]!["id"]!.GetValue<string>());
        Assert.Equal("site", queue[0]!["source"]!.GetValue<string>());
        Assert.Equal("ABC-1D23", queue[0]!["plate"]!.GetValue<string>());
        Assert.Equal("r2", queue[1]!["id"]!.GetValue<string>());
        Assert.Equal("bottom", live["bar"]!["position"]!.GetValue<string>());
        Assert.Equal(40, live["bar"]!["fontSize"]!.GetValue<int>());

        first.Complete(true);
        await rig.WaitFor(() => (rig.Live?["recent"] as JsonArray)?.Count == 1, "recent in live");

        var recent = rig.Live!["recent"]!.AsArray()[0]!;
        Assert.Equal("shown", recent["state"]!.GetValue<string>());
        Assert.Equal("PCP-1A11", recent["plate"]!.GetValue<string>());
        Assert.Equal("r1", rig.Live!["now"]!["id"]!.GetValue<string>());
    }

    [Fact]
    public async Task Live_follows_the_pause_the_led_and_urgent_notices()
    {
        await using var rig = new SiteRig(ledOn: false);
        await rig.StartOnlineAsync();
        await rig.WaitFor(() => rig.Live?["led"] is not null, "live published");
        Assert.False(rig.Live!["led"]!.GetValue<bool>());

        rig.H.Display.SetLed(true);
        await rig.WaitFor(() => rig.Live?["led"]?.GetValue<bool>() == true, "led on in live");
        rig.H.Service.Pause();
        await rig.WaitFor(() => rig.Live?["paused"]?.GetValue<bool>() == true, "paused in live");
        rig.H.Service.Resume();
        await rig.WaitFor(() => rig.Live?["paused"]?.GetValue<bool>() == false, "resumed in live");
        rig.H.Display.SetLed(false);
        _ = await rig.H.Pc("fiat uno azul pcp1a11", urgent: true);
        await rig.WaitFor(() => (rig.Live?["queue"] as JsonArray)?.Count == 1, "queue in live");

        Assert.True(rig.Live!["queue"]!.AsArray()[0]!["urgent"]!.GetValue<bool>());
    }

    [Fact]
    public async Task A_heartbeat_is_written_every_thirty_seconds_with_the_server_time()
    {
        await using var rig = new SiteRig();
        await rig.StartOnlineAsync();
        await rig.WaitFor(() => LivePuts(rig) >= 1, "first live");
        var before = Heartbeats(rig);

        await rig.AdvanceAsync(TimeSpan.FromSeconds(31));
        await rig.WaitFor(() => Heartbeats(rig) >= before + 1, "one heartbeat");
        await rig.AdvanceAsync(TimeSpan.FromSeconds(30));
        await rig.WaitFor(() => Heartbeats(rig) >= before + 2, "second heartbeat");

        var beat = rig.Fake.Writes("live").Last(c => c.Method == "PATCH");
        Assert.Equal("timestamp", beat.Body!["heartbeatAt"]![".sv"]!.GetValue<string>());
        Assert.Single(beat.Body.AsObject());
    }

    [Fact]
    public async Task A_burst_of_changes_is_written_to_live_once()
    {
        await using var rig = new SiteRig(ledOn: false, debounce: TimeSpan.FromMilliseconds(250));
        await rig.StartOnlineAsync();
        await rig.AdvanceAsync(TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(50));
        await rig.WaitFor(() => LivePuts(rig) >= 1, "first live");
        var before = LivePuts(rig);

        for (var i = 0; i < 5; i++)
        {
            _ = await rig.H.Pc("corolla prata abc1d2" + i);
        }

        await Task.Delay(100, Ct);
        Assert.Equal(before, LivePuts(rig));
        await rig.AdvanceAsync(TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(50));

        await rig.WaitFor(() => LivePuts(rig) == before + 1, "one write for the burst");
        await Task.Delay(100, Ct);
        Assert.Equal(before + 1, LivePuts(rig));
        Assert.Equal(5, rig.Live!["queue"]!.AsArray().Count);
    }

    [Fact]
    public async Task A_change_that_does_not_alter_live_is_not_written_again()
    {
        await using var rig = new SiteRig();
        await rig.StartOnlineAsync();
        await rig.WaitFor(() => LivePuts(rig) >= 1, "first live");
        await Task.Delay(100, Ct);
        var before = LivePuts(rig);

        // A reconnection brings a new snapshot of the requests but changes nothing the status tab shows.
        rig.Fake.LastStream.Close();
        await rig.AdvanceAsync(TimeSpan.FromSeconds(3));
        await rig.WaitFor(() => rig.Fake.Streams.Length == 2, "reopened");

        Assert.Equal(before, LivePuts(rig));
    }

    [Fact]
    public async Task What_the_last_session_left_half_way_is_picked_up_or_expired()
    {
        await using var rig = new SiteRig(ledOn: false);
        var now = rig.Clock.GetUtcNow();

        // r9 is still in the saved queue; r8 is not anywhere (the queue file was lost); r7 already ended (it is in the history).
        rig.H.Queue.Enqueue(new QueueItem("r9", "FIAT UNO AZUL | PLACA: PCP-1A11", "PCP-1A11", AlertSource.Site, false, now, SiteRig.Member, "x"), DuplicatePolicy.Warn);
        rig.H.Queue.AddRejected(new QueueItem("r7", "SEVEN", null, AlertSource.Site, false, now, SiteRig.Member, "x"), "faltou placa");
        foreach (var (id, state) in new[] { ("r9", "queued"), ("r8", "showing"), ("r7", "queued") })
        {
            rig.Send(id, "whatever", tweak: r => r["status"] = new JsonObject { ["state"] = state, ["position"] = 5, ["at"] = 1 });
        }

        await rig.StartOnlineAsync();

        await rig.WaitState("r8", "expired");
        await rig.WaitState("r7", "rejected");
        await rig.WaitFor(() => rig.StatusOf("r9")?["position"]?.GetValue<int>() == 1, "r9 rewritten");
        Assert.Equal("queued", rig.StateOf("r9"));
        Assert.Equal("faltou placa", rig.ReasonOf("r7"));
        Assert.Single(rig.H.Queue.Pending);
    }

    [Fact]
    public async Task Requests_older_than_a_day_are_deleted_and_newer_ones_are_kept()
    {
        await using var rig = new SiteRig(ledOn: false);
        rig.Send("old", SiteRig.Good, age: TimeSpan.FromHours(25), tweak: r => r["status"] = new JsonObject { ["state"] = "shown", ["at"] = 1 });
        rig.Send("recent", SiteRig.Good, age: TimeSpan.FromHours(23), tweak: r => r["status"] = new JsonObject { ["state"] = "shown", ["at"] = 1 });

        await rig.StartOnlineAsync();

        await rig.WaitFor(() => rig.Fake.Get("requests/old") is null, "old request deleted");
        Assert.NotNull(rig.Fake.Get("requests/recent"));
        Assert.Contains(rig.Fake.Writes("requests/old"), c => c.Method == "DELETE");
    }

    [Fact]
    public async Task Old_requests_are_also_deleted_while_running_once_an_hour()
    {
        await using var rig = new SiteRig(ledOn: false);
        await rig.StartOnlineAsync();
        rig.Send("r1", SiteRig.Good, age: TimeSpan.FromHours(20), tweak: r => r["status"] = new JsonObject { ["state"] = "shown", ["at"] = 1 });
        await Task.Delay(100, Ct);

        await rig.AdvanceAsync(TimeSpan.FromHours(5), TimeSpan.FromSeconds(30));

        await rig.WaitFor(() => rig.Fake.Get("requests/r1") is null, "deleted after it turned a day old");
    }

    [Fact]
    public async Task The_state_goes_from_connecting_to_online()
    {
        await using var rig = new SiteRig();

        await rig.StartOnlineAsync();

        Assert.Equal([SiteState.Connecting, SiteState.Connecting, SiteState.Online], rig.States.Take(3).ToArray());
        Assert.Equal(1, rig.Fake.SignIns);
    }

    [Fact]
    public async Task A_wrong_password_is_an_auth_error_and_the_database_is_never_touched()
    {
        await using var rig = new SiteRig(password: "wrong-password");
        rig.Engine.Start();

        await rig.WaitFor(() => rig.States.Contains(SiteState.AuthError), "auth error");

        Assert.Empty(rig.Fake.Calls);
        Assert.Contains("senha", rig.LastDetail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_refused_login_is_tried_again_after_five_minutes()
    {
        await using var rig = new SiteRig(password: "wrong-password");
        rig.Engine.Start();
        await rig.WaitFor(() => rig.States.Contains(SiteState.AuthError), "auth error");
        rig.Fake.SignInFailure = null;
        var states = rig.States.Length;

        // Same wrong password: still refused, but it did try again.
        await rig.AdvanceAsync(SiteEngine.LoginRetry + TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30));

        await rig.WaitFor(() => rig.States.Length > states, "another attempt");
        Assert.Equal(SiteState.AuthError, rig.Engine.State);
    }

    [Fact]
    public async Task Without_a_network_the_state_is_reconnecting_then_offline_after_a_minute_and_online_again_when_it_returns()
    {
        await using var rig = new SiteRig();
        await rig.StartOnlineAsync();
        rig.Fake.NetworkDown = true;
        rig.Fake.LastStream.Close();

        await rig.WaitFor(() => rig.Engine.State == SiteState.Reconnecting, "reconnecting");
        await rig.AdvanceAsync(TimeSpan.FromSeconds(50), TimeSpan.FromSeconds(1));
        Assert.Equal(SiteState.Reconnecting, rig.Engine.State);
        await rig.AdvanceAsync(TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(1));
        await rig.WaitFor(() => rig.Engine.State == SiteState.Offline, "offline");

        // More failed attempts do not turn the dot yellow again.
        await rig.AdvanceAsync(TimeSpan.FromSeconds(70), TimeSpan.FromSeconds(1));
        Assert.Equal(SiteState.Offline, rig.Engine.State);
        rig.Fake.NetworkDown = false;
        await rig.AdvanceAsync(TimeSpan.FromSeconds(35), TimeSpan.FromSeconds(1));

        await rig.WaitFor(() => rig.Engine.State == SiteState.Online, "online again");
    }

    [Fact]
    public async Task A_database_that_refuses_the_reading_is_an_auth_error()
    {
        await using var rig = new SiteRig();
        rig.Fake.DbRule = (_, path) => path == "requests" ? HttpStatusCode.Unauthorized : null;
        rig.Engine.Start();

        await rig.WaitFor(() => rig.States.Contains(SiteState.AuthError), "auth error");

        Assert.Contains("/devices", rig.LastDetail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_sign_in_that_fails_for_lack_of_network_retries_with_backoff_and_goes_on_when_it_returns()
    {
        await using var rig = new SiteRig();
        rig.Fake.NetworkDown = true;
        rig.Engine.Start();
        await rig.WaitFor(() => rig.States.Contains(SiteState.Reconnecting), "reconnecting");

        rig.Fake.NetworkDown = false;
        await rig.AdvanceAsync(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(1));

        await rig.WaitFor(() => rig.Engine.State == SiteState.Online, "online");
    }

    [Fact]
    public async Task Stopping_ends_the_engine_and_nothing_is_written_afterwards()
    {
        await using var rig = new SiteRig();
        await rig.StartOnlineAsync();
        await rig.WaitFor(() => LivePuts(rig) >= 1, "first live");

        await rig.Engine.StopAsync();
        var calls = rig.Fake.Calls.Length;
        await rig.AdvanceAsync(TimeSpan.FromSeconds(90), TimeSpan.FromSeconds(10));
        rig.Send("r1", SiteRig.Good);
        await Task.Delay(100, Ct);

        Assert.Equal(calls, rig.Fake.Calls.Length);
        Assert.Null(rig.StateOf("r1"));
    }

    [Fact]
    public async Task A_failing_write_never_kills_the_engine()
    {
        await using var rig = new SiteRig(ledOn: false);
        await rig.StartOnlineAsync();
        rig.DenyWrites(_ => HttpStatusCode.ServiceUnavailable);

        rig.Send("r1", SiteRig.Good);
        await Task.Delay(150, Ct);
        rig.Fake.DbRule = null;
        await rig.AdvanceAsync(SiteEngine.HeartbeatInterval + TimeSpan.FromSeconds(1));
        rig.Send("r2", "hilux preta xyz9k88");

        await rig.WaitState("r2", "queued");
        await rig.WaitState("r1", "queued");
        Assert.Equal(SiteState.Online, rig.Engine.State);
    }
}
