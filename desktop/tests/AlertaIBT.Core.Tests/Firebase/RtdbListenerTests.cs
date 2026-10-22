using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using AlertaIBT.Core.Firebase;
using AlertaIBT.Core.Tests.Services;
using Microsoft.Extensions.Time.Testing;

namespace AlertaIBT.Core.Tests.Firebase;

public sealed class RtdbListenerTests : IAsyncDisposable
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 25, 9, 0, 0, TimeSpan.Zero));
    private readonly FakeFirebase _fake;
    private readonly FirebaseAuthClient _auth;
    private readonly RtdbListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private readonly List<(ListenerState State, string? Detail)> _states = [];
    private readonly List<RtdbChange> _changes = [];
    private Task? _run;

    public RtdbListenerTests()
    {
        _fake = new FakeFirebase(_clock);
        _auth = new FirebaseAuthClient(new HttpClient(_fake), FakeFirebase.ApiKey, clock: _clock);
        var db = new RealtimeDbClient(new HttpClient(_fake), "https://x-default-rtdb.firebaseio.com", _auth);
        _listener = new RtdbListener(db, "requests", _clock);
        _listener.StateChanged += (state, detail) =>
        {
            lock (_gate)
            {
                _states.Add((state, detail));
            }
        };
        _listener.Changed += change =>
        {
            lock (_gate)
            {
                _changes.Add(change);
            }
        };
    }

    private ListenerState[] States
    {
        get
        {
            lock (_gate)
            {
                return _states.Select(s => s.State).ToArray();
            }
        }
    }

    private RtdbChange[] Changes
    {
        get
        {
            lock (_gate)
            {
                return _changes.ToArray();
            }
        }
    }

    private async Task StartAsync()
    {
        await _auth.SignInAsync(FakeFirebase.Email, FakeFirebase.Password, Ct);
        _run = Task.Run(() => _listener.RunAsync(_stop.Token), Ct);
    }

    // Moves the fake clock forward a second at a time, letting the listener run in between.
    private async Task AdvanceAsync(int seconds, Action? each = null)
    {
        // First let the listener reach the wait it is about to make: a timer made after the clock moved would miss the step.
        await Task.Delay(40, Ct);
        for (var i = 0; i < seconds; i++)
        {
            _clock.Advance(TimeSpan.FromSeconds(1));
            each?.Invoke();
            await Task.Delay(3, Ct);
        }

        await Task.Delay(30, Ct);
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        if (_run is { } run)
        {
            await run.WaitAsync(TimeSpan.FromSeconds(5));
        }

        _stop.Dispose();
    }

    [Fact]
    public async Task The_first_data_connects_and_fills_the_copy()
    {
        _fake.Set("requests/a", new JsonObject { ["raw"] = "one" });
        _fake.Set("requests/b", new JsonObject { ["raw"] = "two" });

        await StartAsync();
        await Wait.Until(() => States.Contains(ListenerState.Connected), "connected");

        Assert.Equal([ListenerState.Connecting, ListenerState.Connected], States);
        Assert.Equal(["a", "b"], _listener.Snapshot().Keys.Order());
        var first = Changes[0];
        Assert.True(first.Reset);
        Assert.Equal(["a", "b"], first.Children.Keys.Order());
        Assert.Equal("one", _listener.GetChild("a")!["raw"]!.GetValue<string>());
    }

    [Fact]
    public async Task An_empty_database_still_connects()
    {
        await StartAsync();
        await Wait.Until(() => States.Contains(ListenerState.Connected), "connected");

        Assert.Empty(_listener.Snapshot());
        Assert.Null(_listener.GetChild("nope"));
    }

    [Fact]
    public async Task Put_and_patch_events_change_the_copy_at_their_paths()
    {
        await StartAsync();
        await Wait.Until(() => States.Contains(ListenerState.Connected), "connected");
        var stream = _fake.LastStream;

        stream.Put("/r1", new JsonObject { ["raw"] = "x", ["cancel"] = false });
        stream.Put("/r1/status", new JsonObject { ["state"] = "queued", ["at"] = 5 });
        stream.Patch("/r1", new JsonObject { ["cancel"] = true, ["urgent"] = true });
        stream.Put("/r2/deep/er", JsonValue.Create(7));
        await Wait.Until(() => _listener.GetChild("r2") is not null && _listener.GetChild("r1")?["urgent"] is not null, "events applied");

        var r1 = _listener.GetChild("r1")!;
        Assert.Equal("x", r1["raw"]!.GetValue<string>());
        Assert.True(r1["cancel"]!.GetValue<bool>());
        Assert.Equal("queued", r1["status"]!["state"]!.GetValue<string>());
        Assert.Equal(7, _listener.GetChild("r2")!["deep"]!["er"]!.GetValue<int>());
        Assert.DoesNotContain(Changes.Skip(1), c => c.Reset);
        Assert.Contains(Changes, c => c.Children.Keys.SequenceEqual(["r1"]));
    }

    [Fact]
    public async Task A_null_put_removes_the_node_and_empty_parents_go_with_it()
    {
        _fake.Set("requests/r1", new JsonObject { ["status"] = new JsonObject { ["state"] = "queued" } });
        await StartAsync();
        await Wait.Until(() => _listener.GetChild("r1") is not null, "first data");
        var stream = _fake.LastStream;

        stream.Put("/r1/status/state", null);
        await Wait.Until(() => _listener.GetChild("r1") is null, "r1 gone");

        Assert.Empty(_listener.Snapshot());
        Assert.Null(Changes[^1].Children["r1"]);
    }

    [Fact]
    public async Task A_patch_with_null_values_removes_those_children()
    {
        _fake.Set("requests/r1", new JsonObject { ["a"] = 1, ["b"] = 2 });
        await StartAsync();
        await Wait.Until(() => _listener.GetChild("r1") is not null, "first data");

        _fake.LastStream.Patch("/r1", new JsonObject { ["a"] = null, ["c"] = 3 });
        await Wait.Until(() => _listener.GetChild("r1")?["c"] is not null, "patched");

        var r1 = _listener.GetChild("r1")!.AsObject();
        Assert.Equal(["b", "c"], r1.Select(p => p.Key).Order());
    }

    [Fact]
    public async Task Values_with_accents_survive_a_multi_byte_letter_split_between_chunks()
    {
        await StartAsync();
        await Wait.Until(() => States.Contains(ListenerState.Connected), "connected");
        var bytes = Encoding.UTF8.GetBytes("event: put\ndata: {\"path\":\"/r1\",\"data\":{\"raw\":\"a\U000000E7\U000000E3o \U00002705\"}}\n\n");
        var cut = Array.IndexOf(bytes, (byte)0xC3) + 1;

        _fake.LastStream.Body.Push(bytes[..cut]);
        await Task.Delay(30, Ct);
        _fake.LastStream.Body.Push(bytes[cut..]);
        await Wait.Until(() => _listener.GetChild("r1") is not null, "event applied");

        Assert.Equal("a\U000000E7\U000000E3o \U00002705", _listener.GetChild("r1")!["raw"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_closed_stream_reconnects_after_one_second_and_sends_everything_again()
    {
        _fake.Set("requests/a", new JsonObject { ["raw"] = "one" });
        await StartAsync();
        await Wait.Until(() => States.Contains(ListenerState.Connected), "connected");

        _fake.Set("requests/b", new JsonObject { ["raw"] = "two" });
        _fake.LastStream.Close();
        await Wait.Until(() => States.Contains(ListenerState.Reconnecting), "reconnecting");
        Assert.Single(_fake.Streams);
        await AdvanceAsync(1);
        await Wait.Until(() => _fake.Streams.Length == 2 && States[^1] == ListenerState.Connected, "connected again");

        Assert.Equal(["a", "b"], _listener.Snapshot().Keys.Order());
        Assert.Equal(2, Changes.Count(c => c.Reset));
    }

    [Fact]
    public async Task A_reconnection_removes_what_vanished_while_it_was_away()
    {
        _fake.Set("requests/a", new JsonObject { ["raw"] = "one" });
        _fake.Set("requests/b", new JsonObject { ["raw"] = "two" });
        await StartAsync();
        await Wait.Until(() => _listener.Snapshot().Count == 2, "first data");
        var old = _fake.LastStream;

        old.Close();
        _fake.Set("requests/a", null);
        await Wait.Until(() => States.Contains(ListenerState.Reconnecting), "reconnecting");
        await AdvanceAsync(1);
        await Wait.Until(() => _listener.Snapshot().Count == 1, "second data");

        Assert.Equal(["b"], _listener.Snapshot().Keys);
        var reset = Changes.Last(c => c.Reset);
        Assert.Null(reset.Children["a"]);
        Assert.NotNull(reset.Children["b"]);
    }

    [Fact]
    public async Task The_wait_between_failed_attempts_grows_from_one_to_thirty_seconds()
    {
        _fake.DbRule = (_, _) => HttpStatusCode.ServiceUnavailable;
        await StartAsync();
        var seconds = 0;
        var seen = new List<int>();
        var known = 0;
        while (seen.Count < 8 && seconds < 200)
        {
            await Task.Delay(8, Ct);
            var calls = _fake.Calls.Where(c => c.Method == "GET").ToArray();
            while (known < calls.Length)
            {
                seen.Add(seconds);
                known++;
            }

            if (seen.Count >= 8)
            {
                break;
            }

            _clock.Advance(TimeSpan.FromSeconds(1));
            seconds++;
        }

        var gaps = seen.Zip(seen.Skip(1), (a, b) => b - a).ToArray();
        Assert.Equal(8, seen.Count);

        // 1, 2, 4, 8, 16, 30, 30 seconds (the step of the clock is one second, so a gap may be one more).
        int[] expected = [1, 2, 4, 8, 16, 30, 30];
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.InRange(gaps[i], expected[i], expected[i] + 1);
        }
    }

    [Fact]
    public async Task A_healthy_connection_resets_the_wait()
    {
        await StartAsync();
        await Wait.Until(() => States.Contains(ListenerState.Connected), "connected");

        // Two quick failures first would have grown the wait: here the connection was healthy, so the next wait is one second again.
        _fake.LastStream.Close();
        await Wait.Until(() => States[^1] == ListenerState.Reconnecting, "reconnecting 1");
        await AdvanceAsync(1);
        await Wait.Until(() => _fake.Streams.Length == 2 && States[^1] == ListenerState.Connected, "connected 2");
        _fake.LastStream.Close();
        await Wait.Until(() => States[^1] == ListenerState.Reconnecting, "reconnecting 2");
        await AdvanceAsync(1);

        await Wait.Until(() => _fake.Streams.Length == 3, "third connection after one second");
    }

    [Fact]
    public async Task Keep_alives_hold_a_quiet_connection_open_beyond_sixty_seconds()
    {
        await StartAsync();
        await Wait.Until(() => States.Contains(ListenerState.Connected), "connected");

        await AdvanceAsync(150, () =>
        {
            if (_clock.GetUtcNow().Second % 20 == 0)
            {
                _fake.LastStream.KeepAlive();
            }
        });

        Assert.Single(_fake.Streams);
        Assert.DoesNotContain(ListenerState.Reconnecting, States);
    }

    [Fact]
    public async Task A_connection_that_goes_silent_for_sixty_seconds_is_dropped_and_reopened()
    {
        await StartAsync();
        await Wait.Until(() => States.Contains(ListenerState.Connected), "connected");

        await AdvanceAsync(59);
        Assert.Single(_fake.Streams);
        await AdvanceAsync(3);
        await Wait.Until(() => States.Contains(ListenerState.Reconnecting), "reconnecting");
        await AdvanceAsync(2);

        await Wait.Until(() => _fake.Streams.Length == 2 && States[^1] == ListenerState.Connected, "reopened");
    }

    [Fact]
    public async Task Auth_revoked_reconnects_at_once_with_a_renewed_token()
    {
        await StartAsync();
        await Wait.Until(() => States.Contains(ListenerState.Connected), "connected");
        var oldToken = _fake.LastStream.Token;

        _fake.LastStream.Event("auth_revoked", "\"credential is no longer valid\"");
        await Wait.Until(() => _fake.Streams.Length == 2, "second connection (no waiting)");

        Assert.NotEqual(oldToken, _fake.LastStream.Token);
        Assert.Equal(1, _fake.Refreshes);
        await Wait.Until(() => States[^1] == ListenerState.Connected, "connected again");
        Assert.DoesNotContain(ListenerState.Reconnecting, States);
    }

    [Fact]
    public async Task A_stream_the_server_keeps_refusing_is_a_permission_problem_and_retries_every_thirty_seconds()
    {
        _fake.DbRule = (_, path) => path == "requests" ? HttpStatusCode.Unauthorized : null;
        await StartAsync();

        await Wait.Until(() => States.Contains(ListenerState.PermissionDenied), "permission denied");
        var attempts = _fake.Calls.Count(c => c.Method == "GET");
        await AdvanceAsync(29);
        Assert.Equal(attempts, _fake.Calls.Count(c => c.Method == "GET"));
        await AdvanceAsync(2);

        await Wait.Until(() => _fake.Calls.Count(c => c.Method == "GET") > attempts, "another attempt");
        Assert.DoesNotContain(ListenerState.Connected, States);
    }

    [Fact]
    public async Task A_cancel_before_any_data_means_the_rules_refuse_the_reading()
    {
        _fake.SilentStreams = true;
        await StartAsync();
        await Wait.Until(() => _fake.Streams.Length == 1, "stream open");

        _fake.LastStream.Event("cancel", "\"permission_denied at /requests: Client doesn't have permission to access the desired data.\"");

        await Wait.Until(() => States.Contains(ListenerState.PermissionDenied), "permission denied");
    }

    [Fact]
    public async Task The_connection_is_renewed_before_the_token_expires_without_a_visible_reconnect()
    {
        await StartAsync();
        await Wait.Until(() => States.Contains(ListenerState.Connected), "connected");
        var oldToken = _fake.LastStream.Token;

        // Token life 3600 s, renewed 300 s early: the connection is replaced at about 55 minutes.
        await AdvanceAsync(3299, () =>
        {
            if (_clock.GetUtcNow().Second % 30 == 0)
            {
                _fake.LastStream.KeepAlive();
            }
        });
        Assert.Single(_fake.Streams);
        await AdvanceAsync(3, () => _fake.LastStream.KeepAlive());

        await Wait.Until(() => _fake.Streams.Length == 2, "renewed connection");
        Assert.NotEqual(oldToken, _fake.LastStream.Token);
        Assert.Equal(1, _fake.Refreshes);
        Assert.DoesNotContain(ListenerState.Reconnecting, States);
    }

    [Fact]
    public async Task A_malformed_event_makes_it_reconnect_to_resynchronise()
    {
        await StartAsync();
        await Wait.Until(() => States.Contains(ListenerState.Connected), "connected");

        _fake.LastStream.Event("put", "{this is not json");
        await Wait.Until(() => States[^1] == ListenerState.Reconnecting, "reconnecting");
        await AdvanceAsync(1);

        await Wait.Until(() => _fake.Streams.Length == 2 && States[^1] == ListenerState.Connected, "connected again");
    }

    [Fact]
    public async Task Unknown_events_are_ignored()
    {
        await StartAsync();
        await Wait.Until(() => States.Contains(ListenerState.Connected), "connected");

        _fake.LastStream.Event("something-new", "{}");
        _fake.LastStream.Put("/r1", new JsonObject { ["raw"] = "x" });
        await Wait.Until(() => _listener.GetChild("r1") is not null, "later event applied");

        Assert.Single(_fake.Streams);
    }

    [Fact]
    public async Task A_307_on_the_stream_is_followed()
    {
        _fake.Set("requests/a", new JsonObject { ["raw"] = "one" });
        await _auth.SignInAsync(FakeFirebase.Email, FakeFirebase.Password, Ct);
        _fake.RedirectNextStreamTo = "https://s-shard-9.firebaseio.com/requests.json?auth=" + await _auth.GetTokenAsync(false, Ct);
        _run = Task.Run(() => _listener.RunAsync(_stop.Token), Ct);

        await Wait.Until(() => _listener.GetChild("a") is not null, "data through the redirect");

        Assert.Single(_fake.Streams);
    }

    [Fact]
    public async Task Cancelling_stops_the_listener()
    {
        await StartAsync();
        await Wait.Until(() => States.Contains(ListenerState.Connected), "connected");

        await _stop.CancelAsync();

        await _run!.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.True(_run.IsCompletedSuccessfully);
    }
}
