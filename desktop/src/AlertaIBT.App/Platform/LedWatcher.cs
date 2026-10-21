using AlertaIBT.App.Overlay;
using AlertaIBT.Core.Screens;

namespace AlertaIBT.App.Platform;

/// <summary>
/// Knows where the LED is. It looks at the monitors when Windows says the display changed (<c>WM_DISPLAYCHANGE</c>, through
/// <see cref="NotifyDisplayChanged"/>, after a short pause because Windows sends several in a row) and again every 5 seconds
/// as a safety net for a message that got lost. When the LED appears, vanishes, changes resolution or moves it raises
/// <see cref="LedChanged"/> and remembers the LED's device path, so with two LEDs the same one is preferred next time.
/// With <c>--test-led</c> the primary monitor stands in for the LED.
/// </summary>
/// <remarks>Events are raised on a timer thread or on the thread that called <see cref="NotifyDisplayChanged"/>; subscribers marshal to the UI thread themselves.</remarks>
public sealed class LedWatcher : ILedSource, IDisposable
{
    /// <summary>How often the monitors are checked even when no message arrived.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    /// <summary>How long after a display-change message the monitors are checked (Windows sends a burst of them).</summary>
    public static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(500);

    private readonly LedTracker _tracker;
    private readonly Action<string> _rememberDevicePath;
    private readonly Action<Exception>? _onError;
    private readonly TimeProvider _clock;
    private readonly object _gate = new();
    private ITimer? _poll;
    private ITimer? _debounce;
    private bool _disposed;

    /// <param name="tracker">Finds the LED among the monitors.</param>
    /// <param name="rememberDevicePath">Called with the LED's device path when a real LED (not the test stand-in) is found.</param>
    /// <param name="onError">Told about an unexpected error while looking at the monitors; it must not throw.</param>
    /// <param name="clock">Drives the poll and the pause; the system clock by default.</param>
    public LedWatcher(LedTracker tracker, Action<string> rememberDevicePath, Action<Exception>? onError = null, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(tracker);
        ArgumentNullException.ThrowIfNull(rememberDevicePath);

        _tracker = tracker;
        _rememberDevicePath = rememberDevicePath;
        _onError = onError;
        _clock = clock ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public event Action<ScreenInfo?>? LedChanged;

    /// <inheritdoc />
    public ScreenInfo? Led
    {
        get
        {
            lock (_gate)
            {
                return _tracker.Current;
            }
        }
    }

    /// <summary>Whether the primary monitor stands in for the LED (<c>--test-led</c>).</summary>
    public bool TestMode => _tracker.TestMode;

    /// <summary>Looks at the monitors now, without raising <see cref="LedChanged"/>, and starts the 5-second check.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_disposed || _poll is not null)
            {
                return;
            }

            _tracker.Refresh();
            _poll = _clock.CreateTimer(_ => CheckNow(), null, PollInterval, PollInterval);
            _debounce = _clock.CreateTimer(_ => CheckNow(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        Remember(Led);
    }

    /// <summary>Tells the watcher that Windows reported a display change; the monitors are checked after <see cref="DebounceDelay"/>.</summary>
    public void NotifyDisplayChanged()
    {
        lock (_gate)
        {
            _debounce?.Change(DebounceDelay, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Looks at the monitors now and raises <see cref="LedChanged"/> if the LED differs from the last look.</summary>
    public void CheckNow()
    {
        ScreenInfo? led;
        bool changed;
        try
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                changed = _tracker.Refresh();
                led = _tracker.Current;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A failed look must not kill the timer thread; the next check tries again.
            ReportError(ex);
            return;
        }

        if (!changed)
        {
            return;
        }

        Remember(led);
        try
        {
            LedChanged?.Invoke(led);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ReportError(ex);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _poll?.Dispose();
            _debounce?.Dispose();
        }
    }

    private void Remember(ScreenInfo? led)
    {
        if (!_tracker.TestMode && led is { DevicePath.Length: > 0 })
        {
            try
            {
                _rememberDevicePath(led.DevicePath);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                ReportError(ex);
            }
        }
    }

    private void ReportError(Exception ex)
    {
        try
        {
            _onError?.Invoke(ex);
        }
        catch (Exception)
        {
            // Last resort: nothing is left to tell.
        }
    }
}
