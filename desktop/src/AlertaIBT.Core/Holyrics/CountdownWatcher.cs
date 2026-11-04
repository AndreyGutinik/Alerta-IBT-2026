using AlertaIBT.Core.Temple;
using System.Globalization;
using AlertaIBT.Core.Diagnostics;
using AlertaIBT.Core.Player;
using AlertaIBT.Core.Settings;
using Microsoft.Extensions.Logging;

namespace AlertaIBT.Core.Holyrics;

/// <summary>Which Holyrics countdown a reading came from.</summary>
public enum CountdownKind
{
    /// <summary>None is on display.</summary>
    None,

    /// <summary>The countdown of the communication panel (<c>GetCPInfo</c>: <c>countdown_show</c> and <c>countdown_time</c>, whole seconds).</summary>
    Panel,

    /// <summary>The countdown presentation item (<c>GetCurrentPresentation</c>: <c>type</c> "countdown", <c>current_time</c> as "M:SS"); the "para iniciar o culto" kind.</summary>
    Presentation,
}

/// <summary>One look at the Holyrics countdowns.</summary>
/// <param name="Outcome">How the request ended (<see cref="HolyricsOutcome.Ok"/> when Holyrics answered).</param>
/// <param name="Kind">The countdown that is on display (the one with the least time when there are two), or <see cref="CountdownKind.None"/>.</param>
/// <param name="SecondsLeft">The seconds the countdown shows (it can go below zero on the panel), or <c>null</c> when none is on display.</param>
/// <param name="Message">For a failure: the short reason.</param>
public readonly record struct CountdownReading(HolyricsOutcome Outcome, CountdownKind Kind = CountdownKind.None, double? SecondsLeft = null, string? Message = null)
{
    /// <summary>Whether Holyrics answered.</summary>
    public bool Ok => Outcome == HolyricsOutcome.Ok;

    /// <summary>Whether a countdown is on display.</summary>
    public bool Shown => Ok && SecondsLeft is not null;
}

/// <summary>What the watcher reads from Holyrics (the client implements it; the tests use a fake).</summary>
public interface IHolyricsCountdownSource
{
    /// <summary>Whether the connection is filled in.</summary>
    bool IsConfigured { get; }

    /// <summary>Looks at the countdowns on display now. Never throws (except a cancel the caller asked for); a failure comes back in the reading.</summary>
    Task<CountdownReading> ReadCountdownAsync(CancellationToken cancellationToken);
}

/// <summary>Reads the time of the countdown that Holyrics shows.</summary>
public static class CountdownText
{
    /// <summary>"M:SS", "MM:SS" or "H:MM:SS" (what a countdown presentation reports as <c>current_time</c>) in seconds; <c>null</c> when it is not a time.</summary>
    public static double? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var parts = text.Trim().Split(':');
        if (parts.Length is < 2 or > 3)
        {
            return null;
        }

        double total = 0;
        foreach (var part in parts)
        {
            if (!double.TryParse(part, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value) || value < 0)
            {
                return null;
            }

            total = (total * 60) + value;
        }

        return total;
    }

    /// <summary>The time as the chip shows it: "00:07", or "1:02:03" above an hour; below zero it stays at "00:00".</summary>
    public static string Format(double seconds)
    {
        var whole = (int)Math.Max(0, Math.Floor(seconds));
        return whole >= 3600
            ? string.Create(CultureInfo.InvariantCulture, $"{whole / 3600}:{whole % 3600 / 60:00}:{whole % 60:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{whole / 60:00}:{whole % 60:00}");
    }
}

/// <summary>
/// Decides, from one reading after another, when a countdown run has reached the threshold: <b>once per run</b>. A run ends (and the trigger is armed again) when
/// no countdown is on display, or the time shown goes up (a restart), or it is above the threshold.
/// </summary>
public sealed class CountdownTrigger
{
    private bool _armed = true;
    private double? _last;

    /// <summary>Feeds one reading; true exactly when this one is the first at or below <paramref name="threshold"/> of its run.</summary>
    /// <param name="seconds">The seconds on display, or <c>null</c> when no countdown is shown.</param>
    /// <param name="threshold">The seconds left at which to fire.</param>
    public bool Observe(double? seconds, double threshold)
    {
        if (seconds is null)
        {
            _armed = true;
            _last = null;
            return false;
        }

        var value = seconds.Value;
        if (_last is { } previous && value > previous)
        {
            _armed = true; // the time went up: the countdown was restarted
        }

        _last = value;
        if (value > threshold)
        {
            _armed = true;
            return false;
        }

        if (!_armed)
        {
            return false;
        }

        _armed = false;
        return true;
    }

    /// <summary>Forgets the run (the feature was switched off or Holyrics went away): the next countdown starts a new one.</summary>
    public void Reset()
    {
        _armed = true;
        _last = null;
    }
}

/// <summary>What the watcher sees now, for the chip of the Controle card.</summary>
/// <param name="Seconds">The seconds on display, or <c>null</c> when no countdown is shown.</param>
/// <param name="Problem">A reason the watch cannot work and the owner can fix (the token may not read the countdown), or <c>null</c>.</param>
public sealed record CountdownSnapshot(double? Seconds, string? Problem)
{
    /// <summary>Nothing on display, nothing wrong.</summary>
    public static CountdownSnapshot Idle { get; } = new(null, null);
}

/// <summary>
/// Watches the Holyrics countdown and, when ANY countdown reaches the chosen seconds (2 by default), fades the music out and pauses it: the same thing the old
/// Holyrics trigger asked of <c>/player/pause</c>. It only asks Holyrics while the feature is on and the connection is configured: about once a second while no
/// countdown is on display, four times a second while one is. A Holyrics that does not answer is asked again after a growing pause (up to 15 s) and at the normal
/// pace as soon as it answers. The stopwatch (the one that counts up) is ignored. Nothing here touches the UI thread.
/// </summary>
public sealed class CountdownWatcher : IDisposable
{
    /// <summary>The pause between looks when no countdown is on display.</summary>
    public static readonly TimeSpan SlowPoll = TimeSpan.FromSeconds(1);

    /// <summary>The pause between looks while a countdown is on display.</summary>
    public static readonly TimeSpan FastPoll = TimeSpan.FromMilliseconds(250);

    /// <summary>The longest pause after failures.</summary>
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(15);

    private readonly SettingsService _settings;
    private readonly IHolyricsCountdownSource _source;
    private readonly IPlayerControl _player;
    private readonly ILogger? _logger;
    private readonly TimeProvider _clock;
    private readonly CountdownTrigger _trigger = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private Task? _loop;
    private int _failures;
    private bool _disposed;
    private CountdownSnapshot _snapshot = CountdownSnapshot.Idle;

    /// <param name="settings">Where the switch and the seconds live.</param>
    /// <param name="source">Reads the countdown from Holyrics.</param>
    /// <param name="player">The music player (what the Holyrics trigger of /player/pause reaches).</param>
    /// <param name="logger">Receives the log lines.</param>
    /// <param name="clock">Times the pauses between looks.</param>
    public CountdownWatcher(SettingsService settings, IHolyricsCountdownSource source, IPlayerControl player, ILogger? logger = null, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(player);

        _settings = settings;
        _source = source;
        _player = player;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>Raised (from the background task) when <see cref="Current"/> changed.</summary>
    public event Action? Changed;

    /// <summary>What the watcher sees now.</summary>
    public CountdownSnapshot Current
    {
        get
        {
            lock (_gate)
            {
                return _snapshot;
            }
        }
    }

    /// <summary>How many times the music was stopped by a countdown since the app started (for the tests and the log).</summary>
    public int Triggers { get; private set; }

    /// <summary>Starts watching in the background (once). Returns at once.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_disposed || _loop is not null)
            {
                return;
            }

            _loop = Task.Run(() => RunAsync(_cts.Token));
        }
    }

    /// <summary>The pause before the next look, given what the last one saw (the cadence of the watch).</summary>
    public static TimeSpan NextDelay(bool active, bool countdownShown, int failures)
    {
        if (!active)
        {
            return SlowPoll;
        }

        if (failures > 0)
        {
            var seconds = Math.Min(MaxBackoff.TotalSeconds, Math.Pow(2, Math.Min(failures, 5)));
            return TimeSpan.FromSeconds(seconds);
        }

        return countdownShown ? FastPoll : SlowPoll;
    }

    /// <summary>One look and its consequences; returns the pause to wait before the next. The loop calls it; the tests call it with a fake clock.</summary>
    /// <param name="cancellationToken">Cancels the look.</param>
    public async Task<TimeSpan> PollOnceAsync(CancellationToken cancellationToken = default)
    {
        var holyrics = _settings.Current.Effective().Holyrics;
        if (!holyrics.CountdownStopEnabled || !_source.IsConfigured)
        {
            _trigger.Reset();
            _failures = 0;
            Publish(CountdownSnapshot.Idle);
            return NextDelay(active: false, countdownShown: false, 0);
        }

        var reading = await _source.ReadCountdownAsync(cancellationToken).ConfigureAwait(false);
        if (!reading.Ok)
        {
            // A blink of Holyrics never re-arms the trigger: the same run must not stop the music twice.
            _failures++;
            Publish(new CountdownSnapshot(null, reading.Outcome == HolyricsOutcome.Unauthorized ? reading.Message : null));
            return NextDelay(active: true, countdownShown: false, _failures);
        }

        _failures = 0;
        Publish(new CountdownSnapshot(reading.SecondsLeft, null));
        if (_trigger.Observe(reading.SecondsLeft, holyrics.CountdownStopAt))
        {
            StopTheMusic(holyrics.CountdownStopAt);
        }

        return NextDelay(active: true, reading.Shown, 0);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Task? loop;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            loop = _loop;
        }

        _cts.Cancel();
        try
        {
            loop?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // the loop was cancelled
        }

        _cts.Dispose();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TimeSpan delay;
            try
            {
                delay = await PollOnceAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _failures++;
                delay = NextDelay(active: true, countdownShown: false, _failures);
                _logger?.LogDebug("Cron\U000000F4metro do Holyrics: erro inesperado ({Type})", ex.GetType().Name);
            }

            try
            {
                await Task.Delay(delay, _clock, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    // The same stop as the Holyrics trigger of /player/pause: the fade-out of the owner's setting, then paused. A player that is not playing is left alone.
    private void StopTheMusic(int seconds)
    {
        Triggers++;
        Breadcrumbs.Add("holyrics: cronometro chegou a " + seconds + " s");
        try
        {
            if (!_player.GetStatus().IsPlaying)
            {
                _logger?.LogInformation("Cron\U000000F4metro do Holyrics chegou a {Seconds} s: a m\U000000FAsica n\U000000E3o estava tocando", seconds);
                return;
            }

            var result = _player.Pause();
            if (result.Success)
            {
                _logger?.LogInformation("Cron\U000000F4metro do Holyrics chegou a {Seconds} s: m\U000000FAsica parada", seconds);
            }
            else
            {
                _logger?.LogWarning("Cron\U000000F4metro do Holyrics chegou a {Seconds} s, mas a m\U000000FAsica n\U000000E3o p\U000000F4de ser parada: {Error}", seconds, result.Error);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning("Cron\U000000F4metro do Holyrics chegou a {Seconds} s: erro ao parar a m\U000000FAsica ({Type})", seconds, ex.GetType().Name);
        }
    }

    private void Publish(CountdownSnapshot next)
    {
        lock (_gate)
        {
            if (_snapshot == next)
            {
                return;
            }

            _snapshot = next;
        }

        Changed?.Invoke();
    }
}
