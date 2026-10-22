using Microsoft.Extensions.Logging;

namespace AlertaIBT.Core.Telegram;

/// <summary>What deleting the messages of the group came to.</summary>
/// <param name="Deleted">Messages Telegram deleted.</param>
/// <param name="Failed">Messages that stay in the group: older than 47 hours, or the bot may not delete them, or Telegram could not be reached.</param>
public readonly record struct TelegramClearResult(int Deleted, int Failed);

/// <summary>Deletes the messages of the group that the app knows about.</summary>
public interface ITelegramHistory
{
    /// <summary>Deletes what can be deleted. Never throws.</summary>
    Task<TelegramClearResult> ClearAsync(CancellationToken ct);
}

/// <summary>
/// Deletes, one by one, the messages of the <see cref="TelegramJournal"/> that are less than 47 hours old (Telegram refuses after
/// 48), except the message pinned in the chat. A refusal (the bot is not an admin, the message is gone, it is too old) is counted and
/// ignored. Nothing that is not in the journal is ever touched. The journal is emptied at the end.
/// </summary>
public sealed class TelegramHistoryCleaner : ITelegramHistory
{
    /// <summary>Messages older than this are not even tried: Telegram refuses at 48 hours and the clocks may differ a little.</summary>
    public static readonly TimeSpan DeleteWithin = TimeSpan.FromHours(47);

    private const int NetworkFailuresToStop = 3;

    private readonly TelegramJournal _journal;
    private readonly ITelegramMessages _api;
    private readonly TimeProvider _clock;
    private readonly ILogger _logger;

    /// <param name="journal">The messages the app knows about.</param>
    /// <param name="api">Deletes messages and finds the pinned one.</param>
    /// <param name="clock">Ages the messages; the system clock by default.</param>
    /// <param name="logger">Receives one summary line (never the token).</param>
    public TelegramHistoryCleaner(TelegramJournal journal, ITelegramMessages api, TimeProvider? clock, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(logger);

        _journal = journal;
        _api = api;
        _clock = clock ?? TimeProvider.System;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<TelegramClearResult> ClearAsync(CancellationToken ct)
    {
        var deleted = 0;
        var failed = 0;
        try
        {
            if (!_api.IsConfigured)
            {
                _logger.LogDebug("Telegram sem token: nada apagado");
                return new TelegramClearResult(0, 0);
            }

            var entries = _journal.Snapshot();
            if (entries.Count == 0)
            {
                return new TelegramClearResult(0, 0);
            }

            var now = _clock.GetUtcNow();
            var pinned = new Dictionary<long, long?>();
            var networkFailures = 0;
            var stopped = false;
            foreach (var entry in entries)
            {
                ct.ThrowIfCancellationRequested();
                if (stopped || now - entry.SentAt > DeleteWithin)
                {
                    failed++;
                    continue;
                }

                if (!pinned.TryGetValue(entry.ChatId, out var pinnedId))
                {
                    pinnedId = await PinnedOfAsync(entry.ChatId, ct).ConfigureAwait(false);
                    pinned[entry.ChatId] = pinnedId;
                }

                if (pinnedId == entry.MessageId)
                {
                    continue;
                }

                try
                {
                    await _api.DeleteMessageAsync(entry.ChatId, entry.MessageId, ct).ConfigureAwait(false);
                    deleted++;
                    networkFailures = 0;
                }
                catch (TelegramException ex)
                {
                    // Telegram answered with a refusal, or did not answer at all.
                    if (ex.StatusCode is null)
                    {
                        networkFailures++;
                        stopped = networkFailures >= NetworkFailuresToStop;
                    }
                    else
                    {
                        networkFailures = 0;
                        stopped = ex.StatusCode == 429;
                    }

                    if (!IsAlreadyGone(ex))
                    {
                        failed++;
                    }
                }
            }

            _journal.Clear();
        }
        catch (OperationCanceledException)
        {
            // Stopped half way: what is left in the journal is tried again by the next clear.
            _logger.LogWarning("Telegram: a limpeza foi interrompida");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Telegram: erro ao apagar as mensagens");
        }

        _logger.LogInformation("Telegram: {Deleted} apagadas, {Failed} n\U000000E3o puderam ser apagadas", deleted, failed);
        return new TelegramClearResult(deleted, failed);
    }

    // The message the chat has pinned, or null when none or when it cannot be known (then nothing is protected but the journal itself).
    private async Task<long?> PinnedOfAsync(long chatId, CancellationToken ct)
    {
        try
        {
            return await _api.GetPinnedMessageIdAsync(chatId, ct).ConfigureAwait(false);
        }
        catch (TelegramException)
        {
            return null;
        }
    }

    // A message someone already deleted: nothing stays in the group, so it is not a failure.
    private static bool IsAlreadyGone(TelegramException ex) =>
        ex.StatusCode == 400 && ex.Message.Contains("message to delete not found", StringComparison.OrdinalIgnoreCase);
}
