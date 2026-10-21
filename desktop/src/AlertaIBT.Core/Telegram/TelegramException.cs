namespace AlertaIBT.Core.Telegram;

/// <summary>
/// A call to the Telegram Bot API that did not work: no internet, a timeout, an HTTP error, or an answer that is not
/// the expected JSON. The message is in Portuguese for the status line and never contains the bot token.
/// </summary>
public sealed class TelegramException : Exception
{
    /// <param name="message">What went wrong; the caller makes sure it holds no token.</param>
    /// <param name="statusCode">The HTTP or Telegram error code, or <c>null</c> when there was no answer.</param>
    public TelegramException(string message, int? statusCode = null)
        : base(message)
    {
        StatusCode = statusCode;
    }

    /// <summary>The HTTP status (or the <c>error_code</c> of the body), when Telegram answered.</summary>
    public int? StatusCode { get; }

    /// <summary>Whether Telegram refused because another consumer holds the bot (HTTP 409).</summary>
    public bool IsConflict => StatusCode == 409;
}
