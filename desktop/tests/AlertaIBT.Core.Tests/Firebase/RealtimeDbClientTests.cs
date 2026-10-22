using System.Net;
using System.Text.Json.Nodes;
using AlertaIBT.Core.Firebase;
using Microsoft.Extensions.Time.Testing;

namespace AlertaIBT.Core.Tests.Firebase;

public sealed class RealtimeDbClientTests
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 25, 9, 0, 0, TimeSpan.Zero));
    private readonly FakeFirebase _fake;
    private readonly FirebaseAuthClient _auth;

    public RealtimeDbClientTests()
    {
        _fake = new FakeFirebase(_clock);
        _auth = new FirebaseAuthClient(new HttpClient(_fake), FakeFirebase.ApiKey, clock: _clock);
    }

    private async Task<RealtimeDbClient> SignedInAsync(string url = "https://x-default-rtdb.firebaseio.com")
    {
        await _auth.SignInAsync(FakeFirebase.Email, FakeFirebase.Password, Ct);
        return new RealtimeDbClient(new HttpClient(_fake), url, _auth);
    }

    [Fact]
    public async Task Get_reads_the_value_and_sends_the_token()
    {
        var db = await SignedInAsync();
        _fake.Set("users/u1", new JsonObject { ["name"] = "Ana", ["role"] = "member" });

        var user = await db.GetAsync("users/u1", Ct);

        Assert.Equal("Ana", user!["name"]!.GetValue<string>());
        var call = Assert.Single(_fake.Calls, c => c.Method == "GET");
        Assert.Equal("users/u1", call.Path);
        Assert.Contains("auth=tok-", call.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_of_nothing_is_null()
    {
        var db = await SignedInAsync();

        Assert.Null(await db.GetAsync("users/nobody", Ct));
    }

    [Fact]
    public async Task Shallow_get_asks_for_the_keys_only()
    {
        var db = await SignedInAsync();
        _fake.Set("requests/a", new JsonObject { ["uid"] = "u" });
        _fake.Set("requests/b", new JsonObject { ["uid"] = "u" });

        var keys = (JsonObject)(await db.GetAsync("requests", Ct, shallow: true))!;

        Assert.Equal(["a", "b"], keys.Select(p => p.Key).Order());
        Assert.True(keys["a"]!.GetValue<bool>());
        Assert.Contains("shallow=true", _fake.Calls[^1].Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Put_patch_and_delete_reach_the_database()
    {
        var db = await SignedInAsync();

        await db.PutAsync("requests/r1/status", new JsonObject { ["state"] = "queued", ["at"] = 1 }, Ct);
        await db.PatchAsync("live", new JsonObject { ["led"] = true }, Ct);
        await db.DeleteAsync("requests/r1", Ct);

        Assert.Equal(["PUT", "PATCH", "DELETE"], _fake.Writes().Select(c => c.Method));
        Assert.All(_fake.Writes(), c => Assert.Contains("print=silent", c.Query, StringComparison.Ordinal));
        Assert.Null(_fake.Get("requests/r1"));
        Assert.True(_fake.Get("live/led")!.GetValue<bool>());
    }

    [Fact]
    public async Task Path_segments_are_escaped()
    {
        var db = await SignedInAsync();

        await db.PutAsync("requests/a b/status", new JsonObject { ["state"] = "x", ["at"] = 1 }, Ct);

        Assert.Equal("requests/a b/status", _fake.Writes()[0].Path);
    }

    [Fact]
    public async Task The_emulator_namespace_query_is_kept()
    {
        var db = await SignedInAsync("http://127.0.0.1:9000?ns=demo-alertaibt");

        await db.GetAsync("users/x", Ct);

        Assert.Contains("ns=demo-alertaibt", _fake.Calls[^1].Query, StringComparison.Ordinal);
        Assert.Contains("auth=", _fake.Calls[^1].Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_401_is_tried_once_more_with_a_renewed_token()
    {
        var db = await SignedInAsync();
        _fake.RevokeTokens();

        var value = await db.GetAsync("users/x", Ct);

        Assert.Null(value);
        Assert.Equal(1, _fake.Refreshes);
        Assert.Equal(2, _fake.Calls.Count(c => c.Method == "GET"));
    }

    [Fact]
    public async Task A_401_that_stays_is_a_permission_denial()
    {
        var db = await SignedInAsync();
        _fake.DbRule = (_, path) => path == "users/x" ? HttpStatusCode.Unauthorized : null;

        var error = await Assert.ThrowsAsync<RtdbException>(() => db.GetAsync("users/x", Ct));

        Assert.Equal(RtdbFailure.PermissionDenied, error.Failure);
        Assert.Equal(HttpStatusCode.Unauthorized, error.Status);
    }

    [Fact]
    public async Task A_server_error_and_a_dead_network_are_told_apart()
    {
        var db = await SignedInAsync();
        _fake.DbRule = (_, _) => HttpStatusCode.ServiceUnavailable;
        var server = await Assert.ThrowsAsync<RtdbException>(() => db.GetAsync("a", Ct));
        _fake.DbRule = null;
        _fake.NetworkDown = true;
        var network = await Assert.ThrowsAsync<RtdbException>(() => db.GetAsync("a", Ct));

        Assert.Equal(RtdbFailure.Server, server.Failure);
        Assert.Equal(RtdbFailure.Network, network.Failure);
        Assert.DoesNotContain("tok-", network.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("x-default-rtdb", network.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_307_redirect_is_followed_even_when_the_client_does_not_follow_redirects()
    {
        var db = await SignedInAsync();
        _fake.Set("users/u1", new JsonObject { ["name"] = "Ana" });
        _fake.RedirectNextStreamTo = "https://s-shard-1.firebaseio.com/users/u1.json?auth=" + await _auth.GetTokenAsync(false, Ct);

        using var response = await db.OpenStreamAsync("users/u1", false, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, _fake.Calls.Count(c => c.Method == "GET"));
    }

    [Fact]
    public async Task The_stream_request_asks_for_an_event_stream()
    {
        var db = await SignedInAsync();

        using var response = await db.OpenStreamAsync("requests", false, Ct);

        Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);
        Assert.Single(_fake.Streams);
    }

    [Fact]
    public void A_database_url_that_is_not_http_is_refused()
    {
        Assert.Throws<ArgumentException>(() => new RealtimeDbClient(new HttpClient(_fake), "ftp://x.firebaseio.com", _auth));
        Assert.Throws<ArgumentException>(() => new RealtimeDbClient(new HttpClient(_fake), "not a url", _auth));
    }
}
