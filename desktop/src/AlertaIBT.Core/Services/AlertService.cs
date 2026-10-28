using System.Threading.Channels;
using AlertaIBT.Core.Alerts;
using AlertaIBT.Core.Correction;
using AlertaIBT.Core.Queue;
using AlertaIBT.Core.Settings;
using AlertaIBT.Core.Telegram;
using Microsoft.Extensions.Logging;

namespace AlertaIBT.Core.Services;

/// <summary>
/// The heart of the app: every alert from every source (the PC panel, the Telegram group, later the site) goes through
/// <see cref="SubmitAsync"/>: correct, apply the rules, queue; and a background loop plays the queue on the bar one alert at a
/// time. It tells the Telegram group what happened to each alert, handles the sermon pause, the LED going off and coming back,
/// and the 30 minute expiry.
/// </summary>
/// <remarks>
/// <para>
/// The sermon (<see cref="Pause"/>, from the Pause button or from Holyrics through the local API) cancels the alert on the bar and
/// the whole queue at once, tells the group once, and makes new alerts answer <see cref="SubmitKind.PausedRefused"/> until
/// <see cref="Resume"/> (silent). The pause is never saved.
/// </para>
/// <para>
/// The queue itself records duplicates and pause refusals in the history; this service only records what the queue does not see
/// (missing fields, improper content). When the LED is off or disappears, a Telegram sender is told once per alert and the
/// alert waits; an alert cut short by the LED going away goes back to the front of the queue.
/// </para>
/// <para>
/// Nothing here throws to the callers of the background parts: errors go to the log and to <see cref="StatusHub.ReportError"/>.
/// The queue raises <see cref="AlertQueue.Changed"/> on whatever thread made the change, so a listener must hop to its own thread.
/// </para>
/// </remarks>
public sealed class AlertService : IOverlayControl, IDisposable
{
    /// <summary>How often the 30 minute expiry looks at the queue.</summary>
    public static readonly TimeSpan ExpireInterval = TimeSpan.FromSeconds(60);

    /// <summary>
    /// An expired alert is only announced to its sender when it is not older than this: after a long stop (a restart a
    /// week later) nobody is waiting for the answer any more.
    /// </summary>
    public static readonly TimeSpan NotifyExpiredWithin = TimeSpan.FromHours(2);

    private static readonly TimeSpan FirstRetry = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan MaxRetry = TimeSpan.FromSeconds(10);
    private static readonly string[] AllRequired = ["modelo", "cor", "placa"];

    private readonly AlertQueue _queue;
    private readonly CorrectionPipeline _pipeline;
    private readonly IAlertDisplay _display;
    private readonly IGroupNotifier _notifier;
    private readonly Func<AppSettings> _settings;
    private readonly StatusHub _hub;
    private readonly TimeProvider _clock;
    private readonly ILogger _logger;
    private readonly Func<string> _newId;
    private readonly ProfanityFilter _filter;

    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });

    private readonly object _playGate = new();
    private CancellationTokenSource? _playCts;
    private StopReason _stopReason;

    private readonly object _toldGate = new();
    private readonly HashSet<string> _ledOffTold = [];

    private CancellationTokenSource? _stop;
    private Task? _loop;
    private ITimer? _expireTimer;
    private int _started;
    private int _hooked;
    private bool _disposed;

    /// <param name="queue">The queue (its state is kept on disk by <see cref="QueueStore"/>).</param>
    /// <param name="pipeline">Corrects the text.</param>
    /// <param name="display">The bar over the LED.</param>
    /// <param name="notifier">Writes to the Telegram group.</param>
    /// <param name="settings">Gives the current settings (the correction mode is read for every alert).</param>
    /// <param name="hub">Receives the status of the parts this service knows.</param>
    /// <param name="logger">Receives errors and the main events.</param>
    /// <param name="clock">The source of time (the expiry timer, retry delays and the age of alerts).</param>
    /// <param name="newId">Makes the id of a new alert; a random GUID by default.</param>
    /// <param name="filter">
    /// Refuses vulgar text for every sender, before and after the correction; the built-in word list alone by default.
    /// </param>
    public AlertService(
        AlertQueue queue,
        CorrectionPipeline pipeline,
        IAlertDisplay display,
        IGroupNotifier notifier,
        Func<AppSettings> settings,
        StatusHub hub,
        ILogger logger,
        TimeProvider? clock = null,
        Func<string>? newId = null,
        ProfanityFilter? filter = null)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(display);
        ArgumentNullException.ThrowIfNull(notifier);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(hub);
        ArgumentNullException.ThrowIfNull(logger);

        _queue = queue;
        _pipeline = pipeline;
        _display = display;
        _notifier = notifier;
        _settings = settings;
        _hub = hub;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
        _newId = newId ?? (() => Guid.NewGuid().ToString("N"));
        _filter = filter ?? new ProfanityFilter();
    }

    private enum StopReason
    {
        None,
        Pause,
        Skip,
    }

    /// <summary>The queue, for the lists of the windows (pending, history, <see cref="AlertQueue.Changed"/>, move and remove).</summary>
    public AlertQueue Queue => _queue;

    /// <summary>Whether the overlay is paused (the sermon).</summary>
    public bool IsPaused => _queue.IsPaused;

    /// <summary>
    /// Starts the background loop and the expiry timer. Alerts restored from disk that waited too long expire now. It can be called again
    /// after <see cref="StopAsync"/> (the master switch "Alertas ligados"): the queue is kept, so the alerts that waited play again.
    /// </summary>
    public void Start()
    {
        if (_disposed || Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }

        Hook();

        // The alerts restored from disk were already told about the LED before the restart.
        foreach (var item in _queue.ToSnapshot().Pending)
        {
            Tell(item.Id);
        }

        _hub.SetLedPresent(_display.LedPresent);
        _hub.SetPaused(_queue.IsPaused);
        ExpireOld(atStart: true);

        _stop = new CancellationTokenSource();
        var stop = _stop.Token;
        _expireTimer = _clock.CreateTimer(_ => ExpireOld(atStart: false), null, ExpireInterval, ExpireInterval);
        _loop = Task.Run(() => RunLoopAsync(stop), CancellationToken.None);
        Wake();
    }

    /// <summary>
    /// Stops the loop. The alert on the bar is cut short and goes back to the front of the queue, so it is saved and shown
    /// after the next start.
    /// </summary>
    public async Task StopAsync()
    {
        Unhook();
        _expireTimer?.Dispose();
        _expireTimer = null;
        var stop = _stop;
        if (stop is null)
        {
            return;
        }

        try
        {
            await stop.CancelAsync().ConfigureAwait(false);
            if (_loop is { } loop)
            {
                await loop.ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Fail("parar o servico de avisos", ex);
        }
        finally
        {
            // Ready for another Start (the master switch turned on again).
            _loop = null;
            _stop = null;
            stop.Dispose();
            Volatile.Write(ref _started, 0);
        }
    }

    private void Hook()
    {
        if (Interlocked.Exchange(ref _hooked, 1) != 0)
        {
            return;
        }

        _queue.Changed += OnQueueChanged;
        _display.LedPresenceChanged += OnLedPresenceChanged;
        _pipeline.GeminiHealthChanged += OnGeminiHealthChanged;
    }

    private void Unhook()
    {
        if (Interlocked.Exchange(ref _hooked, 0) == 0)
        {
            return;
        }

        _queue.Changed -= OnQueueChanged;
        _display.LedPresenceChanged -= OnLedPresenceChanged;
        _pipeline.GeminiHealthChanged -= OnGeminiHealthChanged;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Unhook();
        try
        {
            StopAsync().Wait(TimeSpan.FromSeconds(3));
        }
        catch (Exception ex)
        {
            Fail("encerrar o servico de avisos", ex);
        }
    }

    /// <summary>
    /// Corrects, checks and queues an alert, and answers its sender in the Telegram group when it came from there. The same
    /// text that <see cref="PreviewAsync"/> shows is the one queued.
    /// </summary>
    /// <param name="raw">What the person typed.</param>
    /// <param name="source">Where it came from.</param>
    /// <param name="role">Who sent it (a member needs model, color and plate; a repeated plate is refused for a member only).</param>
    /// <param name="urgent">Whether it jumps ahead of the normal alerts.</param>
    /// <param name="senderId">Who sent it (Telegram user id, site user), or <c>null</c>.</param>
    /// <param name="ct">Cancels the correction; the alert is then neither queued nor answered.</param>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    public async Task<SubmitOutcome> SubmitAsync(
        string raw,
        AlertSource source,
        SenderRole role,
        bool urgent,
        string? senderId,
        CancellationToken ct = default,
        string? itemId = null)
    {
        ArgumentNullException.ThrowIfNull(raw);
        ct.ThrowIfCancellationRequested();

        var policy = role == SenderRole.Member ? DuplicatePolicy.Block : DuplicatePolicy.Warn;

        // The sermon: refuse at once, without spending the Gemini quota or making the sender wait for the correction.
        if (_queue.IsPaused)
        {
            var refused = NewItem(Display(raw), null, source, urgent, senderId, raw, itemId);
            _queue.AddRejected(refused, HistoryReasons.Sermon);
            Reply(source, TelegramReplies.PausedRefused);
            return new SubmitOutcome(SubmitKind.PausedRefused, refused.Text, []);
        }

        var evaluation = await EvaluateAsync(raw, role, ct).ConfigureAwait(false);
        var by = evaluation.Result.By;
        var changed = evaluation.Result.Changed;

        if (evaluation.Result.Refused)
        {
            _queue.AddRejected(NewItem(Display(raw), null, source, urgent, senderId, raw, itemId), HistoryReasons.ImproperContent);
            Reply(source, TelegramReplies.Refused);
            if (evaluation.Blocked)
            {
                // Never the text itself: only that the filter acted, and for whom.
                _logger.LogInformation("Filtro de palavroes bloqueou um aviso ({Source}, remetente {SenderId})", source, senderId ?? "-");
            }
            else
            {
                _logger.LogInformation("Aviso recusado como improprio ({Source})", source);
            }

            return new SubmitOutcome(SubmitKind.Refused, string.Empty, [], By: by);
        }

        if (evaluation.TooLong)
        {
            _queue.AddRejected(NewItem(evaluation.Text, evaluation.Plate, source, urgent, senderId, raw, itemId), HistoryReasons.TooLong);
            Reply(source, TelegramReplies.TooLong(AlertRules.MemberMaxLength));
            _logger.LogInformation("Aviso grande demais recusado ({Source}, {Length} letras)", source, evaluation.Text.Length);
            return new SubmitOutcome(SubmitKind.TooLong, evaluation.Text, [], By: by, Changed: changed);
        }

        if (evaluation.Decision.Kind == RuleKind.Reject)
        {
            var missing = evaluation.Decision.Missing.Count > 0 ? evaluation.Decision.Missing : AllRequired;
            var text = evaluation.Text.Length > 0 ? evaluation.Text : raw.Trim();
            _queue.AddRejected(NewItem(text, evaluation.Result.Alert.Plate, source, urgent, senderId, raw, itemId), HistoryReasons.MissingFields(missing));
            Reply(source, TelegramReplies.Missing(missing));
            return new SubmitOutcome(SubmitKind.Rejected, evaluation.Text, missing, By: by, Changed: changed);
        }

        var item = NewItem(evaluation.Text, evaluation.Plate, source, urgent, senderId, raw, itemId) with { Corrected = changed };
        var enqueued = _queue.Enqueue(item, policy);
        switch (enqueued.Kind)
        {
            case EnqueueKind.PausedRefused:
                // Paused while the text was being corrected: the queue wrote the history itself.
                Reply(source, TelegramReplies.PausedRefused);
                return new SubmitOutcome(SubmitKind.PausedRefused, item.Text, [], By: by, Changed: changed);

            case EnqueueKind.DuplicateBlocked:
                Reply(
                    source,
                    enqueued.DuplicateMinutesAgo is { } minutes
                        ? TelegramReplies.DuplicateShown(minutes)
                        : TelegramReplies.DuplicateQueued);
                return new SubmitOutcome(
                    SubmitKind.Duplicate,
                    item.Text,
                    [],
                    DuplicateMinutesAgo: enqueued.DuplicateMinutesAgo,
                    WasDuplicate: true,
                    By: by,
                    Changed: changed);

            default:
                if (!_display.LedPresent)
                {
                    TellLedOff([item]);
                }

                _logger.LogInformation("Aviso na fila ({Source}, posicao {Position}): {Text}", source, enqueued.Position, item.Text);
                return new SubmitOutcome(
                    SubmitKind.Queued,
                    item.Text,
                    [],
                    enqueued.Position,
                    enqueued.DuplicateMinutesAgo,
                    enqueued.WasDuplicate,
                    item.Id,
                    by,
                    changed);
        }
    }

    /// <summary>
    /// What an alert would turn into, without sending it. It runs the very same correction as <see cref="SubmitAsync"/> (the
    /// pipeline remembers Gemini's answer for two minutes), so the text shown is the text queued.
    /// </summary>
    /// <param name="raw">What the person typed.</param>
    /// <param name="role">Who is typing.</param>
    /// <param name="ct">Cancels the correction.</param>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    public async Task<PreviewResult> PreviewAsync(string raw, SenderRole role, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(raw);

        var evaluation = await EvaluateAsync(raw, role, ct).ConfigureAwait(false);
        if (evaluation.Result.Refused)
        {
            return new PreviewResult(string.Empty, RuleKind.Reject, [], false, evaluation.Result.By, Refused: true);
        }

        var duplicate = _queue.CheckDuplicate(evaluation.Plate);
        return new PreviewResult(
            evaluation.Text,
            evaluation.TooLong ? RuleKind.Reject : evaluation.Decision.Kind,
            evaluation.Decision.Missing,
            duplicate.IsDuplicate,
            evaluation.Result.By,
            duplicate.MinutesAgo,
            evaluation.Result.Changed,
            TooLong: evaluation.TooLong);
    }

    /// <summary>Shows a past alert again ("Exibir de novo"): a new alert with the same text, from the PC, repeats allowed.</summary>
    /// <param name="shown">The alert to repeat.</param>
    /// <param name="urgent">Whether it jumps ahead of the normal alerts.</param>
    public SubmitOutcome Replay(QueueItem shown, bool urgent = false)
    {
        ArgumentNullException.ThrowIfNull(shown);

        var item = NewItem(shown.Text, shown.Plate, AlertSource.Pc, urgent, null, shown.Original ?? shown.Text) with { Corrected = shown.Corrected };
        var enqueued = _queue.Enqueue(item, DuplicatePolicy.Warn);
        return enqueued.Kind == EnqueueKind.PausedRefused
            ? new SubmitOutcome(SubmitKind.PausedRefused, item.Text, [])
            : new SubmitOutcome(
                SubmitKind.Queued,
                item.Text,
                [],
                enqueued.Position,
                enqueued.DuplicateMinutesAgo,
                enqueued.WasDuplicate,
                item.Id);
    }

    /// <summary>
    /// The sermon began: cancels the alert on the bar and the whole queue, refuses new alerts, and tells the group once.
    /// Does nothing when already paused.
    /// </summary>
    public void Pause()
    {
        if (!_queue.TryPause(out var cancelled))
        {
            return;
        }

        CancelPass(StopReason.Pause);
        _hub.SetPaused(true);
        Forget(cancelled);
        _notifier.Notify(TelegramReplies.Paused);
        _logger.LogInformation("Overlay pausado (pregacao): {Count} aviso(s) cancelado(s)", cancelled.Count);
    }

    /// <summary>The sermon ended: accepts alerts again, with the queue empty. Silent. Does nothing when not paused.</summary>
    public void Resume()
    {
        if (!_queue.IsPaused)
        {
            return;
        }

        _queue.Resume();
        _hub.SetPaused(false);
        _logger.LogInformation("Overlay retomado");
        Wake();
    }

    /// <summary>"Encerrar atual" / "Proxima": ends the alert on the bar now; it counts as shown, and the next one starts.</summary>
    public void Skip() => CancelPass(StopReason.Skip);

    /// <summary>Cancels every waiting alert (the one on the bar keeps going). No question asked.</summary>
    public void Clear() => _queue.Clear();

    // ---- correction ----

    private async Task<Evaluation> EvaluateAsync(string raw, SenderRole role, CancellationToken ct)
    {
        // Vulgar text is turned down first of all, for every sender: it never reaches Gemini, whether Gemini is up or not.
        if (_filter.IsBlocked(raw))
        {
            return BlockedEvaluation(CorrectorKind.Offline);
        }

        // A member's text that is already too long once cleaned is turned down at once: it never reaches Gemini or the
        // offline dictionary (a 4,000 character Telegram message would otherwise keep the only consumer busy for seconds).
        var cleaned = Display(raw);
        if (AlertRules.IsTooLong(cleaned, role))
        {
            var skipped = new CorrectionResult(
                new ParsedAlert(string.Empty, string.Empty, null, string.Empty, cleaned),
                CorrectorKind.Offline,
                Changed: false,
                Refused: false);
            return new Evaluation(skipped, new RuleDecision(RuleKind.Free, []), cleaned, null, TooLong: true);
        }

        var result = await CorrectAsync(raw, ct).ConfigureAwait(false);
        var alert = result.Alert;
        var decision = AlertRules.Decide(alert, role);
        var text = decision.Kind == RuleKind.Vehicle ? AlertFormatter.Format(alert) : AlertFormatter.FormatFree(alert.FreeText);
        var plate = decision.Kind == RuleKind.Vehicle ? alert.Plate : null;

        // The correction (Gemini, or a fixed rule) can bring a word in that was not typed: the final text is filtered too.
        if (!result.Refused && _filter.IsBlocked(text))
        {
            return BlockedEvaluation(result.By);
        }

        return new Evaluation(result, decision, text, plate, AlertRules.IsTooLong(text, role));
    }

    // The same shape as a refusal by Gemini, so every caller handles it with the code it already has.
    private static Evaluation BlockedEvaluation(CorrectorKind by)
    {
        var refused = new CorrectionResult(new ParsedAlert(string.Empty, string.Empty, null, string.Empty, string.Empty), by, Changed: false, Refused: true);
        return new Evaluation(refused, new RuleDecision(RuleKind.Reject, []), string.Empty, null, TooLong: false, Blocked: true);
    }

    // The one place the text is corrected, for the preview and for the submit alike.
    private async Task<CorrectionResult> CorrectAsync(string raw, CancellationToken ct)
    {
        var correction = _settings().Correction;
        if (!correction.Enabled)
        {
            return new CorrectionResult(AlertParser.Parse(raw), CorrectorKind.Offline, Changed: false, Refused: false);
        }

        return await _pipeline.RunAsync(raw, string.Equals(correction.Mode, "gemini", StringComparison.Ordinal), ct).ConfigureAwait(false);
    }

    // ---- the loop that plays the queue ----

    private async Task RunLoopAsync(CancellationToken stop)
    {
        var failures = 0;
        while (!stop.IsCancellationRequested)
        {
            try
            {
                var outcome = await PlayNextAsync(stop).ConfigureAwait(false);
                switch (outcome)
                {
                    case PassOutcome.Idle:
                        await _wake.Reader.ReadAsync(stop).ConfigureAwait(false);
                        break;
                    case PassOutcome.Shown:
                        failures = 0;
                        break;
                    case PassOutcome.CutShort:
                        failures++;
                        await Task.Delay(RetryDelay(failures), _clock, stop).ConfigureAwait(false);
                        break;
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Whatever went wrong, the loop must go on: a dead loop means a silent LED for the rest of the service.
                Fail("tocar a fila", ex);
                failures++;

                // The pass is over, so an alert still marked as on screen would stay there for ever and block the queue.
                if (_queue.Current is { } stuck)
                {
                    _queue.TryReturnCurrentToFront(stuck.Id);
                }

                try
                {
                    await Task.Delay(RetryDelay(failures), _clock, stop).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private static TimeSpan RetryDelay(int failures)
    {
        var seconds = FirstRetry.TotalSeconds * Math.Pow(2, Math.Min(Math.Max(failures, 1) - 1, 6));
        return TimeSpan.FromSeconds(Math.Min(seconds, MaxRetry.TotalSeconds));
    }

    private enum PassOutcome
    {
        Idle,
        Shown,
        CutShort,
    }

    // Takes the next alert to the bar, if there is one and the bar can show it, and plays it.
    private async Task<PassOutcome> PlayNextAsync(CancellationToken stop)
    {
        if (_queue.IsPaused || !_display.LedPresent || _queue.StartNext() is not { } item)
        {
            return PassOutcome.Idle;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(stop);
        lock (_playGate)
        {
            _playCts = cts;
            _stopReason = StopReason.None;
        }

        try
        {
            // A pause between taking the alert and registering the pass has already cancelled it: do not put it on the bar.
            if (_queue.Current?.Id != item.Id)
            {
                return PassOutcome.Idle;
            }

            var completed = false;
            _hub.SetShowing(true);
            try
            {
                completed = await _display.ShowAsync(item.Text, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Pause, skip or shutdown: handled below from the reason and the state of the queue.
            }
            catch (Exception ex)
            {
                Fail("mostrar o aviso na barra", ex);
            }
            finally
            {
                _hub.SetShowing(false);
            }

            StopReason reason;
            lock (_playGate)
            {
                reason = _stopReason;
            }

            if (completed || reason == StopReason.Skip)
            {
                if (_queue.TryFinishCurrent(item.Id))
                {
                    Forget([item]);
                    if (item.Source is AlertSource.Telegram or AlertSource.Site)
                    {
                        _notifier.Notify(TelegramReplies.Shown(item.Text));
                    }
                }

                return PassOutcome.Shown;
            }

            // Cut short by the LED (gone or moved), a failure of the bar, or the shutdown: the alert goes back to the front.
            // After a pause the queue no longer has it on screen and nothing happens.
            if (_queue.TryReturnCurrentToFront(item.Id))
            {
                _logger.LogInformation("Aviso interrompido, volta para a frente da fila: {Text}", item.Text);
                if (!_display.LedPresent)
                {
                    TellLedOff([item]);
                    return PassOutcome.Idle;
                }

                return stop.IsCancellationRequested ? PassOutcome.Idle : PassOutcome.CutShort;
            }

            return PassOutcome.Idle;
        }
        finally
        {
            lock (_playGate)
            {
                _playCts = null;
            }
        }
    }

    private void CancelPass(StopReason reason)
    {
        CancellationTokenSource? cts;
        lock (_playGate)
        {
            cts = _playCts;
            if (cts is not null)
            {
                _stopReason = reason;
            }
        }

        try
        {
            cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The pass ended by itself an instant ago.
        }
    }

    // ---- events ----

    private void OnQueueChanged(object? sender, EventArgs e)
    {
        try
        {
            _hub.SetPaused(_queue.IsPaused);
            Wake();
        }
        catch (Exception ex)
        {
            Fail("atualizar o estado da fila", ex);
        }
    }

    private void OnLedPresenceChanged(bool present)
    {
        try
        {
            _hub.SetLedPresent(present);
            Wake();
            if (!present)
            {
                TellLedOff(_queue.ToSnapshot().Pending);
            }
        }
        catch (Exception ex)
        {
            Fail("tratar a mudanca do LED", ex);
        }
    }

    // Handlers of the pipeline's event must never throw: it raises the event inline.
    private void OnGeminiHealthChanged(bool online)
    {
        try
        {
            _hub.SetGemini(online);
            if (online)
            {
                _logger.LogInformation("Gemini online");
            }
            else
            {
                _logger.LogWarning("Gemini offline: os avisos usam so o corretor offline");
            }
        }
        catch (Exception ex)
        {
            Fail("registrar o estado do Gemini", ex);
        }
    }

    private void ExpireOld(bool atStart)
    {
        try
        {
            var now = _clock.GetUtcNow();
            foreach (var item in _queue.ExpireOld())
            {
                if (item.Source == AlertSource.Telegram && (!atStart || now - item.CreatedAt <= NotifyExpiredWithin))
                {
                    _notifier.Notify(TelegramReplies.Expired);
                }

                Forget([item]);
            }

            PruneTold();
        }
        catch (Exception ex)
        {
            Fail("expirar avisos antigos", ex);
        }
    }

    private void Wake() => _wake.Writer.TryWrite(true);

    // ---- small helpers ----

    private void Reply(AlertSource source, string text)
    {
        if (source == AlertSource.Telegram)
        {
            _notifier.Notify(text);
        }
    }

    private void TellLedOff(IEnumerable<QueueItem> items)
    {
        foreach (var item in items)
        {
            if (item.Source == AlertSource.Telegram && Tell(item.Id))
            {
                _notifier.Notify(TelegramReplies.LedOff);
            }
        }
    }

    // True the first time an alert is told that the LED is off.
    private bool Tell(string id)
    {
        lock (_toldGate)
        {
            return _ledOffTold.Add(id);
        }
    }

    private void Forget(IEnumerable<QueueItem> items)
    {
        lock (_toldGate)
        {
            foreach (var item in items)
            {
                _ledOffTold.Remove(item.Id);
            }
        }
    }

    private void PruneTold()
    {
        var live = _queue.ToSnapshot().Pending.Select(i => i.Id).ToHashSet(StringComparer.Ordinal);
        lock (_toldGate)
        {
            _ledOffTold.RemoveWhere(id => !live.Contains(id));
        }
    }

    private QueueItem NewItem(string text, string? plate, AlertSource source, bool urgent, string? senderId, string original, string? id = null) =>
        new(id ?? _newId(), text, plate, source, urgent, _clock.GetUtcNow(), senderId, original);

    private static string Display(string raw)
    {
        var text = AlertFormatter.FormatFree(raw);
        return text.Length > 0 ? text : raw.Trim();
    }

    private void Fail(string what, Exception ex)
    {
        try
        {
            _logger.LogError(ex, "Erro ao {What}", what);
            _hub.ReportError("Avisos", ex, _clock.GetUtcNow());
        }
        catch (Exception)
        {
            // Reporting an error must never raise another one.
        }
    }

    private sealed record Evaluation(CorrectionResult Result, RuleDecision Decision, string Text, string? Plate, bool TooLong, bool Blocked = false);
}
