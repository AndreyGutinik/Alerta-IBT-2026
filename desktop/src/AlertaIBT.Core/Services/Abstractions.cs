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

/// <summary>The one pass of an alert on the bar, as it really is: the text, when it began and how long it lasts.</summary>
/// <param name="Text">The text on the bar (the one given to <see cref="IAlertDisplay.ShowAsync"/>), to tell which queue item it is.</param>
/// <param name="StartedAtMs">When the pass began, in milliseconds since 1970 (UTC), on the clock of this computer.</param>
/// <param name="DurationMs">The whole pass in milliseconds: slide in, scroll (real text width and speed), slide out (<c>BarTimeline.Total</c>).</param>
public sealed record PassTiming(string Text, long StartedAtMs, long DurationMs);

/// <summary>Tells what the bar is doing right now, for the site to publish the real length of the pass.</summary>
public interface IPassClock
{
    /// <summary>The alert on the bar now; <c>null</c> when none (between alerts, paused, the LED gone, the 3 s test). Any thread.</summary>
    PassTiming? Current { get; }

    /// <summary>Raised when <see cref="Current"/> changes (a pass begins, restarts or ends); on any thread. A handler must not throw.</summary>
    event Action? Changed;
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
