using System.Globalization;
using System.Threading.Channels;
using AlertaIBT.Core.Alerts;
using AlertaIBT.Core.Telegram;
using Microsoft.Extensions.Logging;

namespace AlertaIBT.Core.Services;

/// <summary>
/// Joins the Telegram poller to the alert service: each message of the group that starts with <c>#</c> becomes an alert of a
/// member, answered in the group. Messages are handled one at a time in the order they arrived, off the polling thread, so a slow
/// correction never holds up the next poll; the poller moves its offset forward as soon as it has handed a message over.
/// </summary>
/// <remarks>
/// Telegram keeps unconfirmed messages for up to 24 hours, so after the PC was off the first poll brings old messages. One
/// older than 30 minutes is dropped (it would only block the LED with a notice nobody is waiting for); its sender is told
/// that it expired when it is not older than <see cref="AlertService.NotifyExpiredWithin"/>.
/// </remarks>
public sealed class TelegramEngine : IDisposable
{
    /// <summary>The oldest message that still becomes an alert.</summary>
    public static readonly TimeSpan MaxMessageAge = TimeSpan.FromMinutes(30);

    private readonly TelegramPoller _poller;
    private readonly AlertService _alerts;
    private readonly IGroupNotifier _notifier;
    private readonly StatusHub _hub;
    private readonly ILogger _logger;
    private readonly TimeProvider _clock;
    private readonly TelegramJournal? _journal;
    private Channel<(string Text, TgMessage Message)> _inbox = NewInbox();

    private CancellationTokenSource? _stop;
    private Task? _pollerTask;
    private Task? _consumerTask;
    private int _started;

    /// <param name="poller">Brings the messages.</param>
    /// <param name="alerts">Takes them as alerts.</param>
    /// <param name="notifier">Tells the group about a message that is too old.</param>
    /// <param name="hub">Receives the Telegram state.</param>
    /// <param name="logger">Receives errors and dropped messages.</param>
    /// <param name="clock">The source of the current time, to age the messages.</param>
    /// <param name="journal">Where the id of every alert message is noted, so "Limpar histórico" can delete it; none when <c>null</c>.</param>
    public TelegramEngine(
        TelegramPoller poller,
        AlertService alerts,
        IGroupNotifier notifier,
        StatusHub hub,
        ILogger logger,
        TimeProvider? clock = null,
        TelegramJournal? journal = null)
    {
        ArgumentNullException.ThrowIfNull(poller);
        ArgumentNullException.ThrowIfNull(alerts);
        ArgumentNullException.ThrowIfNull(notifier);
        ArgumentNullException.ThrowIfNull(hub);
        ArgumentNullException.ThrowIfNull(logger);

        _poller = poller;
        _alerts = alerts;
        _notifier = notifier;
        _hub = hub;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
        _journal = journal;
    }

    /// <summary>Starts polling and handling messages. It can be called again after <see cref="StopAsync"/> (the master switch "Alertas ligados").</summary>
    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }

        _inbox = NewInbox();
        var inbox = _inbox;
        _stop = new CancellationTokenSource();
        var stop = _stop.Token;
        _poller.MessageReceived += OnMessage;
        _poller.StateChanged += OnState;
        OnState(_poller.State);
        _consumerTask = Task.Run(() => ConsumeAsync(inbox, stop), CancellationToken.None);
        _pollerTask = Task.Run(() => RunPollerAsync(stop), CancellationToken.None);
    }

    /// <summary>Stops polling and handling messages.</summary>
    public async Task StopAsync()
    {
        var stop = Interlocked.Exchange(ref _stop, null);
        if (stop is null)
        {
            return;
        }

        _poller.MessageReceived -= OnMessage;
        _poller.StateChanged -= OnState;
        _inbox.Writer.TryComplete();
        try
        {
            await stop.CancelAsync().ConfigureAwait(false);
            if (_pollerTask is { } poll)
            {
                await poll.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            }

            if (_consumerTask is { } consume)
            {
                await consume.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            _logger.LogWarning("O Telegram demorou para parar");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro ao parar o Telegram");
        }
        finally
        {
            stop.Dispose();
            _pollerTask = null;
            _consumerTask = null;
            Volatile.Write(ref _started, 0); // ready for another Start
        }
    }

    /// <inheritdoc />
    public void Dispose() => StopAsync().GetAwaiter().GetResult();

    private static Channel<(string Text, TgMessage Message)> NewInbox() =>
        Channel.CreateUnbounded<(string, TgMessage)>(new UnboundedChannelOptions { SingleReader = true });

    // Raised on the polling thread: only hands the message over, never throws.
    private void OnMessage(string text, TgMessage message)
    {
        try
        {
            _inbox.Writer.TryWrite((text, message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro ao receber uma mensagem do Telegram");
        }
    }

    private void OnState(TelegramState state)
    {
        try
        {
            var error = _poller.LastError;
            _hub.SetTelegram(state, error);

            // The poller raises the event only when the state changes, so a long outage is one line, not one per retry.
            if (state == TelegramState.Conflict)
            {
                _logger.LogError("Telegram: outro programa esta usando o mesmo bot (HTTP 409). O Alerta-IBT antigo esta aberto?");
            }
            else if (state is TelegramState.Reconnecting or TelegramState.Offline)
            {
                _logger.LogWarning("Telegram {State}: {Error}", state, error ?? "sem detalhe");
            }
            else
            {
                _logger.LogInformation("Telegram {State}", state);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro ao registrar o estado do Telegram");
        }
    }

    private async Task RunPollerAsync(CancellationToken stop)
    {
        try
        {
            await _poller.RunAsync(stop).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "O Telegram parou");
            _hub.ReportError("Telegram", ex, _clock.GetUtcNow());
        }
    }

    private async Task ConsumeAsync(Channel<(string Text, TgMessage Message)> inbox, CancellationToken stop)
    {
        try
        {
            await foreach (var (text, message) in inbox.Reader.ReadAllAsync(stop).ConfigureAwait(false))
            {
                try
                {
                    await HandleAsync(text, message, stop).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Erro ao tratar uma mensagem do Telegram");
                    _hub.ReportError("Telegram", ex, _clock.GetUtcNow());
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    private async Task HandleAsync(string text, TgMessage message, CancellationToken stop)
    {
        // Accepted, refused or too old: the message is in the group either way, and "Limpar histórico" may delete it.
        Note(message);

        if (AgeOf(message) is { } age && age > MaxMessageAge)
        {
            _logger.LogInformation("Mensagem do Telegram descartada: {Age:F0} min de idade", age.TotalMinutes);
            if (age <= AlertService.NotifyExpiredWithin)
            {
                _notifier.Notify(TelegramReplies.Expired);
            }

            return;
        }

        var sender = message.From?.Id ?? message.SenderChat?.Id;
        var outcome = await _alerts.SubmitAsync(
            text,
            AlertSource.Telegram,
            SenderRole.Member,
            urgent: false,
            sender?.ToString(CultureInfo.InvariantCulture),
            stop).ConfigureAwait(false);
        _logger.LogInformation("Mensagem do Telegram: {Outcome}", outcome.Kind);
    }

    private void Note(TgMessage message)
    {
        if (_journal is null)
        {
            return;
        }

        var sentAt = _clock.GetUtcNow();
        if (message.Date > 0)
        {
            try
            {
                sentAt = DateTimeOffset.FromUnixTimeSeconds(message.Date);
            }
            catch (ArgumentOutOfRangeException)
            {
                // No usable date: now is the best guess.
            }
        }

        _journal.Add(message.Chat.Id, message.MessageId, sentAt);
    }

    // How old the message is, or null when Telegram gave no usable date.
    private TimeSpan? AgeOf(TgMessage message)
    {
        if (message.Date <= 0)
        {
            return null;
        }

        try
        {
            return _clock.GetUtcNow() - DateTimeOffset.FromUnixTimeSeconds(message.Date);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}
