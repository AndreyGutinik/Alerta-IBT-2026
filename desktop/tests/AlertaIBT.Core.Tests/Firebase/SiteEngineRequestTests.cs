using System.Net;
using System.Text.Json.Nodes;
using AlertaIBT.Core.Alerts;
using AlertaIBT.Core.Firebase;
using AlertaIBT.Core.Queue;
using AlertaIBT.Core.Services;
using AlertaIBT.Core.Tests.Services;

namespace AlertaIBT.Core.Tests.Firebase;

// What the engine does with each request that arrives: every outcome of the pipeline, the checks before it, and the safety rails.
public sealed class SiteEngineRequestTests
{
    private const string Sermon = "prega\U000000E7\U000000E3o";

    [Fact]
    public async Task A_member_request_is_queued_with_its_place_and_the_queue_item_has_the_request_id()
    {
        await using var rig = new SiteRig(ledOn: false);
        await rig.StartOnlineAsync();

        rig.Send("r1", SiteRig.Good);
        rig.Send("r2", "hilux preta xyz9k88");
        await rig.WaitState("r1", "queued");
        await rig.WaitState("r2", "queued");

        var first = rig.StatusOf("r1")!;
        Assert.Equal(1, first["position"]!.GetValue<int>());
        Assert.Contains("ABC-1D23", first["text"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(2, rig.StatusOf("r2")!["position"]!.GetValue<int>());
        Assert.Equal(["at", "position", "state", "text"], first.Select(p => p.Key).Order());
        var item = rig.H.Queue.Pending[0];
        Assert.Equal("r1", item.Id);
        Assert.Equal(AlertSource.Site, item.Source);
        Assert.Equal(SiteRig.Member, item.SenderId);
        Assert.False(item.Urgent);
    }

    [Fact]
    public async Task An_urgent_request_goes_first_and_the_places_of_the_others_are_rewritten()
    {
        await using var rig = new SiteRig(ledOn: false);
        await rig.StartOnlineAsync();
        rig.Send("r1", SiteRig.Good);
        await rig.WaitState("r1", "queued");

        rig.Send("r2", "hilux preta xyz9k88", urgent: true);
        await rig.WaitState("r2", "queued");

        await rig.WaitFor(() => rig.StatusOf("r1")?["position"]?.GetValue<int>() == 2, "r1 moved to place 2");
        Assert.Equal(1, rig.StatusOf("r2")!["position"]!.GetValue<int>());
    }

    [Fact]
    public async Task A_request_goes_from_queued_to_showing_to_shown_and_the_group_is_told()
    {
        await using var rig = new SiteRig();
        await rig.StartOnlineAsync();

        rig.Send("r1", SiteRig.Good);
        var pass = await rig.H.Display.NextPass(1);
        await rig.WaitState("r1", "showing");
        Assert.Contains("ABC-1D23", rig.StatusOf("r1")!["text"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Null(rig.StatusOf("r1")!["position"]);

        pass.Complete(true);
        await rig.WaitState("r1", "shown");

        Assert.Equal(rig.NowMs, rig.StatusOf("r1")!["at"]!.GetValue<long>());
        Assert.Contains(rig.H.Notifier.Sent, text => text.Contains("Mensagem exibida", StringComparison.Ordinal) && text.Contains("ABC-1D23", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_place_counts_from_the_alert_after_the_one_on_screen()
    {
        await using var rig = new SiteRig();
        await rig.StartOnlineAsync();
        _ = await rig.H.Pc("fiat uno azul pcp1a11");
        await rig.H.Display.NextPass(1);

        rig.Send("r1", SiteRig.Good);
        await rig.WaitState("r1", "queued");

        Assert.Equal(1, rig.StatusOf("r1")!["position"]!.GetValue<int>());
    }

    [Fact]
    public async Task A_request_that_lacks_fields_is_rejected_and_says_which()
    {
        await using var rig = new SiteRig(ledOn: false);
        await rig.StartOnlineAsync();

        rig.Send("r1", "corolla prata");
        await rig.WaitState("r1", "rejected");

        Assert.Equal("faltou placa", rig.ReasonOf("r1"));
        Assert.Empty(rig.H.Queue.Pending);
    }

    [Fact]
    public async Task An_admin_may_send_a_free_notice()
    {
        await using var rig = new SiteRig(ledOn: false);
        await rig.StartOnlineAsync();

        rig.Send("r1", "pais do pedro henrique favor comparecer a recepcao", uid: SiteRig.Admin);
        await rig.WaitState("r1", "queued");

        Assert.Contains("PEDRO HENRIQUE", rig.StatusOf("r1")!["text"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Improper_text_is_rejected_with_the_reason()
    {
        await using var rig = new SiteRig(ledOn: false);
        await rig.StartOnlineAsync();

        rig.Send("r1", "vai tomar no cu seu idiota");
        await rig.WaitState("r1", "rejected");

        Assert.Equal("conte\U000000FAdo impr\U000000F3prio", rig.ReasonOf("r1"));
        Assert.Empty(rig.H.Queue.Pending);
    }

    [Fact]
    public async Task A_text_that_is_too_long_is_rejected()
    {
        await using var rig = new SiteRig(ledOn: false);
        await rig.StartOnlineAsync();

        rig.Send("r1", "corolla prata abc1d23 " + new string('x', 150) + " " + new string('y', 150));
        await rig.WaitState("r1", "rejected");

        Assert.Equal("texto grande demais", rig.ReasonOf("r1"));
    }

    [Fact]
    public async Task The_same_plate_twice_is_rejected_as_repeated_for_a_member_but_not_for_an_admin()
    {
        await using var rig = new SiteRig(ledOn: false);
        await rig.StartOnlineAsync();
        rig.Send("r1", SiteRig.Good);
        await rig.WaitState("r1", "queued");

        rig.Send("r2", SiteRig.Good);
        await rig.WaitState("r2", "rejected");
        rig.Send("r3", SiteRig.Good, uid: SiteRig.Admin);
        await rig.WaitState("r3", "queued");

        Assert.Equal("repetido", rig.ReasonOf("r2"));
        Assert.Equal(2, rig.H.Queue.Pending.Count);
    }

    [Fact]
    public async Task During_the_sermon_a_new_request_is_refused_and_the_queued_ones_are_cancelled_with_the_reason()
    {
        await using var rig = new SiteRig(ledOn: false);
        await rig.StartOnlineAsync();
        rig.Send("r1", SiteRig.Good);
        rig.Send("r2", "hilux preta xyz9k88");
        await rig.WaitState("r2", "queued");

        rig.H.Service.Pause();
        await rig.WaitState("r1", "cancelled");
        await rig.WaitState("r2", "cancelled");
        rig.Send("r3", "civic branco ijk1l22");
        await rig.WaitState("r3", "rejected");

        Assert.Equal(Sermon, rig.ReasonOf("r1"));
        Assert.Equal(Sermon, rig.ReasonOf("r3"));
    }

    [Fact]
    public async Task A_blocked_or_unknown_user_is_rejected_with_access_removed()
    {
        await using var rig = new SiteRig(ledOn: false);
        await rig.StartOnlineAsync();

        rig.Send("r1", SiteRig.Good, uid: SiteRig.Blocked);
        rig.Send("r2", "hilux preta xyz9k88", uid: "u-nobody");
        await rig.WaitState("r1", "rejected");
        await rig.WaitState("r2", "rejected");

        Assert.Equal("acesso removido", rig.ReasonOf("r1"));
        Assert.Equal("acesso removido", rig.ReasonOf("r2"));
        Assert.Empty(rig.H.Queue.Pending);
    }

    [Fact]
    public async Task A_member_may_send_ten_per_hour_and_the_eleventh_is_refused()
    {
        await using var rig = new SiteRig(ledOn: false);
        await rig.StartOnlineAsync();

        for (var i = 0; i < 10; i++)
        {
            rig.Send("r" + i, "corolla prata abc1d2" + i);
        }

        rig.Send("r10", "corolla prata abd1d20");
        await rig.WaitState("r10", "rejected");

        Assert.Equal("limite de 10 por hora", rig.ReasonOf("r10"));
        for (var i = 0; i < 10; i++)
        {
            Assert.Equal("queued", rig.StateOf("r" + i));
        }
    }

    [Fact]
    public async Task The_limit_opens_again_when_the_hour_rolls_over_and_an_admin_has_none()
    {
        await using var rig = new SiteRig(ledOn: false);
        await rig.StartOnlineAsync();
        for (var i = 0; i < 10; i++)
        {
            rig.Send("r" + i, "corolla prata abc1d2" + i);
        }

        rig.Send("r10", "corolla prata abd1d20");
        await rig.WaitState("r10", "rejected");

        await rig.AdvanceAsync(TimeSpan.FromMinutes(61), TimeSpan.FromSeconds(15));
        rig.Send("r11", "corolla prata abe1d20");
        await rig.WaitState("r11", "queued");
        for (var i = 0; i < 12; i++)
        {
            rig.Send("a" + i, "corolla prata abf1d2" + (i % 10), uid: SiteRig.Admin);
        }

        await rig.WaitFor(() => Enumerable.Range(0, 12).All(i => rig.StateOf("a" + i) is not null), "all admin requests answered");
        Assert.DoesNotContain(Enumerable.Range(0, 12), i => rig.ReasonOf("a" + i) == "limite de 10 por hora");
    }

    [Fact]
    public async Task A_forged_preview_is_ignored_and_the_text_comes_from_the_raw_text()
    {
        await using var rig = new SiteRig(ledOn: false);
        await rig.StartOnlineAsync();

        rig.Send("r1", SiteRig.Good, tweak: r => r["preview"] = new JsonObject
        {
            ["text"] = "FERRARI VERMELHA | PLACA: ZZZ-9Z99 | ENTRADA LIVRE",
            ["kind"] = "vehicle",
            ["plate"] = "ZZZ-9Z99",
            ["by"] = "gemini",
        });
        await rig.WaitState("r1", "queued");

        var text = rig.StatusOf("r1")!["text"]!.GetValue<string>();
        Assert.Contains("ABC-1D23", text, StringComparison.Ordinal);
        Assert.DoesNotContain("FERRARI", text, StringComparison.Ordinal);
        Assert.DoesNotContain("ZZZ", text, StringComparison.Ordinal);
        Assert.Equal("ABC-1D23", rig.H.Queue.Pending[0].Plate);
    }

    [Fact]
    public async Task Malformed_requests_are_ignored_and_the_next_one_still_works()
    {
        await using var rig = new SiteRig(ledOn: false);
        await rig.StartOnlineAsync();

        rig.Fake.Set("requests/bad1", new JsonObject { ["uid"] = SiteRig.Member, ["raw"] = 42 });
        rig.Fake.Set("requests/bad2", JsonValue.Create("just text"));
        rig.Fake.Set("requests/bad3", new JsonObject { ["name"] = "x" });
        rig.Send("r1", SiteRig.Good);
        await rig.WaitState("r1", "queued");

        Assert.Null(rig.StatusOf("bad1"));
        Assert.Null(rig.StatusOf("bad3"));
        Assert.Single(rig.H.Queue.Pending);
        Assert.Equal(SiteState.Online, rig.Engine.State);
    }

    [Fact]
    public async Task A_request_seen_again_after_the_stream_reconnects_is_handled_once()
    {
        await using var rig = new SiteRig(ledOn: false);
        await rig.StartOnlineAsync();
        rig.Send("r1", SiteRig.Good);
        await rig.WaitState("r1", "queued");

        // Even when the status write is lost, so the request still looks pending in the database.
        rig.Fake.Set("requests/r1/status", null);
        rig.Fake.LastStream.Close();
        await rig.AdvanceAsync(TimeSpan.FromSeconds(3));
        await rig.WaitFor(() => rig.Fake.Streams.Length == 2, "stream reopened");
        await rig.AdvanceAsync(TimeSpan.FromSeconds(2));

        Assert.Single(rig.H.Queue.Pending);
        Assert.Empty(rig.H.Queue.History);
    }

    [Fact]
    public async Task A_request_whose_status_write_failed_is_not_queued_twice_when_seen_again()
    {
        await using var rig = new SiteRig(ledOn: false);
        await rig.StartOnlineAsync();
        rig.DenyWrites(path => path.EndsWith("/status", StringComparison.Ordinal) ? HttpStatusCode.ServiceUnavailable : null);

        rig.Send("r1", SiteRig.Good);
        await rig.WaitFor(() => rig.H.Queue.Pending.Count == 1, "queued");
        rig.Fake.LastStream.Close();
        await rig.AdvanceAsync(TimeSpan.FromSeconds(3));
        rig.Fake.DbRule = null;
        await rig.AdvanceAsync(TimeSpan.FromSeconds(31));

        await rig.WaitState("r1", "queued");
        Assert.Single(rig.H.Queue.Pending);
    }

    [Fact]
    public async Task A_cancel_removes_a_queued_request_and_answers_cancelled_without_a_reason()
    {
        await using var rig = new SiteRig(ledOn: false);
        await rig.StartOnlineAsync();
        rig.Send("r1", SiteRig.Good);
        await rig.WaitState("r1", "queued");

        rig.Fake.Set("requests/r1/cancel", JsonValue.Create(true));
        await rig.WaitState("r1", "cancelled");

        Assert.Empty(rig.H.Queue.Pending);
        Assert.Null(rig.ReasonOf("r1"));
        Assert.Equal(HistoryState.Cancelled, rig.H.Queue.History[0].State);
    }

    [Fact]
    public async Task A_cancel_that_comes_too_late_changes_nothing()
    {
        await using var rig = new SiteRig();
        await rig.StartOnlineAsync();
        rig.Send("r1", SiteRig.Good);
        var pass = await rig.H.Display.NextPass(1);
        await rig.WaitState("r1", "showing");

        rig.Fake.Set("requests/r1/cancel", JsonValue.Create(true));
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Equal("showing", rig.StateOf("r1"));
        pass.Complete(true);

        await rig.WaitState("r1", "shown");
    }

    [Fact]
    public async Task A_request_cancelled_before_the_pc_saw_it_is_never_queued()
    {
        await using var rig = new SiteRig(ledOn: false);
        await rig.StartOnlineAsync();

        rig.Send("r1", SiteRig.Good, tweak: r => r["cancel"] = true);
        await rig.WaitState("r1", "cancelled");

        Assert.Empty(rig.H.Queue.Pending);
        Assert.Empty(rig.H.Queue.History);
    }

    [Fact]
    public async Task A_request_that_waited_more_than_thirty_minutes_is_expired_not_shown()
    {
        await using var rig = new SiteRig();
        await rig.StartOnlineAsync();

        rig.Send("r1", SiteRig.Good, age: TimeSpan.FromMinutes(31));
        rig.Send("r2", "hilux preta xyz9k88", age: TimeSpan.FromMinutes(29));
        await rig.WaitState("r1", "expired");
        await rig.WaitState("r2", "showing");

        Assert.Equal(["HILUX PRETA | PLACA: XYZ-9K88"], rig.H.Display.Texts);
    }

    [Fact]
    public async Task What_arrived_while_the_pc_was_off_is_expired_when_it_comes_back()
    {
        await using var rig = new SiteRig();
        await rig.StartOnlineAsync();
        rig.Fake.NetworkDown = true;
        rig.Fake.LastStream.Close();

        // The phone sends while the PC cannot see the database; the PC comes back 40 minutes later.
        rig.Send("r1", SiteRig.Good);
        await rig.AdvanceAsync(TimeSpan.FromMinutes(40), TimeSpan.FromSeconds(30));
        rig.Fake.NetworkDown = false;
        await rig.AdvanceAsync(TimeSpan.FromSeconds(40), TimeSpan.FromSeconds(1));

        await rig.WaitState("r1", "expired");
        Assert.Empty(rig.H.Display.Texts);
    }

    [Fact]
    public async Task A_request_whose_user_cannot_be_read_is_tried_again_at_the_next_heartbeat()
    {
        await using var rig = new SiteRig(ledOn: false);
        await rig.StartOnlineAsync();
        rig.Fake.DbRule = (method, path) => method == "GET" && path.StartsWith("users/", StringComparison.Ordinal) ? HttpStatusCode.ServiceUnavailable : null;

        rig.Send("r1", SiteRig.Good);
        await Task.Delay(150, TestContext.Current.CancellationToken);
        Assert.Null(rig.StateOf("r1"));
        Assert.Empty(rig.H.Queue.Pending);
        rig.Fake.DbRule = null;
        await rig.AdvanceAsync(SiteEngine.HeartbeatInterval + TimeSpan.FromSeconds(1));

        await rig.WaitState("r1", "queued");
        Assert.Single(rig.H.Queue.Pending);
    }

    [Fact]
    public async Task A_rejection_that_could_not_be_written_is_written_at_the_next_heartbeat()
    {
        await using var rig = new SiteRig(ledOn: false);
        await rig.StartOnlineAsync();
        rig.DenyWrites(path => path.EndsWith("/status", StringComparison.Ordinal) ? HttpStatusCode.ServiceUnavailable : null);

        rig.Send("r1", "corolla prata");
        await rig.WaitFor(() => rig.Fake.Writes("requests/r1/status").Length > 0, "a write was attempted");
        Assert.Null(rig.StateOf("r1"));
        rig.Fake.DbRule = null;
        await rig.AdvanceAsync(SiteEngine.HeartbeatInterval + TimeSpan.FromSeconds(1));

        await rig.WaitState("r1", "rejected");
        Assert.Equal("faltou placa", rig.ReasonOf("r1"));
    }

    [Fact]
    public async Task A_request_that_expires_in_the_queue_is_told_expired()
    {
        await using var rig = new SiteRig(ledOn: false);
        await rig.StartOnlineAsync();
        rig.Send("r1", SiteRig.Good);
        await rig.WaitState("r1", "queued");

        await rig.AdvanceAsync(TimeSpan.FromMinutes(32), TimeSpan.FromSeconds(30));

        await rig.WaitState("r1", "expired");
        Assert.Empty(rig.H.Queue.Pending);
    }

    [Fact]
    public async Task An_alert_removed_at_the_pc_is_told_cancelled_with_the_pc_reason()
    {
        await using var rig = new SiteRig(ledOn: false);
        await rig.StartOnlineAsync();
        rig.Send("r1", SiteRig.Good);
        await rig.WaitState("r1", "queued");

        rig.H.Queue.Remove("r1");

        await rig.WaitState("r1", "cancelled");
        Assert.Equal("removido no PC", rig.ReasonOf("r1"));
    }

    [Fact]
    public async Task The_telegram_and_pc_paths_keep_working_when_the_site_is_down()
    {
        await using var rig = new SiteRig();
        await rig.StartOnlineAsync();
        rig.Fake.NetworkDown = true;
        rig.Fake.LastStream.Close();

        var telegram = await rig.H.Telegram("corolla prata abc1d23 vidro aberto");
        var pass = await rig.H.Display.NextPass(1);
        pass.Complete(true);

        Assert.Equal(SubmitKind.Queued, telegram.Kind);
        await rig.WaitFor(() => rig.H.Queue.History.Count == 1, "shown");
        Assert.Contains(rig.H.Notifier.Sent, text => text.Contains("Mensagem exibida", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Nothing_secret_reaches_the_log()
    {
        await using var rig = new SiteRig(ledOn: false);
        await rig.StartOnlineAsync();
        rig.Send("r1", SiteRig.Good);
        await rig.WaitState("r1", "queued");
        rig.Fake.LastStream.Close();
        await rig.AdvanceAsync(TimeSpan.FromSeconds(3));

        var text = string.Join('\n', rig.Log.Entries.Select(e => e.Message));
        Assert.NotEmpty(rig.Log.Entries);
        Assert.DoesNotContain(FakeFirebase.Password, text, StringComparison.Ordinal);
        Assert.DoesNotContain(FakeFirebase.ApiKey, text, StringComparison.Ordinal);
        Assert.DoesNotContain("tok-", text, StringComparison.Ordinal);
        Assert.DoesNotContain("refresh-", text, StringComparison.Ordinal);
    }
}
