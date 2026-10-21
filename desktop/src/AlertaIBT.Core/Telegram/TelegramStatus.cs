namespace AlertaIBT.Core.Telegram;

/// <summary>Where the Telegram connection stands, as shown by the green/yellow/red dot of the app.</summary>
public enum TelegramState
{
    /// <summary>The token or the chat id is missing: nothing is asked of Telegram.</summary>
    NotConfigured,

    /// <summary>Configured, and the first answer has not arrived yet.</summary>
    Connecting,

    /// <summary>The last call worked.</summary>
    Online,

    /// <summary>One or two calls in a row failed; the poller waits and tries again.</summary>
    Reconnecting,

    /// <summary>Three or more calls in a row failed; the poller keeps trying at the slowest pace.</summary>
    Offline,

    /// <summary>
    /// Telegram answered HTTP 409: another program is consuming the same bot (the old Electron app, or a webhook).
    /// Nothing is processed while this lasts.
    /// </summary>
    Conflict,
}
