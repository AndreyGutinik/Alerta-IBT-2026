using AlertaIBT.Core.Alerts;
using AlertaIBT.Core.Correction;
using AlertaIBT.Core.Queue;
using AlertaIBT.Core.Services;
using AlertaIBT.Core.Settings;
using AlertaIBT.Core.Telegram;

namespace AlertaIBT.Core.Tests.Services;

public class AlertServiceSubmitTests
{
    private const string Good = "corolla prata abc1d23 vidro aberto";
    private const string GoodText = "COROLLA PRATA | PLACA: ABC-1D23 | VIDRO ABERTO";

    // The loop is not what these tests are about: with the LED off nothing is played and the queue stays as submitted.
    private static ServiceHarness Quiet(Func<string, CancellationToken, Task<ParsedAlert?>>? gemini = null)
    {
        var h = new ServiceHarness(gemini, start: false);
        h.Display.SetLed(false);
        h.Service.Start();
        return h;
    }

    // ---- accepted ----

    [Fact]
    public async Task Telegram_complete_alert_is_queued_and_with_the_LED_off_the_sender_is_told_once()
    {
        using var h = Quiet();

        var outcome = await h.Telegram(Good);

        Assert.Equal(SubmitKind.Queued, outcome.Kind);
        Assert.Equal(1, outcome.Position);
        Assert.Equal(GoodText, outcome.Text);
        Assert.Equal("id1", outcome.Id);
        Assert.False(outcome.WasDuplicate);
        var queued = Assert.Single(h.Queue.Pending);
        Assert.Equal(GoodText, queued.Text);
        Assert.Equal("ABC-1D23", queued.Plate);
        Assert.Equal(AlertSource.Telegram, queued.Source);
        Assert.Equal("42", queued.SenderId);
        Assert.Equal(Good, queued.Original);
        Assert.Equal(h.Clock.GetUtcNow(), queued.CreatedAt);
        Assert.Empty(h.Queue.History);
        Assert.Equal([TelegramReplies.LedOff], h.Notifier.Sent); // the LED is off in this harness
    }

    [Fact]
    public async Task Operator_without_plate_queues_an_announcement_and_nothing_is_said_in_the_group()
    {
        using var h = Quiet();

        var outcome = await h.Pc("pais do pedro, favor comparecer a recepcao");

        Assert.Equal(SubmitKind.Queued, outcome.Kind);
        Assert.Equal("PAIS DO PEDRO, FAVOR COMPARECER A RECEPCAO", outcome.Text);
        Assert.Null(h.Queue.Pending[0].Plate);
        Assert.Empty(h.Notifier.Sent);
    }

    [Fact]
    public async Task Urgent_goes_to_the_front()
    {
        using var h = Quiet();
        await h.Pc("gol branco aaa1111");

        var urgent = await h.Pc("onix preto bbb2222", urgent: true);

        Assert.Equal(1, urgent.Position);
        Assert.Equal(["ONIX PRETO | PLACA: BBB-2222", "GOL BRANCO | PLACA: AAA-1111"], h.Queue.Pending.Select(p => p.Text));
    }

    [Fact]
    public async Task Multi_line_emoji_and_very_long_input_becomes_one_clean_line()
    {
        using var h = Quiet();
        var longReason = string.Join(' ', Enumerable.Repeat("farol aceso", 40));
        var raw = "\U0001F697 gol\nbranco\r\n abc1234 " + longReason + " \U0001F680";

        var outcome = await h.Pc(raw);

        Assert.Equal(SubmitKind.Queued, outcome.Kind);
        var text = h.Queue.Pending[0].Text;
        Assert.DoesNotContain('\n', text);
        Assert.DoesNotContain('\r', text);
        Assert.DoesNotContain("\U0001F697", text);
        Assert.StartsWith("GOL BRANCO | PLACA: ABC-1234 | FAROL ACESO", text);
        Assert.True(text.Length > 300);
        Assert.Equal(raw, h.Queue.Pending[0].Original); // the original is kept as typed
        Assert.False(h.Queue.Pending[0].Corrected); // only the layout changed, not the words
    }

    // ---- rejected for missing fields ----

    [Fact]
    public async Task Telegram_without_plate_is_rejected_told_and_recorded_once()
    {
        using var h = Quiet();

        var outcome = await h.Telegram("gol branco farol aceso");

        Assert.Equal(SubmitKind.Rejected, outcome.Kind);
        Assert.Equal(["placa"], outcome.Missing);
        Assert.Empty(h.Queue.Pending);
        var entry = Assert.Single(h.Queue.History);
        Assert.Equal(HistoryState.Rejected, entry.State);
        Assert.Equal("faltou placa", entry.Reason);
        Assert.Equal("gol branco farol aceso", entry.Item.Original);
        Assert.Equal([TelegramReplies.Missing(["placa"])], h.Notifier.Sent);
    }

    [Fact]
    public async Task Telegram_missing_several_fields_lists_them_in_order()
    {
        using var h = Quiet();

        var outcome = await h.Telegram("farol aceso");

        Assert.Equal(["modelo", "cor", "placa"], outcome.Missing);
        Assert.Equal("faltou modelo, cor e placa", h.Queue.History[0].Reason);
        Assert.Equal([TelegramReplies.Missing(["modelo", "cor", "placa"])], h.Notifier.Sent);
    }

    [Fact]
    public async Task Telegram_message_with_nothing_but_emoji_is_rejected_without_crashing_the_reply()
    {
        using var h = Quiet();

        var outcome = await h.Telegram("\U0001F697\U0001F697");

        Assert.Equal(SubmitKind.Rejected, outcome.Kind);
        Assert.Equal(["modelo", "cor", "placa"], outcome.Missing);
        Assert.Single(h.Queue.History);
        Assert.Equal([TelegramReplies.Missing(["modelo", "cor", "placa"])], h.Notifier.Sent);
    }

    [Fact]
    public async Task A_rejection_from_the_pc_says_nothing_in_the_group()
    {
        using var h = Quiet();

        var outcome = await h.Pc("   ");

        Assert.Equal(SubmitKind.Rejected, outcome.Kind);
        Assert.Empty(h.Notifier.Sent);
        Assert.Single(h.Queue.History);
    }

    // ---- duplicates (the queue already writes the history: the service must not add a second line) ----

    [Fact]
    public async Task Telegram_duplicate_of_a_queued_plate_is_blocked_told_and_recorded_exactly_once()
    {
        using var h = Quiet();
        await h.Telegram(Good);
        var before = h.Notifier.Sent.Length;

        var outcome = await h.Telegram("corolla prata abc-1d23 farol aceso");

        Assert.Equal(SubmitKind.Duplicate, outcome.Kind);
        Assert.True(outcome.WasDuplicate);
        Assert.Null(outcome.DuplicateMinutesAgo);
        Assert.Single(h.Queue.Pending);
        var entry = Assert.Single(h.Queue.History);
        Assert.Equal(HistoryState.Rejected, entry.State);
        Assert.Equal(HistoryReasons.Duplicate, entry.Reason);
        Assert.Equal(TelegramReplies.DuplicateQueued, h.Notifier.Sent[before]);
        Assert.Equal(before + 1, h.Notifier.Sent.Length);
    }

    [Fact]
    public async Task Telegram_duplicate_of_a_plate_shown_minutes_ago_tells_how_long()
    {
        using var h = new ServiceHarness();
        await h.Telegram(Good);
        var pass = await h.Display.NextPass(1);
        pass.Complete(true);
        await Wait.Until(() => h.Queue.History.Count == 1, "the first alert is shown");
        h.Clock.Advance(TimeSpan.FromMinutes(5));
        var before = h.Notifier.Sent.Length;

        var outcome = await h.Telegram(Good);

        Assert.Equal(SubmitKind.Duplicate, outcome.Kind);
        Assert.Equal(5, outcome.DuplicateMinutesAgo);
        Assert.Equal(TelegramReplies.DuplicateShown(5), h.Notifier.Sent[before]);
        Assert.Equal(1, h.Queue.History.Count(e => e.State == HistoryState.Rejected));
    }

    [Fact]
    public async Task Pc_duplicate_is_queued_with_a_warning_and_the_group_hears_nothing()
    {
        using var h = Quiet();
        await h.Pc(Good);

        var outcome = await h.Pc(Good);

        Assert.Equal(SubmitKind.Queued, outcome.Kind);
        Assert.True(outcome.WasDuplicate);
        Assert.Equal(2, h.Queue.Pending.Count);
        Assert.Empty(h.Notifier.Sent);
    }

    [Fact]
    public async Task An_administrator_may_repeat_a_plate_but_a_member_may_not()
    {
        using var h = Quiet();
        await h.Pc(Good);

        var admin = await h.Service.SubmitAsync(Good, AlertSource.Site, SenderRole.Admin, false, "u1", TestContext.Current.CancellationToken);
        var member = await h.Service.SubmitAsync(Good, AlertSource.Site, SenderRole.Member, false, "u2", TestContext.Current.CancellationToken);

        Assert.Equal(SubmitKind.Queued, admin.Kind);
        Assert.True(admin.WasDuplicate);
        Assert.Equal(SubmitKind.Duplicate, member.Kind);
        Assert.Empty(h.Notifier.Sent); // a site sender is answered on the site, not in the Telegram group
    }

    // ---- the sermon ----

    [Fact]
    public async Task During_the_pause_a_telegram_alert_is_refused_at_once_without_asking_gemini()
    {
        using var h = Quiet();
        h.Service.Pause();
        var notices = h.Notifier.Sent.Length;

        var outcome = await h.Telegram(Good);

        Assert.Equal(SubmitKind.PausedRefused, outcome.Kind);
        Assert.Empty(h.Queue.Pending);
        var entry = Assert.Single(h.Queue.History);
        Assert.Equal(HistoryState.Rejected, entry.State);
        Assert.Equal(HistoryReasons.Sermon, entry.Reason);
        Assert.Equal(TelegramReplies.PausedRefused, h.Notifier.Sent[notices]);
        Assert.Equal(0, h.Gemini.Calls);
        Assert.Equal(0, h.Offline.Calls);
    }

    [Fact]
    public async Task Pause_that_arrives_while_the_text_is_being_corrected_refuses_it_and_writes_the_history_once()
    {
        var gate = new TaskCompletionSource<ParsedAlert?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var h = Quiet((_, _) => gate.Task);
        var submit = h.Telegram(Good);
        await Wait.Until(() => h.Gemini.Calls == 1, "Gemini was asked");

        h.Service.Pause();
        gate.SetResult(null);
        var outcome = await submit;

        Assert.Equal(SubmitKind.PausedRefused, outcome.Kind);
        Assert.Empty(h.Queue.Pending);
        var entry = Assert.Single(h.Queue.History);
        Assert.Equal(HistoryReasons.Sermon, entry.Reason);
        Assert.Contains(TelegramReplies.PausedRefused, h.Notifier.Sent);
    }

    // ---- improper content ----

    [Fact]
    public async Task Gemini_refusal_is_recorded_as_improper_content_and_the_telegram_sender_is_told()
    {
        using var h = Quiet((_, _) => Task.FromResult<ParsedAlert?>(new ParsedAlert("", "", null, "", "")));

        var outcome = await h.Telegram("fulano e um idiota");

        Assert.Equal(SubmitKind.Refused, outcome.Kind);
        Assert.Empty(h.Queue.Pending);
        var entry = Assert.Single(h.Queue.History);
        Assert.Equal(HistoryState.Rejected, entry.State);
        Assert.Equal(HistoryReasons.ImproperContent, entry.Reason);
        Assert.Equal([TelegramReplies.Refused], h.Notifier.Sent);
    }

    [Fact]
    public async Task Gemini_refusal_from_the_pc_is_recorded_but_the_group_is_not_told()
    {
        using var h = Quiet((_, _) => Task.FromResult<ParsedAlert?>(new ParsedAlert("", "", null, "", "")));

        var outcome = await h.Pc("texto ofensivo");

        Assert.Equal(SubmitKind.Refused, outcome.Kind);
        Assert.Single(h.Queue.History);
        Assert.Empty(h.Notifier.Sent);
    }

    // ---- preview and submit agree ----

    [Fact]
    public async Task Preview_and_submit_produce_the_same_text_and_ask_gemini_once()
    {
        var ai = new ParsedAlert("Corolla", "Prata", "ABC-1D23", "vidro aberto", "Corolla Prata ABC-1D23 vidro aberto");
        using var h = Quiet((_, _) => Task.FromResult<ParsedAlert?>(ai));

        var preview = await h.Service.PreviewAsync("corolla prata abc1d23 vidro abrto", SenderRole.Operator, TestContext.Current.CancellationToken);
        var submit = await h.Pc("corolla prata abc1d23 vidro abrto");

        Assert.Equal(preview.Text, submit.Text);
        Assert.Equal(preview.Text, h.Queue.Pending[0].Text);
        Assert.Equal(CorrectorKind.Gemini, preview.By);
        Assert.True(preview.Changed);
        Assert.True(h.Queue.Pending[0].Corrected); // the window can say "Corrigido. Original: ..."
        Assert.Equal(1, h.Gemini.Calls);
    }

    [Fact]
    public async Task Preview_reports_kind_missing_duplicate_and_corrector()
    {
        using var h = Quiet();
        await h.Pc(Good);

        var vehicle = await h.Service.PreviewAsync(Good, SenderRole.Operator, TestContext.Current.CancellationToken);
        var free = await h.Service.PreviewAsync("favor comparecer a recepcao", SenderRole.Operator, TestContext.Current.CancellationToken);
        var member = await h.Service.PreviewAsync("gol branco farol aceso", SenderRole.Member, TestContext.Current.CancellationToken);

        Assert.Equal(RuleKind.Vehicle, vehicle.Kind);
        Assert.True(vehicle.Duplicate);
        Assert.Equal(GoodText, vehicle.Text);
        Assert.Equal(CorrectorKind.Offline, vehicle.By); // Gemini returned null: offline
        Assert.Equal(RuleKind.Free, free.Kind);
        Assert.False(free.Duplicate);
        Assert.Equal(RuleKind.Reject, member.Kind);
        Assert.Equal(["placa"], member.Missing);
        Assert.Single(h.Queue.Pending); // a preview never queues anything
        Assert.Empty(h.Queue.History);
    }

    [Fact]
    public async Task Preview_of_an_improper_text_is_flagged_refused()
    {
        using var h = Quiet((_, _) => Task.FromResult<ParsedAlert?>(new ParsedAlert("", "", null, "", "")));

        var preview = await h.Service.PreviewAsync("xingamento", SenderRole.Operator, TestContext.Current.CancellationToken);

        Assert.True(preview.Refused);
        Assert.Equal(RuleKind.Reject, preview.Kind);
        Assert.Equal(string.Empty, preview.Text);
    }

    [Fact]
    public async Task Preview_of_a_plate_shown_a_while_ago_gives_the_minutes()
    {
        using var h = new ServiceHarness();
        await h.Pc(Good);
        (await h.Display.NextPass(1)).Complete(true);
        await Wait.Until(() => h.Queue.History.Count == 1);
        h.Clock.Advance(TimeSpan.FromMinutes(3));

        var preview = await h.Service.PreviewAsync(Good, SenderRole.Operator, TestContext.Current.CancellationToken);

        Assert.True(preview.Duplicate);
        Assert.Equal(3, preview.DuplicateMinutesAgo);
    }

    // ---- correction switches ----

    [Fact]
    public async Task Correction_off_uses_the_plain_parser_and_never_touches_the_correctors()
    {
        using var h = Quiet();
        h.Settings = h.Settings with { Correction = h.Settings.Correction with { Enabled = false } };

        var outcome = await h.Pc("corolla prata abc1d23 vidro abrto");

        Assert.Equal("COROLLA PRATA | PLACA: ABC-1D23 | VIDRO ABRTO", outcome.Text);
        Assert.Equal(0, h.Gemini.Calls);
        Assert.Equal(0, h.Offline.Calls);
    }

    [Fact]
    public async Task Offline_mode_never_asks_gemini_and_gemini_mode_does()
    {
        using var h = Quiet();
        h.Settings = h.Settings with { Correction = h.Settings.Correction with { Mode = "offline" } };
        await h.Pc("gol branco aaa1111");
        Assert.Equal(0, h.Gemini.Calls);
        Assert.Equal(1, h.Offline.Calls);

        h.Settings = h.Settings with { Correction = h.Settings.Correction with { Mode = "gemini" } };
        await h.Pc("onix preto bbb2222");

        Assert.Equal(1, h.Gemini.Calls);
    }

    // ---- never throws on a bad listener ----

    [Fact]
    public async Task A_status_listener_that_throws_never_breaks_a_submit_or_the_gemini_event()
    {
        using var h = Quiet();
        h.Hub.PropertyChanged += (_, _) => throw new InvalidOperationException("listener bug");

        var outcome = await h.Pc(Good); // Gemini returns null here: the health event fires inside the pipeline

        Assert.Equal(SubmitKind.Queued, outcome.Kind);
        Assert.Equal(GeminiState.Offline, h.Hub.GeminiState);
    }

    // ---- length cap for members ----

    private const string VehiclePrefix = "COROLLA PRATA | PLACA: ABC-1D23 | ";

    // A complete vehicle alert whose formatted text has exactly this many characters.
    private static string Member(int formattedLength) =>
        "corolla prata abc1d23 " + new string('x', formattedLength - VehiclePrefix.Length);

    [Fact]
    public async Task Member_alert_of_exactly_200_formatted_letters_is_queued()
    {
        using var h = Quiet();

        var outcome = await h.Telegram(Member(AlertRules.MemberMaxLength));

        Assert.Equal(200, AlertRules.MemberMaxLength);
        Assert.Equal(SubmitKind.Queued, outcome.Kind);
        Assert.Equal(200, h.Queue.Pending[0].Text.Length);
    }

    [Fact]
    public async Task Member_alert_of_201_formatted_letters_is_rejected_told_and_recorded()
    {
        using var h = Quiet();

        var outcome = await h.Telegram(Member(AlertRules.MemberMaxLength + 1));

        Assert.Equal(SubmitKind.TooLong, outcome.Kind);
        Assert.Empty(outcome.Missing);
        Assert.Null(outcome.Id);
        Assert.Equal(201, outcome.Text.Length);
        Assert.Empty(h.Queue.Pending);
        var entry = Assert.Single(h.Queue.History);
        Assert.Equal(HistoryState.Rejected, entry.State);
        Assert.Equal("texto grande demais", entry.Reason);
        Assert.Equal(AlertSource.Telegram, entry.Item.Source);
        Assert.Equal([TelegramReplies.TooLong(200)], h.Notifier.Sent);
    }

    [Fact]
    public async Task The_limit_counts_the_formatted_text_not_what_was_typed()
    {
        using var h = Quiet();
        // 230 typed characters, but whitespace is squeezed and the emoji dropped: 200 are left.
        var raw = Member(AlertRules.MemberMaxLength) + new string(' ', 15) + "\U0001F697\U0001F697\U0001F697";

        var outcome = await h.Telegram(raw);

        Assert.True(raw.Length > 200);
        Assert.Equal(SubmitKind.Queued, outcome.Kind);
    }

    [Fact]
    public async Task Member_from_the_site_is_limited_too_and_the_group_hears_nothing()
    {
        using var h = Quiet();

        var outcome = await h.Service.SubmitAsync(
            Member(250), AlertSource.Site, SenderRole.Member, false, "u1", TestContext.Current.CancellationToken);

        Assert.Equal(SubmitKind.TooLong, outcome.Kind);
        Assert.Equal("texto grande demais", Assert.Single(h.Queue.History).Reason);
        Assert.Empty(h.Notifier.Sent);
    }

    [Fact]
    public async Task A_huge_member_message_with_missing_fields_is_told_it_is_too_long()
    {
        using var h = Quiet();

        var outcome = await h.Telegram(new string('x', 4000));

        Assert.Equal(SubmitKind.TooLong, outcome.Kind);
        Assert.Equal([TelegramReplies.TooLong(200)], h.Notifier.Sent);
        Assert.Equal("texto grande demais", Assert.Single(h.Queue.History).Reason);
    }

    [Fact]
    public async Task Operator_with_4000_letters_is_accepted()
    {
        using var h = Quiet();

        var outcome = await h.Pc(Member(4000));

        Assert.Equal(SubmitKind.Queued, outcome.Kind);
        Assert.Equal(4000, h.Queue.Pending[0].Text.Length);
        Assert.Empty(h.Notifier.Sent);
    }

    [Fact]
    public async Task Admin_from_the_site_with_4000_letters_is_accepted()
    {
        using var h = Quiet();

        var outcome = await h.Service.SubmitAsync(
            Member(4000), AlertSource.Site, SenderRole.Admin, false, "adm", TestContext.Current.CancellationToken);

        Assert.Equal(SubmitKind.Queued, outcome.Kind);
    }

    [Fact]
    public async Task Preview_reports_the_limit_for_a_member_and_not_for_the_operator()
    {
        using var h = Quiet();
        var raw = Member(201);

        var member = await h.Service.PreviewAsync(raw, SenderRole.Member, TestContext.Current.CancellationToken);
        var op = await h.Service.PreviewAsync(raw, SenderRole.Operator, TestContext.Current.CancellationToken);
        var exact = await h.Service.PreviewAsync(Member(200), SenderRole.Member, TestContext.Current.CancellationToken);

        Assert.True(member.TooLong);
        Assert.Equal(RuleKind.Reject, member.Kind);
        Assert.Equal(201, member.Text.Length);
        Assert.False(op.TooLong);
        Assert.Equal(RuleKind.Vehicle, op.Kind);
        Assert.False(exact.TooLong);
        Assert.Equal(RuleKind.Vehicle, exact.Kind);
        Assert.Empty(h.Queue.Pending); // a preview never queues or records anything
        Assert.Empty(h.Queue.History);
    }

    [Fact]
    public async Task A_cancelled_submit_throws_and_leaves_no_trace()
    {
        using var h = Quiet();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => h.Service.SubmitAsync(Good, AlertSource.Telegram, SenderRole.Member, false, null, cts.Token));

        Assert.Empty(h.Queue.Pending);
        Assert.Empty(h.Queue.History);
    }

    [Fact]
    public async Task Replay_queues_a_shown_alert_again_from_the_pc()
    {
        using var h = new ServiceHarness();
        await h.Telegram(Good);
        (await h.Display.NextPass(1)).Complete(true);
        await Wait.Until(() => h.Queue.History.Count == 1);
        var shown = h.Queue.History[0].Item;

        var outcome = h.Service.Replay(shown);

        Assert.Equal(SubmitKind.Queued, outcome.Kind);
        Assert.True(outcome.WasDuplicate);
        await h.Display.NextPass(2);
        Assert.Equal(shown.Text, h.Display.Texts[1]);
    }
}
