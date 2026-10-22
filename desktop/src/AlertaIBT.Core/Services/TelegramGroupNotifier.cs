using System.Threading.Channels;
using AlertaIBT.Core.Telegram;
using Microsoft.Extensions.Logging;

namespace AlertaIBT.Core.Services;

/// <summary>
/// Writes the bot's answers to the Telegram group, one at a time and in order, in the background: a caller never waits for
/// Telegram and never sees an error. A message that fails is tried once more after a short wait, then dropped with a log line
/// (the same error repeated is logged once until a message goes through). Nothing is sent while the token or the chat is missing.
/// </summary>
public sealed class TelegramGroupNotifier : IGroupNotifier, IDisposable
{
    private const int Capacity = 100;

    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);

    private readonly TelegramClient _client;
    private readonly Func<long> _chatId;
    private readonly ILogger _logger;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly TelegramJournal? _journal;
    private readonly TimeProvider _clock;
    private readonly Channel<string> _outbox = Channel.CreateBounded<string>(
        new BoundedChannelOptions(Capacity) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

    private readonly CancellationTokenSource _stop = new();
    private Task? _pump;
    private int _started;
    private int _disposed;
    private string? _lastLoggedError;

    /// <param name="client">Sends the messages.</param>
    /// <param name="chatId">Gives the group's chat id, read for every message; 0 means none.</param>
    /// <param name="logger">Receives the failures.</param>
    /// <param name="delay">Waits before the second attempt; a seam for tests.</param>
    /// <param name="journal">Where the id of every reply sent is noted, so "Limpar histórico" can delete it; none when <c>null</c>.</param>
    /// <param name="clock">Dates the journal entries; the system clock by default.</param>
    public TelegramGroupNotifier(
        TelegramClient client,
        Func<long> chatId,
        ILogger logger,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        TelegramJournal? journal = null,
        TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(chatId);
        ArgumentNullException.ThrowIfNull(logger);

        _client = client;
        _chatId = chatId;
        _logger = logger;
        _delay = delay ?? Task.Delay;
        _journal = journal;
        _clock = clock ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public void Notify(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        if (_chatId() == 0 || !_client.IsConfigured)
        {
            _logger.LogDebug("Telegram sem token ou chat: a resposta nao foi enviada");
            return;
        }

        _outbox.Writer.TryWrite(text);
        if (Interlocked.Exchange(ref _started, 1) == 0)
        {
            _pump = Task.Run(PumpAsync, CancellationToken.None);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _stop.Cancel();
        _outbox.Writer.TryComplete();
        try
        {
            _pump?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception)
        {
            // The pump never throws; this only guards a race with a cancelled wait.
        }

        _stop.Dispose();
    }

    private async Task PumpAsync()
    {
        var ct = _stop.Token;
        try
        {
            await foreach (var text in _outbox.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                await SendWithOneRetryAsync(text, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "O envio de respostas ao Telegram parou");
        }
    }

    private async Task SendWithOneRetryAsync(string text, CancellationToken ct)
    {
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var chat = _chatId();
            if (chat == 0)
            {
                return;
            }

            try
            {
                var id = await _client.SendMessageAsync(chat, text, ct).ConfigureAwait(false);
                _lastLoggedError = null;
                if (id is { } messageId)
                {
                    _journal?.Add(chat, messageId, _clock.GetUtcNow());
                }

                return;
            }
            catch (TelegramException ex)
            {
                if (!string.Equals(_lastLoggedError, ex.Message, StringComparison.Ordinal))
                {
                    _lastLoggedError = ex.Message;
                    _logger.LogWarning("Nao foi possivel responder no grupo do Telegram: {Error}", ex.Message);
                }

                if (attempt == 2)
                {
                    return;
                }

                await _delay(RetryDelay, ct).ConfigureAwait(false);
            }
        }
    }
}
