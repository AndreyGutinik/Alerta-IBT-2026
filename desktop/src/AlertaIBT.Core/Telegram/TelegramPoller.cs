namespace AlertaIBT.Core.Telegram;

/// <summary>
/// Consumes the bot's messages by long polling and raises <see cref="MessageReceived"/> for each one that is an alert
/// of the configured chat (see <see cref="TelegramMessageFilter"/>). It tracks the connection as a
/// <see cref="TelegramState"/> and keeps the <c>getUpdates</c> offset, which it hands to a callback after each update so
/// a restart neither repeats nor loses messages.
/// </summary>
/// <remarks>
/// <para>
/// There is one consumer per bot: if another program polls the same bot Telegram answers HTTP 409, the state becomes
/// <see cref="TelegramState.Conflict"/> and nothing is processed until the other consumer is gone.
/// </para>
/// <para>
/// Events are raised on the polling thread, one update at a time; a handler that throws is skipped (its update is not
/// repeated) and recorded in <see cref="LastError"/>. The token and the chat id are read on every cycle, so changing
/// them in the settings takes effect without a restart; a different token starts again from offset 0, because update
/// ids of another bot mean nothing here.
/// </para>
/// </remarks>
public sealed class TelegramPoller
{
    /// <summary>The long-poll wait asked of Telegram.</summary>
    public const int LongPollSeconds = 25;

    private const int FailuresUntilOffline = 3;

    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan NotConfiguredRecheck = TimeSpan.FromSeconds(5);

    private readonly TelegramClient _client;
    private readonly Func<long> _chatId;
    private readonly Action<long>? _saveOffset;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    private long _offset;
    private int _state = (int)TelegramState.NotConfigured;
    private volatile string? _lastError;

    /// <param name="client">Talks to Telegram.</param>
    /// <param name="chatId">Gives the configured chat id, read on every cycle; 0 means none.</param>
    /// <param name="saveOffset">
    /// Called with the new offset after each update is handled (and with 0 when the token changes); failures are ignored.
    /// </param>
    /// <param name="offset">The offset saved by the last run; 0 starts from whatever Telegram still holds.</param>
    /// <param name="delay">Waits between retries; a seam for tests, <see cref="Task.Delay(TimeSpan, CancellationToken)"/> by default.</param>
    public TelegramPoller(
        TelegramClient client,
        Func<long> chatId,
        Action<long>? saveOffset = null,
        long offset = 0,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(chatId);

        _client = client;
        _chatId = chatId;
        _saveOffset = saveOffset;
        _offset = Math.Max(0, offset);
        _delay = delay ?? Task.Delay;
    }

    /// <summary>
    /// Raised for each alert: the text without its <c>#</c> and the original message (for its chat, id and sender).
    /// </summary>
    public event Action<string, TgMessage>? MessageReceived;

    /// <summary>Raised when <see cref="State"/> changes, on the polling thread.</summary>
    public event Action<TelegramState>? StateChanged;

    /// <summary>Where the connection stands.</summary>
    public TelegramState State => (TelegramState)Volatile.Read(ref _state);

    /// <summary>The offset of the next <c>getUpdates</c>: the last update id handled plus one.</summary>
    public long Offset => Volatile.Read(ref _offset);

    /// <summary>The last failure, in Portuguese and without the token; <c>null</c> after a success.</summary>
    public string? LastError => _lastError;

    /// <summary>
    /// The time to wait after the <paramref name="failures"/>-th failure in a row: 1, 2, 4, 8, 16 and then 30 seconds.
    /// </summary>
    internal static TimeSpan BackoffFor(int failures)
    {
        if (failures < 1)
        {
            return TimeSpan.Zero;
        }

        // 2^5 = 32 s is already past the cap, so the exponent never needs to grow beyond it.
        var seconds = Math.Pow(2, Math.Min(failures - 1, 5));
        return TimeSpan.FromSeconds(Math.Min(seconds, MaxBackoff.TotalSeconds));
    }

    /// <summary>
    /// Polls until <paramref name="ct"/> is cancelled, which ends the run quietly. It never throws for a Telegram
    /// problem: it waits (growing from 1 to 30 s) and tries again.
    /// </summary>
    /// <param name="ct">Stops the polling.</param>
    public async Task RunAsync(CancellationToken ct)
    {
        var failures = 0;
        string? sessionToken = null;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var token = _client.ConfiguredToken;
                if (token is null || _chatId() == 0)
                {
                    failures = 0;
                    _lastError = null;
                    SetState(TelegramState.NotConfigured);
                    await _delay(NotConfiguredRecheck, ct).ConfigureAwait(false);
                    continue;
                }

                if (sessionToken is not null && !string.Equals(sessionToken, token, StringComparison.Ordinal))
                {
                    failures = 0;
                    Volatile.Write(ref _offset, 0);
                    Save(0);
                }

                sessionToken = token;
                if (State == TelegramState.NotConfigured)
                {
                    SetState(TelegramState.Connecting);
                }

                IReadOnlyList<TgUpdate> updates;
                try
                {
                    updates = await _client.GetUpdatesAsync(Offset, LongPollSeconds, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    failures++;
                    _lastError = ex is TelegramException ? ex.Message : "Falha inesperada ao falar com o Telegram.";
                    SetState(ex is TelegramException { IsConflict: true }
                        ? TelegramState.Conflict
                        : failures >= FailuresUntilOffline ? TelegramState.Offline : TelegramState.Reconnecting);
                    await _delay(BackoffFor(failures), ct).ConfigureAwait(false);
                    continue;
                }

                failures = 0;
                _lastError = null;
                SetState(TelegramState.Online);
                Handle(updates);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Stopped while waiting to retry: a normal end.
        }
    }

    private void Handle(IReadOnlyList<TgUpdate> updates)
    {
        var chatId = _chatId();
        foreach (var update in updates)
        {
            // Telegram never repeats an update below the offset it was given; a stray one is not processed twice.
            if (update.UpdateId < Offset)
            {
                continue;
            }

            if (update.Message is { } message && TelegramMessageFilter.TryAccept(message, chatId) is { Length: > 0 } text)
            {
                try
                {
                    MessageReceived?.Invoke(text, message);
                }
                catch (Exception)
                {
                    // A buggy handler must not stall the bot or make the update come back forever.
                    _lastError = "Falha ao processar uma mensagem do Telegram.";
                }
            }

            var next = update.UpdateId + 1;
            Volatile.Write(ref _offset, next);
            Save(next);
        }
    }

    private void Save(long offset)
    {
        try
        {
            _saveOffset?.Invoke(offset);
        }
        catch (Exception)
        {
            // The offset only protects against a repeat after a restart; losing one save is not worth stopping the bot.
        }
    }

    private void SetState(TelegramState state)
    {
        if (Interlocked.Exchange(ref _state, (int)state) == (int)state)
        {
            return;
        }

        try
        {
            StateChanged?.Invoke(state);
        }
        catch (Exception)
        {
            // A buggy listener must not stop the polling.
        }
    }
}
