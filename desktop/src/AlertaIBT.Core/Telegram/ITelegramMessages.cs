namespace AlertaIBT.Core.Telegram;

/// <summary>What "Limpar histórico" needs from Telegram: to delete a message and to know which one is pinned. A seam for tests.</summary>
public interface ITelegramMessages
{
    /// <summary>Whether there is a bot token to use.</summary>
    bool IsConfigured { get; }

    /// <summary>Deletes a message; throws <see cref="TelegramException"/> when Telegram refuses or cannot be reached.</summary>
    Task DeleteMessageAsync(long chatId, long messageId, CancellationToken ct);

    /// <summary>The id of the pinned message of the chat, or <c>null</c> when none; throws <see cref="TelegramException"/> on failure.</summary>
    Task<long?> GetPinnedMessageIdAsync(long chatId, CancellationToken ct);
}
