using AlertaIBT.App.ViewModels;
using AlertaIBT.Core.Alerts;
using AlertaIBT.Core.Queue;
using AlertaIBT.Core.Services;
using Microsoft.Extensions.Time.Testing;

namespace AlertaIBT.App.Tests.ViewModels;

public sealed class HomeViewModelTests : IDisposable
{
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero));
    private readonly StatusHub _hub = new();
    private readonly FakeGateway _gateway;
    private readonly QuickPanelViewModel _composer;
    private readonly HomeViewModel _vm;

    public HomeViewModelTests()
    {
        _gateway = new FakeGateway(_clock);
        _composer = new QuickPanelViewModel(_gateway, _hub, clock: _clock);
        _vm = new HomeViewModel(_gateway, _composer, clock: _clock);
    }

    public void Dispose()
    {
        _vm.Dispose();
        _composer.Dispose();
    }

    private QueueItem Item(string id, string text, AlertSource source = AlertSource.Pc, bool urgent = false, string? original = null, bool corrected = false) =>
        new(id, text, null, source, urgent, _clock.GetUtcNow(), null, original, corrected);

    private void Enqueue(QueueItem item) => _gateway.Queue.Enqueue(item, DuplicatePolicy.Warn);

    // ---- what is on the bar ----

    [Fact]
    public void Nothing_on_the_bar_means_no_text_and_no_original_line()
    {
        Assert.False(_composer.HasCurrent);
        Assert.Equal(string.Empty, _composer.CurrentText);
        Assert.Equal(string.Empty, _vm.OriginalLine);
        Assert.False(_vm.HasOriginal);
        Assert.True(_vm.QueueIsEmpty);
        Assert.True(_vm.HistoryIsEmpty);
        Assert.Equal("Fila", _vm.QueueTitle);
    }

    [Fact]
    public void A_corrected_alert_shows_the_corrigido_original_line()
    {
        Enqueue(Item("a", "CRETA PRETO | PLACA: GBR-9003 | FAROL ACESO", AlertSource.Telegram, original: "#creta preto | placa:gbr- 9003 / farol acesso", corrected: true));

        _gateway.Queue.StartNext();

        Assert.Equal("Corrigido. Original: #creta preto | placa:gbr- 9003 / farol acesso", _vm.OriginalLine);
        Assert.True(_vm.HasOriginal);
        Assert.Equal("CRETA PRETO | PLACA: GBR-9003 | FAROL ACESO", _composer.CurrentText);
        Assert.Equal("TELEGRAM", _composer.CurrentSource);
    }

    [Fact]
    public void An_alert_the_correction_left_alone_has_no_original_line()
    {
        Enqueue(Item("a", "ONIX PRETO | PLACA: QWE-1B23", original: "ONIX PRETO | PLACA: QWE-1B23", corrected: false));

        _gateway.Queue.StartNext();

        Assert.False(_vm.HasOriginal);
    }

    [Fact]
    public void The_original_line_goes_away_when_the_alert_ends()
    {
        Enqueue(Item("a", "X", original: "x", corrected: true));
        _gateway.Queue.StartNext();
        Assert.True(_vm.HasOriginal);

        _gateway.Queue.FinishCurrent();

        Assert.False(_vm.HasOriginal);
    }

    // ---- the buttons ----

    [Fact]
    public void Proxima_and_Encerrar_atual_both_end_the_alert_on_the_bar()
    {
        _vm.SkipCommand.Execute(null);
        _vm.SkipCommand.Execute(null);

        Assert.Equal(2, _gateway.Skips);
    }

    [Fact]
    public void The_pause_button_is_the_composers_and_one_press_is_one_toggle()
    {
        _composer.TogglePauseCommand.Execute(null);

        Assert.Equal(1, _gateway.Toggles);

        _hub.SetPaused(true);
        Assert.True(_composer.IsPaused);
        Assert.Equal("Retomar barra", _composer.PauseTip);
        Assert.Equal("LucidePlay", _composer.PauseIconKey);
    }

    [Fact]
    public async Task The_message_field_previews_and_sends_like_the_quick_panel()
    {
        _composer.InputText = "prata corolla bra2e19 vidro abrto";
        _clock.Advance(QuickPanelViewModel.PreviewDelay);
        await _composer.LastPreviewTask!;

        Assert.True(_composer.HasPreview);
        Assert.Equal("COROLLA PRATA | PLACA: BRA-2E19 | VIDRO ABERTO", _composer.Preview.Text);

        await _composer.SendCommand.ExecuteAsync(null);
        _composer.InputText = "gol branco abc1234";
        await _composer.SendUrgentCommand.ExecuteAsync(null);

        Assert.Equal([("prata corolla bra2e19 vidro abrto", false), ("gol branco abc1234", true)], _gateway.Submits);
    }

    [Fact]
    public void Limpar_empties_the_field()
    {
        _composer.InputText = "gol branco";

        _composer.ClearCommand.Execute(null);

        Assert.Equal(string.Empty, _composer.InputText);
    }

    // ---- the queue ----

    [Fact]
    public void The_queue_lists_the_waiting_alerts_with_their_origin_in_order()
    {
        Enqueue(Item("a", "AAA", AlertSource.Telegram));
        Enqueue(Item("b", "BBB", AlertSource.Site));
        Enqueue(Item("c", "CCC", AlertSource.Pc, urgent: false));

        Assert.Equal(["AAA", "BBB", "CCC"], _vm.Queue.Select(r => r.Text));
        Assert.Equal(["TELEGRAM", "SITE", "PC"], _vm.Queue.Select(r => r.SourceName));
        Assert.Equal("Fila (3)", _vm.QueueTitle);
        Assert.False(_vm.QueueIsEmpty);
    }

    [Fact]
    public void An_urgent_alert_is_marked_and_waits_at_the_front()
    {
        Enqueue(Item("a", "AAA"));
        Enqueue(Item("u", "UUU", urgent: true));

        Assert.Equal(["UUU", "AAA"], _vm.Queue.Select(r => r.Text));
        Assert.True(_vm.Queue[0].Urgent);
    }

    [Fact]
    public void The_arrows_move_a_row_and_the_ends_cannot_go_further()
    {
        Enqueue(Item("a", "AAA"));
        Enqueue(Item("b", "BBB"));
        Enqueue(Item("c", "CCC"));
        Assert.False(_vm.Queue[0].CanMoveUp);
        Assert.True(_vm.Queue[0].CanMoveDown);
        Assert.True(_vm.Queue[2].CanMoveUp);
        Assert.False(_vm.Queue[2].CanMoveDown);
        Assert.False(_vm.Queue[0].MoveUpCommand.CanExecute(null));

        _vm.Queue[0].MoveDownCommand.Execute(null);
        Assert.Equal(["BBB", "AAA", "CCC"], _vm.Queue.Select(r => r.Text));

        _vm.Queue[2].MoveUpCommand.Execute(null);
        Assert.Equal(["BBB", "CCC", "AAA"], _vm.Queue.Select(r => r.Text));
        Assert.Equal([("a", 1), ("c", -1)], _gateway.Moves);
    }

    [Fact]
    public void The_cross_removes_a_row_and_the_history_says_it_was_removed_on_the_pc()
    {
        Enqueue(Item("a", "AAA"));
        Enqueue(Item("b", "BBB"));

        _vm.Queue[0].RemoveCommand.Execute(null);

        Assert.Equal(["BBB"], _vm.Queue.Select(r => r.Text));
        Assert.Equal(["a"], _gateway.Removes);
        var row = Assert.Single(_vm.History);
        Assert.Equal(HistoryReasons.RemovedOnPc, row.Reason);
        Assert.Equal("LucideBan", row.IconKey);
        Assert.False(row.CanReplay);
    }

    [Fact]
    public void Clearing_the_queue_deletes_directly_without_any_question()
    {
        Enqueue(Item("a", "AAA"));
        Enqueue(Item("b", "BBB"));
        _gateway.Queue.StartNext(); // one on the bar, one waiting
        Enqueue(Item("c", "CCC"));

        _vm.ClearQueueCommand.Execute(null);

        Assert.Equal(1, _gateway.Clears);
        Assert.Empty(_vm.Queue);
        Assert.True(_vm.QueueIsEmpty);
        Assert.True(_composer.HasCurrent); // the one on the bar keeps going
        Assert.Equal(2, _vm.History.Count);
        Assert.All(_vm.History, r => Assert.Equal(HistoryReasons.QueueCleared, r.Reason));
    }

    // ---- the history ----

    [Fact]
    public void A_shown_alert_has_the_green_check_no_reason_and_the_replay_button()
    {
        Enqueue(Item("a", "AAA", AlertSource.Telegram));
        _gateway.Queue.StartNext();
        _gateway.Queue.FinishCurrent();

        var row = Assert.Single(_vm.History);

        Assert.Equal("LucideCircleCheck", row.IconKey);
        Assert.Equal(HistoryTone.Ok, row.Tone);
        Assert.Equal(string.Empty, row.Reason);
        Assert.False(row.HasReason);
        Assert.True(row.CanReplay);
        Assert.Equal("TELEGRAM", row.SourceName);
        Assert.Equal(_clock.GetUtcNow().ToLocalTime().ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture), row.Time); // the PC's own clock
    }

    [Fact]
    public void A_refused_alert_has_the_red_x_and_why_and_no_replay()
    {
        _gateway.Queue.AddRejected(Item("r", "gol branco farol aceso", AlertSource.Telegram), HistoryReasons.MissingFields(["placa"]));

        var row = Assert.Single(_vm.History);

        Assert.Equal("LucideCircleX", row.IconKey);
        Assert.Equal(HistoryTone.Bad, row.Tone);
        Assert.Equal("faltou placa", row.Reason);
        Assert.True(row.HasReason);
        Assert.False(row.CanReplay);
        Assert.False(row.ReplayCommand.CanExecute(null));
        Assert.Equal("Recusado: faltou placa", row.IconTip);
        Assert.Equal("gol branco farol aceso", row.Text);
    }

    [Fact]
    public void An_expired_alert_says_expirou()
    {
        Enqueue(Item("a", "AAA"));
        _clock.Advance(TimeSpan.FromMinutes(31));
        _gateway.Queue.ExpireOld();

        var row = Assert.Single(_vm.History);

        Assert.Equal("LucideClock", row.IconKey);
        Assert.Equal("expirou", row.Reason);
        Assert.False(row.CanReplay);
    }

    [Fact]
    public void The_history_holds_fifty_newest_first()
    {
        for (var i = 0; i < 60; i++)
        {
            _gateway.Queue.AddRejected(Item($"r{i}", $"texto {i}"), HistoryReasons.Sermon);
        }

        Assert.Equal(50, _vm.History.Count);
        Assert.Equal("texto 59", _vm.History[0].Text);
        Assert.Equal("texto 10", _vm.History[49].Text);
    }

    [Fact]
    public void Exibir_de_novo_queues_the_shown_alert_again_and_says_where()
    {
        Enqueue(Item("a", "AAA", AlertSource.Telegram));
        _gateway.Queue.StartNext();
        _gateway.Queue.FinishCurrent();
        var shown = _vm.History[0];

        shown.ReplayCommand.Execute(null);

        Assert.Equal(["a"], _gateway.Replays.Select(i => i.Id));
        Assert.Equal("Na fila: 1\U000000BA", _vm.NoticeText);
        Assert.Equal(FeedbackTone.Ok, _vm.NoticeTone);
        Assert.True(_vm.HasNotice);
    }

    [Fact]
    public void Exibir_de_novo_during_the_sermon_says_so()
    {
        _gateway.OnReplay = item => new SubmitOutcome(SubmitKind.PausedRefused, item.Text, []);
        Enqueue(Item("a", "AAA"));
        _gateway.Queue.StartNext();
        _gateway.Queue.FinishCurrent();

        _vm.History[0].ReplayCommand.Execute(null);

        Assert.Equal(FeedbackTone.Warn, _vm.NoticeTone);
        Assert.Contains("Prega\U000000E7\U000000E3o em andamento", _vm.NoticeText);
    }

    [Fact]
    public void The_notice_goes_away_after_four_seconds()
    {
        Enqueue(Item("a", "AAA"));
        _gateway.Queue.StartNext();
        _gateway.Queue.FinishCurrent();
        _vm.History[0].ReplayCommand.Execute(null);

        _clock.Advance(TimeSpan.FromMilliseconds(3999));
        Assert.True(_vm.HasNotice);
        _clock.Advance(TimeSpan.FromMilliseconds(1));

        Assert.False(_vm.HasNotice);
    }

    [Fact]
    public void A_replay_that_throws_is_reported_in_the_notice_and_never_crashes()
    {
        _gateway.OnReplay = _ => throw new InvalidOperationException("boom");
        Enqueue(Item("a", "AAA"));
        _gateway.Queue.StartNext();
        _gateway.Queue.FinishCurrent();

        _vm.History[0].ReplayCommand.Execute(null);

        Assert.Equal(FeedbackTone.Error, _vm.NoticeTone);
    }

    [Fact]
    public void A_corrected_row_carries_the_wand_tooltip_with_the_original()
    {
        Enqueue(Item("a", "COROLLA PRATA", original: "corola prata", corrected: true));
        Enqueue(Item("b", "GOL", original: "gol", corrected: false));

        Assert.Equal("Corrigido\nOriginal: corola prata", _vm.Queue[0].CorrectionTip);
        Assert.True(_vm.Queue[0].WasCorrected);
        Assert.Null(_vm.Queue[1].CorrectionTip);
        Assert.False(_vm.Queue[1].WasCorrected);
    }

    [Fact]
    public void Changes_from_other_threads_reach_the_lists_through_the_post_function()
    {
        var posted = new List<Action>();
        using var home = new HomeViewModel(_gateway, _composer, posted.Add, _clock);
        posted.Clear();

        Enqueue(Item("a", "AAA"));

        Assert.Empty(home.Queue); // not touched on the caller's thread
        Assert.NotEmpty(posted);
        posted.ForEach(a => a());
        Assert.Single(home.Queue);
    }

    [Fact]
    public void OnShown_looks_at_everything_again_and_asks_for_the_focus()
    {
        var focus = 0;
        _composer.FocusInputRequested += () => focus++;

        _vm.OnShown();

        Assert.Equal(1, focus);
    }
}
