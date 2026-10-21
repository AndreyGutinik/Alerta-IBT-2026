namespace AlertaIBT.Core.Services;

/// <summary>
/// The screen the alerts run on: in the app, the bar over the LED. The alert service plays one alert at a time on it and
/// reacts when the LED appears or vanishes.
/// </summary>
public interface IAlertDisplay
{
    /// <summary>Whether the LED screen is there right now.</summary>
    bool LedPresent { get; }

    /// <summary>
    /// Raised with the new presence of the LED when it appears, vanishes, changes resolution or moves. May be raised on
    /// any thread; a handler must not throw.
    /// </summary>
    event Action<bool>? LedPresenceChanged;

    /// <summary>
    /// Shows <paramref name="text"/> once. <c>true</c> when it crossed the whole screen; <c>false</c> when it was cut short
    /// (cancelled, the LED vanished or moved, the bar failed), in which case the alert must be shown again later.
    /// </summary>
    /// <param name="text">The final text of the alert.</param>
    /// <param name="ct">Stops the pass at once.</param>
    Task<bool> ShowAsync(string text, CancellationToken ct);
}

/// <summary>Writes to the Telegram group. It never blocks and never throws: a message that cannot be sent is only logged.</summary>
public interface IGroupNotifier
{
    /// <summary>Queues <paramref name="text"/> to be sent to the group.</summary>
    /// <param name="text">The message.</param>
    void Notify(string text);
}

/// <summary>What the local control API (Holyrics) can do to the overlay.</summary>
public interface IOverlayControl
{
    /// <summary>Whether the overlay is paused (the sermon).</summary>
    bool IsPaused { get; }

    /// <summary>The sermon began: cancels everything and refuses new alerts. Does nothing when already paused.</summary>
    void Pause();

    /// <summary>The sermon ended: accepts alerts again, silently. Does nothing when not paused.</summary>
    void Resume();
}
