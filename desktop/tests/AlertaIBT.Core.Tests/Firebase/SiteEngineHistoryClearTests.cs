using System.Net;
using System.Text.Json.Nodes;
using AlertaIBT.Core.Firebase;
using AlertaIBT.Core.Tests.Services;

namespace AlertaIBT.Core.Tests.Firebase;

// "Limpar historico" on the site: finished requests go, requests that are still alive stay, /live/recent is emptied.
public sealed class SiteEngineHistoryClearTests
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    private static JsonObject Status(string state) => new() { ["state"] = state, ["at"] = 1 };

    private static bool IsEmptyList(JsonNode? node) => node is null || node is JsonArray { Count: 0 };

    [Fact]
    public async Task Finished_requests_are_deleted_active_ones_are_kept_and_the_recent_list_is_emptied()
    {
        await using var rig = new SiteRig();
        rig.Send("f-shown", SiteRig.Good, tweak: r => r["status"] = Status("shown"));
        rig.Send("f-rejected", SiteRig.Good, tweak: r => r["status"] = Status("rejected"));
        rig.Send("f-cancelled", SiteRig.Good, tweak: r => r["status"] = Status("cancelled"));
        rig.Send("f-expired", SiteRig.Good, tweak: r => r["status"] = Status("expired"));
        rig.Send("unreadable", "   "); // no text: the engine ignores it and it has no status, so it is not finished
        await rig.StartOnlineAsync();

        _ = await rig.H.Pc("fiat uno azul pcp1a11");
        (await rig.H.Display.NextPass(1)).Complete(true);
        await rig.WaitFor(() => (rig.Live?["recent"] as JsonArray)?.Count == 1, "a recent notice in live");

        rig.Send("r-showing", "hilux preta xyz9k88");
        await rig.WaitState("r-showing", "showing");
        rig.Send("r-queued", "onix preto qwe1b23");
        await rig.WaitState("r-queued", "queued");

        var result = await rig.Engine.ClearHistoryAsync(Ct);

        Assert.Equal(new SiteClearResult(4, 0), result);
        foreach (var gone in new[] { "f-shown", "f-rejected", "f-cancelled", "f-expired" })
        {
            Assert.Null(rig.Fake.Get("requests/" + gone));
        }

        foreach (var kept in new[] { "r-showing", "r-queued", "unreadable" })
        {
            Assert.NotNull(rig.Fake.Get("requests/" + kept));
        }

        Assert.Equal("showing", rig.StateOf("r-showing"));
        Assert.Equal("queued", rig.StateOf("r-queued"));
        Assert.True(IsEmptyList(rig.Fake.Get("live/recent")));
    }

    [Fact]
    public async Task The_recent_list_stays_empty_once_the_pc_history_is_cleared_too()
    {
        await using var rig = new SiteRig();
        await rig.StartOnlineAsync();
        _ = await rig.H.Pc("fiat uno azul pcp1a11");
        (await rig.H.Display.NextPass(1)).Complete(true);
        await rig.WaitFor(() => (rig.Live?["recent"] as JsonArray)?.Count == 1, "a recent notice in live");

        await rig.Engine.ClearHistoryAsync(Ct);
        rig.H.Queue.ClearHistory();

        await rig.WaitFor(() => rig.Live?["version"] is not null && IsEmptyList(rig.Live["recent"]), "live without recent");
        await Wait.Settle();
        Assert.True(IsEmptyList(rig.Live!["recent"]));
        Assert.NotNull(rig.Live["version"]); // the rest of /live was rewritten, not lost
    }

    [Fact]
    public async Task A_request_the_database_refuses_to_delete_is_counted_and_the_others_still_go()
    {
        await using var rig = new SiteRig();
        rig.Send("a", SiteRig.Good, tweak: r => r["status"] = Status("shown"));
        rig.Send("b", SiteRig.Good, tweak: r => r["status"] = Status("shown"));
        rig.Send("c", SiteRig.Good, tweak: r => r["status"] = Status("shown"));
        await rig.StartOnlineAsync();
        rig.DenyWrites(path => path == "requests/b" ? HttpStatusCode.Forbidden : null);

        var result = await rig.Engine.ClearHistoryAsync(Ct);

        Assert.Equal(new SiteClearResult(2, 1), result);
        Assert.Null(rig.Fake.Get("requests/a"));
        Assert.NotNull(rig.Fake.Get("requests/b"));
        Assert.Null(rig.Fake.Get("requests/c"));
    }

    [Fact]
    public async Task Nothing_is_deleted_when_there_are_no_finished_requests()
    {
        await using var rig = new SiteRig(ledOn: false);
        await rig.StartOnlineAsync();
        rig.Send("only", SiteRig.Good);
        await rig.WaitState("only", "queued");

        var result = await rig.Engine.ClearHistoryAsync(Ct);

        Assert.Equal(new SiteClearResult(0, 0), result);
        Assert.NotNull(rig.Fake.Get("requests/only"));
        Assert.DoesNotContain(rig.Fake.Writes("requests/only"), c => c.Method == "DELETE");
    }

    [Fact]
    public async Task A_site_that_is_not_online_refuses_to_clear()
    {
        await using var rig = new SiteRig();

        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Engine.ClearHistoryAsync(Ct));
    }

    [Fact]
    public async Task A_request_that_just_finished_gets_its_last_status_before_it_is_deleted()
    {
        await using var rig = new SiteRig(debounce: TimeSpan.FromSeconds(30));
        await rig.StartOnlineAsync();
        rig.Send("r1", SiteRig.Good);
        await rig.WaitFor(() => rig.H.Queue.Current is not null || rig.H.Queue.Pending.Count > 0, "in the queue");
        (await rig.H.Display.NextPass(1)).Complete(true);
        await rig.WaitFor(() => rig.H.Queue.History.Count == 1, "shown in the PC history");

        var result = await rig.Engine.ClearHistoryAsync(Ct);

        Assert.Equal(1, result.Deleted); // its "shown" status was written by the clear itself, then it was deleted
        Assert.Null(rig.Fake.Get("requests/r1"));
    }
}
