namespace AlertaIBT.Core.Telegram;

/// <summary>Decides which Telegram messages become alerts.</summary>
public static class TelegramMessageFilter
{
    /// <summary>
    /// The alert text of a message, or null when the message is not an alert: it is from another chat (or the chat
    /// is not configured, <paramref name="chatId"/> 0), it has no text, it does not start with <c>#</c>, or it was
    /// sent by a bot or on behalf of another chat. The text comes back without its leading <c>#</c> signs, trimmed; it is empty when the message
    /// is just <c>#</c>.
    /// </summary>
    /// <remarks>
    /// An anonymous admin of the group is accepted although Telegram sends it as a bot (<c>GroupAnonymousBot</c>):
    /// what tells it apart is <see cref="TgMessage.SenderChat"/> being the group itself. A channel post forwarded
    /// into the group also comes from a bot, but its sender chat is the channel, so it is refused.
    /// </remarks>
    public static string? TryAccept(TgMessage m, long chatId)
    {
        ArgumentNullException.ThrowIfNull(m);

        if (chatId == 0 || m.Chat.Id != chatId)
        {
            return null;
        }

        // A message sent on behalf of a chat other than the group is a channel post, whatever "from" says:
        // Telegram auto-forwards linked-channel posts with from = user 777000, which is not flagged as a bot.
        if (m.SenderChat is not null && m.SenderChat.Id != chatId)
        {
            return null;
        }

        if (m.From?.IsBot == true && m.SenderChat is null)
        {
            return null;
        }

        string? text = m.Text?.Trim();
        if (string.IsNullOrEmpty(text) || text[0] != '#')
        {
            return null;
        }

        return text.TrimStart('#').Trim();
    }
}
