using AlertaIBT.App.Platform;
using AlertaIBT.App.Services;
using AlertaIBT.App.ViewModels;
using AlertaIBT.Core.Alerts;
using AlertaIBT.Core.Correction;
using AlertaIBT.Core.Queue;
using AlertaIBT.Core.Services;
using AlertaIBT.Core.Telegram;
using Microsoft.Extensions.Time.Testing;

namespace AlertaIBT.App.Tests.ViewModels;

public sealed class QuickPanelViewModelTests : IDisposable
{
    private readonly FakeTimeProvider _clock = new();
    private readonly StatusHub _hub = new();
    private readonly FakeGateway _gateway;
    private readonly QuickPanelViewModel _vm;
    private double? _progress;

    public QuickPanelViewModelTests()
    {
        _gateway = new FakeGateway(_clock);
        _vm = new QuickPanelViewModel(_gateway, _hub, () => _progress, clock: _clock);
    }

    public void Dispose() => _vm.Dispose();

    private static QueueItem Item(string id, string text, AlertSource source = AlertSource.Pc) =>
        new(id, text, null, source, false, DateTimeOffset.UnixEpoch, null, null);

    // ---- the preview, 700 ms after the last key ----

    [Fact]
    public async Task The_preview_is_asked_700_ms_after_the_last_key_and_not_before()
    {
        _vm.InputText = "prata corolla bra2e19 vidro abrto";

        _clock.Advance(TimeSpan.FromMilliseconds(699));
        Assert.Empty(_gateway.Previews);
        Assert.True(_vm.IsPreviewPending);

        _clock.Advance(TimeSpan.FromMilliseconds(1));
        await _vm.LastPreviewTask!;

        Assert.Equal(["prata corolla bra2e19 vidro abrto"], _gateway.Previews);
        Assert.False(_vm.IsPreviewPending);
        Assert.True(_vm.HasPreview);
        Assert.Equal("COROLLA PRATA | PLACA: BRA-2E19 | VIDRO ABERTO", _vm.Preview.Text);
        Assert.Equal(PreviewTone.Ok, _vm.Preview.Tone);
    }

    [Fact]
    public async Task Typing_again_restarts_the_wait_and_only_the_last_text_is_previewed()
    {
        _vm.InputText = "gol";
        _clock.Advance(TimeSpan.FromMilliseconds(600));
        _vm.InputText = "gol branco";
        _clock.Advance(TimeSpan.FromMilliseconds(600));
        Assert.Empty(_gateway.Previews);

        _clock.Advance(TimeSpan.FromMilliseconds(100));
        await _vm.LastPreviewTask!;

        Assert.Equal(["gol branco"], _gateway.Previews);
    }

    [Fact]
    public async Task A_blank_field_clears_the_preview_at_once_and_asks_for_nothing()
    {
        _vm.InputText = "gol branco";
        _clock.Advance(PreviewDelay);
        await _vm.LastPreviewTask!;
        Assert.True(_vm.HasPreview);

        _vm.InputText = "   ";

        Assert.False(_vm.HasPreview);
        Assert.False(_vm.IsPreviewPending);
        _clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Single(_gateway.Previews);
    }

    [Fact]
    public async Task A_preview_that_arrives_after_the_text_changed_is_dropped()
    {
        var slow = new TaskCompletionSource<PreviewResult>();
        _gateway.OnPreview = (_, _) => slow.Task;
        _vm.InputText = "gol";
        _clock.Advance(PreviewDelay);
        var first = _vm.LastPreviewTask!;

        _vm.InputText = "gol branco";
        slow.SetResult(FakeGateway.VehicleResult("OLD TEXT"));
        await first;

        Assert.False(_vm.HasPreview); // the old answer is not shown for the new text
        Assert.True(_vm.IsPreviewPending);
    }

    [Fact]
    public async Task Offline_correction_shows_the_blue_preview_and_a_duplicate_adds_the_warning()
    {
        _gateway.OnPreview = (_, _) => Task.FromResult(
            new PreviewResult("GOL BRANCO | PLACA: ABC-1234", RuleKind.Vehicle, [], true, CorrectorKind.Offline, 3));

        _vm.InputText = "gol branco abc1234";
        _clock.Advance(PreviewDelay);
        await _vm.LastPreviewTask!;

        Assert.Equal(PreviewTone.Info, _vm.Preview.Tone);
        Assert.True(_vm.HasPreviewNote);
        Assert.Contains("h\U000000E1 3 min", _vm.Preview.Note);
    }

    [Fact]
    public async Task A_preview_that_fails_shows_nothing_and_does_not_throw()
    {
        _gateway.OnPreview = (_, _) => throw new InvalidOperationException("boom");

        _vm.InputText = "gol";
        _clock.Advance(PreviewDelay);
        await _vm.LastPreviewTask!;

        Assert.False(_vm.HasPreview);
        Assert.False(_vm.IsPreviewPending);
    }

    // ---- sending ----

    [Fact]
    public async Task Enter_sends_the_trimmed_text_as_normal_and_a_queued_alert_empties_the_field()
    {
        _vm.InputText = "  prata corolla bra2e19 vidro abrto \n";

        await _vm.SendCommand.ExecuteAsync(null);

        Assert.Equal([("prata corolla bra2e19 vidro abrto", false)], _gateway.Submits);
        Assert.Equal(string.Empty, _vm.InputText);
        Assert.False(_vm.HasPreview);
        Assert.Equal("Na fila: 1\U000000BA", _vm.FeedbackText);
        Assert.Equal(FeedbackTone.Ok, _vm.FeedbackTone);
    }

    [Fact]
    public async Task Ctrl_Enter_sends_urgent()
    {
        _vm.InputText = "gol branco abc1234";

        await _vm.SendUrgentCommand.ExecuteAsync(null);

        Assert.Equal([("gol branco abc1234", true)], _gateway.Submits);
        Assert.Equal("Urgente na fila: 1\U000000BA", _vm.FeedbackText);
    }

    [Fact]
    public async Task Sending_an_empty_field_does_nothing()
    {
        _vm.InputText = "   ";

        await _vm.SendCommand.ExecuteAsync(null);

        Assert.Empty(_gateway.Submits);
        Assert.False(_vm.HasFeedback);
    }

    [Fact]
    public async Task During_the_sermon_the_text_stays_and_the_reason_is_shown()
    {
        _gateway.OnSubmit = (_, _) => Task.FromResult(new SubmitOutcome(SubmitKind.PausedRefused, "X", []));
        var rejected = 0;
        _vm.SendRejected += () => rejected++;
        _vm.InputText = "gol branco abc1234";

        await _vm.SendCommand.ExecuteAsync(null);

        Assert.Equal("gol branco abc1234", _vm.InputText);
        Assert.Equal(FeedbackTone.Warn, _vm.FeedbackTone);
        Assert.Contains("Prega\U000000E7\U000000E3o em andamento", _vm.FeedbackText);
        Assert.Equal(1, rejected);
    }

    [Fact]
    public async Task A_rejected_text_stays_and_the_red_preview_says_why()
    {
        _gateway.OnSubmit = (_, _) => Task.FromResult(new SubmitOutcome(SubmitKind.Rejected, string.Empty, ["placa"]));
        var rejected = 0;
        _vm.SendRejected += () => rejected++;
        _vm.InputText = "gol branco";

        await _vm.SendCommand.ExecuteAsync(null);

        Assert.Equal("gol branco", _vm.InputText);
        Assert.Equal(PreviewTone.Error, _vm.Preview.Tone);
        Assert.Equal("falta placa", _vm.Preview.Text);
        Assert.Equal(1, rejected);
    }

    [Fact]
    public async Task An_improper_text_is_refused_in_red()
    {
        _gateway.OnSubmit = (_, _) => Task.FromResult(new SubmitOutcome(SubmitKind.Refused, string.Empty, []));
        _vm.InputText = "xingamento";

        await _vm.SendCommand.ExecuteAsync(null);

        Assert.Equal("conte\U000000FAdo impr\U000000F3prio", _vm.Preview.Text);
        Assert.Equal("xingamento", _vm.InputText);
    }

    [Fact]
    public async Task A_service_that_throws_is_reported_and_never_escapes()
    {
        _gateway.OnSubmit = (_, _) => throw new InvalidOperationException("boom");
        _vm.InputText = "gol branco abc1234";

        await _vm.SendCommand.ExecuteAsync(null);

        Assert.Equal(FeedbackTone.Error, _vm.FeedbackTone);
        Assert.Equal("gol branco abc1234", _vm.InputText);
        Assert.False(_vm.IsSending);
    }

    [Fact]
    public async Task A_second_send_while_the_first_is_in_flight_is_ignored()
    {
        var gate = new TaskCompletionSource<SubmitOutcome>();
        _gateway.OnSubmit = (_, _) => gate.Task;
        _vm.InputText = "gol branco abc1234";

        var first = _vm.SendAsync(false);
        await _vm.SendAsync(true);
        gate.SetResult(new SubmitOutcome(SubmitKind.Queued, "X", [], 1));
        await first;

        Assert.Single(_gateway.Submits);
        Assert.False(_vm.IsSending);
    }

    [Fact]
    public async Task A_send_cancels_the_pending_preview()
    {
        _vm.InputText = "gol branco abc1234";

        await _vm.SendAsync(false);
        _clock.Advance(TimeSpan.FromSeconds(5));

        Assert.Empty(_gateway.Previews);
    }

    [Fact]
    public async Task The_message_under_the_field_goes_away_after_four_seconds_or_on_typing()
    {
        _vm.InputText = "gol branco abc1234";
        await _vm.SendAsync(false);
        Assert.True(_vm.HasFeedback);

        _clock.Advance(QuickPanelViewModel.FeedbackTime);

        Assert.False(_vm.HasFeedback);
        Assert.Equal(FeedbackTone.None, _vm.FeedbackTone);

        _gateway.OnSubmit = (_, _) => Task.FromResult(new SubmitOutcome(SubmitKind.PausedRefused, "X", []));
        _vm.InputText = "onix";
        await _vm.SendAsync(false);
        Assert.True(_vm.HasFeedback);
        _vm.InputText = "onix preto";
        Assert.False(_vm.HasFeedback);
    }

    // ---- keys and buttons ----

    [Fact]
    public void Esc_clears_the_field_first_and_closes_the_panel_when_it_is_already_empty()
    {
        var closed = 0;
        _vm.CloseRequested += () => closed++;
        _vm.InputText = "gol";

        _vm.Escape();
        Assert.Equal(string.Empty, _vm.InputText);
        Assert.Equal(0, closed);

        _vm.Escape();
        Assert.Equal(1, closed);
    }

    [Fact]
    public void Abrir_janela_asks_for_the_main_window_and_closes_the_panel()
    {
        var events = new List<string>();
        _vm.OpenMainWindowRequested += () => events.Add("window");
        _vm.CloseRequested += () => events.Add("close");

        _vm.OpenWindowCommand.Execute(null);

        Assert.Equal(["window", "close"], events);
    }

    [Fact]
    public void Showing_the_panel_asks_for_the_focus_and_forgets_the_old_message()
    {
        var focus = 0;
        _vm.FocusInputRequested += () => focus++;
        _vm.InputText = "gol";
        _gateway.OnSubmit = (_, _) => Task.FromResult(new SubmitOutcome(SubmitKind.PausedRefused, "X", []));
        _ = _vm.SendAsync(false);
        Assert.True(_vm.HasFeedback);

        _vm.OnShown();

        Assert.Equal(1, focus);
        Assert.False(_vm.HasFeedback);
        Assert.Equal("gol", _vm.InputText); // the draft stays
    }

    // ---- pause: one press, one toggle ----

    [Fact]
    public void One_press_of_the_pause_button_toggles_exactly_once_and_the_button_follows_the_hub()
    {
        Assert.Equal("Pausar barra", _vm.PauseTip);
        Assert.Equal("LucidePause", _vm.PauseIconKey);

        _vm.TogglePauseCommand.Execute(null);
        Assert.Equal(1, _gateway.Toggles);

        _hub.SetPaused(true); // what AlertService does after the toggle
        Assert.True(_vm.IsPaused);
        Assert.Equal("Retomar barra", _vm.PauseTip);
        Assert.Equal("LucidePlay", _vm.PauseIconKey);
        Assert.Equal("Pausada", _vm.PillText);
        Assert.Equal(StatusLight.Yellow, _vm.PillLight);
        Assert.Equal(1, _gateway.Toggles); // the pause state coming back did not toggle again

        _vm.TogglePauseCommand.Execute(null);
        Assert.Equal(2, _gateway.Toggles);
    }

    // ---- status line ----

    [Fact]
    public void The_three_icons_and_the_pill_follow_the_status_hub()
    {
        Assert.Equal(StatusLight.Gray, _vm.Telegram.Light);
        Assert.Equal(StatusLight.Gray, _vm.Site.Light);
        Assert.Equal(StatusLight.Red, _vm.Led.Light);
        Assert.Equal("LED desligado", _vm.PillText);
        Assert.Equal(StatusLight.Red, _vm.PillLight);

        _hub.SetTelegram(TelegramState.Online);
        _hub.SetLedPresent(true);

        Assert.Equal(StatusLight.Green, _vm.Telegram.Light);
        Assert.Equal(StatusLight.Green, _vm.Led.Light);
        Assert.Equal("Pronta", _vm.PillText);
        Assert.StartsWith("Telegram: Conectado", _vm.Telegram.Tip);

        _hub.SetShowing(true);
        Assert.Equal("Exibindo", _vm.PillText);
        Assert.Equal(StatusLight.Green, _vm.PillLight);
    }

    [Fact]
    public void The_icons_are_the_telegram_plane_the_globe_and_the_monitor()
    {
        Assert.Equal("LucideSend", _vm.Telegram.IconKey);
        Assert.Equal("LucideGlobe", _vm.Site.IconKey);
        Assert.Equal("LucideMonitor", _vm.Led.IconKey);
    }

    [Fact]
    public void Changes_from_other_threads_go_through_the_post_function()
    {
        var posted = new List<Action>();
        using var vm = new QuickPanelViewModel(_gateway, _hub, post: posted.Add, clock: _clock);
        posted.Clear();

        _hub.SetPaused(true);

        Assert.False(vm.IsPaused); // nothing changed until the UI thread runs the posted action
        Assert.NotEmpty(posted);
        foreach (var action in posted.ToArray())
        {
            action();
        }

        Assert.True(vm.IsPaused);
    }

    // ---- No telao agora ----

    [Fact]
    public void Nothing_on_the_bar_means_an_empty_message_area_and_an_empty_queue_line()
    {
        Assert.False(_vm.HasCurrent);
        Assert.Equal(string.Empty, _vm.CurrentText);
        Assert.Equal(string.Empty, _vm.CurrentSource);
        Assert.Equal("Fila vazia", _vm.QueueSummary);
        Assert.Equal(0, _vm.ProgressValue);
    }

    [Fact]
    public void The_alert_on_the_bar_shows_its_text_source_progress_and_the_next_one()
    {
        _gateway.Queue.Enqueue(Item("a", "GOL BRANCO | PLACA: AAA-1111", AlertSource.Telegram), DuplicatePolicy.Warn);
        _gateway.Queue.Enqueue(Item("b", "ONIX PRETO | PLACA: BBB-2222"), DuplicatePolicy.Warn);
        _gateway.Queue.Enqueue(Item("c", "FOX PRATA | PLACA: CCC-3333"), DuplicatePolicy.Warn);
        _gateway.Queue.StartNext();

        Assert.True(_vm.HasCurrent);
        Assert.Equal("GOL BRANCO | PLACA: AAA-1111", _vm.CurrentText);
        Assert.Equal("TELEGRAM", _vm.CurrentSource);
        Assert.Equal("Fila: 2 \U000000B7 pr\U000000F3xima: ONIX PRETO | PLACA: BBB-2222", _vm.QueueSummary);

        _progress = 0.4;
        _vm.RefreshProgress();
        Assert.Equal(0.4, _vm.ProgressValue);

        _progress = 7;
        _vm.RefreshProgress();
        Assert.Equal(1, _vm.ProgressValue); // held in range

        _gateway.Queue.FinishCurrent();
        Assert.False(_vm.HasCurrent);
        Assert.Equal(string.Empty, _vm.CurrentText);
        Assert.Equal(0, _vm.ProgressValue);
    }

    // ---- the hotkey ----

    [Fact]
    public void A_hotkey_that_cannot_be_registered_is_explained_in_the_panel()
    {
        var shortcut = new QuickPanelHotkey(new FakeRegistrar { Accept = false });
        shortcut.Apply("Ctrl+Alt+Space");

        _vm.ApplyHotkey(shortcut.Status);

        Assert.True(_vm.HasHotkeyMessage);
        Assert.Contains("Ctrl+Alt+Espa\U000000E7o", _vm.HotkeyMessage);
        Assert.Contains("\U000000EDcone da bandeja", _vm.HotkeyMessage);
        Assert.Contains("Enter: envia", _vm.InfoTip);
        Assert.Contains("n\U000000E3o est\U000000E1 dispon\U000000EDvel", _vm.InfoTip);
    }

    [Fact]
    public void A_working_hotkey_shows_no_message_and_is_listed_in_the_hints()
    {
        var shortcut = new QuickPanelHotkey(new FakeRegistrar());
        shortcut.Apply("Ctrl+Alt+Space");

        _vm.ApplyHotkey(shortcut.Status);

        Assert.False(_vm.HasHotkeyMessage);
        Assert.Equal(
            "Enter: envia\nCtrl+Enter: urgente (fura a fila)\nEsc: limpa\nCtrl+Alt+Espa\U000000E7o: abre este painel de qualquer programa",
            _vm.InfoTip);
    }

    private static TimeSpan PreviewDelay => QuickPanelViewModel.PreviewDelay;

    private sealed class FakeRegistrar : IHotkeyRegistrar
    {
        public bool Accept { get; set; } = true;

        public bool Register(AlertaIBT.Core.Input.HotkeyGesture gesture) => Accept;

        public void Unregister()
        {
        }
    }
}

internal sealed class FakeGateway(TimeProvider clock) : IAlertGateway
{
    public AlertQueue Queue { get; } = new(clock);

    public List<string> Previews { get; } = [];

    public List<(string Raw, bool Urgent)> Submits { get; } = [];

    public int Toggles { get; private set; }

    public Func<string, CancellationToken, Task<PreviewResult>> OnPreview { get; set; } =
        (_, _) => Task.FromResult(VehicleResult("COROLLA PRATA | PLACA: BRA-2E19 | VIDRO ABERTO"));

    public Func<string, bool, Task<SubmitOutcome>> OnSubmit { get; set; } =
        (_, _) => Task.FromResult(new SubmitOutcome(SubmitKind.Queued, "X", [], 1, Id: "id"));

    public static PreviewResult VehicleResult(string text) => new(text, RuleKind.Vehicle, [], false, CorrectorKind.Gemini);

    public Task<PreviewResult> PreviewAsync(string raw, CancellationToken ct)
    {
        Previews.Add(raw);
        return OnPreview(raw, ct);
    }

    public Task<SubmitOutcome> SubmitAsync(string raw, bool urgent, CancellationToken ct)
    {
        Submits.Add((raw, urgent));
        return OnSubmit(raw, urgent);
    }

    public void TogglePause() => Toggles++;

    public int Skips { get; private set; }

    public List<(string Id, int Delta)> Moves { get; } = [];

    public List<string> Removes { get; } = [];

    public int Clears { get; private set; }

    public List<QueueItem> Replays { get; } = [];

    public Func<QueueItem, SubmitOutcome> OnReplay { get; set; } =
        item => new SubmitOutcome(SubmitKind.Queued, item.Text, [], 1, Id: "again");

    public void Skip() => Skips++;

    public bool Move(string id, int delta)
    {
        Moves.Add((id, delta));
        return Queue.Move(id, delta);
    }

    public bool Remove(string id)
    {
        Removes.Add(id);
        return Queue.Remove(id);
    }

    public void ClearQueue()
    {
        Clears++;
        Queue.Clear();
    }

    public SubmitOutcome Replay(QueueItem shown)
    {
        Replays.Add(shown);
        return OnReplay(shown);
    }

    public int HistoryClears { get; private set; }

    /// <summary>What the clear answers (the PC history is really emptied before it is asked).</summary>
    public Func<int, Task<HistoryClearResult>> OnClearHistory { get; set; } =
        pcEntries => Task.FromResult(new HistoryClearResult(pcEntries, SiteClearOutcome.Cleared, 9, 7, 0));

    public Task<HistoryClearResult> ClearHistoryAsync(CancellationToken ct)
    {
        HistoryClears++;
        return OnClearHistory(Queue.ClearHistory());
    }
}
