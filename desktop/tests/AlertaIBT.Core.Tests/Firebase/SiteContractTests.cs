using System.Text.Json.Nodes;
using AlertaIBT.Core.Firebase;
using AlertaIBT.Core.Queue;

namespace AlertaIBT.Core.Tests.Firebase;

public sealed class SiteContractTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 25, 9, 0, 0, TimeSpan.Zero);

    private static JsonObject Request(Action<JsonObject>? change = null)
    {
        var request = new JsonObject
        {
            ["uid"] = "u1",
            ["name"] = "Ana",
            ["raw"] = "corolla prata abc1d23",
            ["urgent"] = false,
            ["createdAt"] = T0.ToUnixTimeMilliseconds(),
            ["cancel"] = false,
        };
        change?.Invoke(request);
        return request;
    }

    [Fact]
    public void A_good_request_is_read()
    {
        var ok = SiteRequest.TryParse("r1", Request(r => r["urgent"] = true), out var request);

        Assert.True(ok);
        Assert.Equal("r1", request.Id);
        Assert.Equal("u1", request.Uid);
        Assert.Equal("corolla prata abc1d23", request.Raw);
        Assert.True(request.Urgent);
        Assert.Equal(T0.ToUnixTimeMilliseconds(), request.CreatedAt);
        Assert.False(request.Cancel);
        Assert.Equal("pending", request.State);
    }

    [Fact]
    public void The_state_comes_from_the_status()
    {
        var node = Request(r => r["status"] = new JsonObject { ["state"] = "queued", ["at"] = 1 });

        Assert.True(SiteRequest.TryParse("r1", node, out var request));
        Assert.Equal("queued", request.State);
    }

    [Theory]
    [InlineData("uid")]
    [InlineData("raw")]
    public void A_request_without_the_sender_or_the_text_is_ignored(string missing)
    {
        var node = Request(r => r.Remove(missing));

        Assert.False(SiteRequest.TryParse("r1", node, out _));
    }

    [Fact]
    public void A_request_with_fields_of_the_wrong_type_is_ignored_or_defaulted()
    {
        Assert.False(SiteRequest.TryParse("r1", Request(r => r["raw"] = 42), out _));
        Assert.False(SiteRequest.TryParse("r1", Request(r => r["uid"] = new JsonObject()), out _));
        Assert.False(SiteRequest.TryParse("r1", Request(r => r["raw"] = "   "), out _));
        Assert.False(SiteRequest.TryParse("r1", JsonValue.Create("text"), out _));
        Assert.False(SiteRequest.TryParse("r1", null, out _));
        Assert.True(SiteRequest.TryParse("r1", Request(r => { r["urgent"] = "yes"; r["cancel"] = 1; r["createdAt"] = "now"; }), out var odd));
        Assert.False(odd.Urgent);
        Assert.False(odd.Cancel);
        Assert.Equal(0, odd.CreatedAt);
    }

    [Fact]
    public void The_status_json_holds_only_the_fields_the_rules_allow()
    {
        var json = new StatusValue("queued", Position: 3, Reason: "r", Text: "t").ToJson(1000);

        Assert.Equal(["at", "position", "reason", "state", "text"], json.Select(p => p.Key).Order());
        Assert.Equal(1000, json["at"]!.GetValue<long>());
        Assert.Equal(3, json["position"]!.GetValue<int>());
    }

    [Fact]
    public void The_status_json_leaves_out_what_is_empty_and_clips_what_is_long()
    {
        var plain = new StatusValue("expired", Position: 0, Reason: "", Text: null, At: 5).ToJson(1000);
        var long_ = new StatusValue("rejected", Reason: new string('r', 500), Text: new string('t', 900)).ToJson(1000);

        Assert.Equal(["at", "state"], plain.Select(p => p.Key).Order());
        Assert.Equal(5, plain["at"]!.GetValue<long>());
        Assert.Equal(200, long_["reason"]!.GetValue<string>().Length);
        Assert.Equal(400, long_["text"]!.GetValue<string>().Length);
    }

    [Fact]
    public void A_cancel_by_the_sender_has_no_reason_but_the_others_keep_theirs()
    {
        var item = new QueueItem("x", "T", null, Core.Alerts.AlertSource.Site, false, T0, "u", null);

        Assert.Null(SiteContract.ReasonOf(new HistoryEntry(item, HistoryState.Cancelled, T0, HistoryReasons.CancelledOnSite)));
        Assert.Equal("prega\U000000E7\U000000E3o", SiteContract.ReasonOf(new HistoryEntry(item, HistoryState.Cancelled, T0, HistoryReasons.Sermon)));
        Assert.Equal("cancelled", SiteContract.StateOf(HistoryState.Cancelled));
        Assert.Equal("shown", SiteContract.StateOf(HistoryState.Shown));
        Assert.Equal("rejected", SiteContract.StateOf(HistoryState.Rejected));
        Assert.Equal("expired", SiteContract.StateOf(HistoryState.Expired));
    }

    [Fact]
    public void The_tenth_notice_of_the_hour_passes_and_the_eleventh_does_not()
    {
        var limiter = new PerPersonLimiter(10, TimeSpan.FromHours(1));

        for (var i = 1; i <= 10; i++)
        {
            Assert.True(limiter.TryAcquire("u1", T0.AddMinutes(i)), $"notice {i}");
        }

        Assert.False(limiter.TryAcquire("u1", T0.AddMinutes(11)));
        Assert.False(limiter.TryAcquire("u1", T0.AddMinutes(30)));
        Assert.True(limiter.TryAcquire("u2", T0.AddMinutes(30)));
    }

    [Fact]
    public void The_count_goes_down_as_the_hour_rolls_over()
    {
        var limiter = new PerPersonLimiter(10, TimeSpan.FromHours(1));
        for (var i = 0; i < 10; i++)
        {
            Assert.True(limiter.TryAcquire("u1", T0.AddMinutes(i)));
        }

        // The first notice was at minute 0: an hour later it no longer counts, the second still does.
        Assert.False(limiter.TryAcquire("u1", T0.AddMinutes(59)));
        Assert.True(limiter.TryAcquire("u1", T0.AddMinutes(60)));
        Assert.False(limiter.TryAcquire("u1", T0.AddMinutes(60)));
        Assert.True(limiter.TryAcquire("u1", T0.AddMinutes(61)));
    }

    [Fact]
    public void Rejected_attempts_over_the_limit_do_not_extend_the_wait()
    {
        var limiter = new PerPersonLimiter(2, TimeSpan.FromHours(1));
        Assert.True(limiter.TryAcquire("u1", T0));
        Assert.True(limiter.TryAcquire("u1", T0.AddMinutes(1)));
        for (var i = 2; i < 50; i++)
        {
            Assert.False(limiter.TryAcquire("u1", T0.AddMinutes(i)));
        }

        Assert.True(limiter.TryAcquire("u1", T0.AddMinutes(60)));
    }

    [Fact]
    public void Notices_of_an_earlier_session_count_after_a_restart()
    {
        var limiter = new PerPersonLimiter(10, TimeSpan.FromHours(1));
        var now = T0.AddMinutes(30);
        for (var i = 0; i < 9; i++)
        {
            limiter.Seed("u1", T0.AddMinutes(i), now);
        }

        limiter.Seed("u1", T0.AddHours(-3), now);

        Assert.True(limiter.TryAcquire("u1", now));
        Assert.False(limiter.TryAcquire("u1", now));
    }
}
