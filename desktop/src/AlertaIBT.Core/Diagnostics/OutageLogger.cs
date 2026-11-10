using System.Globalization;
using Microsoft.Extensions.Logging;

namespace AlertaIBT.Core.Diagnostics;

/// <summary>The words an <see cref="OutageLogger"/> uses about its service (the log is in pt-BR).</summary>
/// <param name="Subject">Who is talking: "Holyrics", "Art-Net", "Login do site".</param>
/// <param name="DownText">What it is doing while down, for the reminder: "sem resposta" gives "Holyrics continua sem resposta ha 2 h".</param>
/// <param name="RecoveredText">What it did when it came back, for the recovery: "voltou a responder" gives "Holyrics voltou a responder apos 5 min, 31 tentativas".</param>
public sealed record OutageWording(string Subject, string DownText = "sem resposta", string RecoveredText = "voltou a responder");

/// <summary>
/// Writes ONE line per outage of a service that a background loop keeps asking (Holyrics closed, a UDP port taken, the internet down), not one per try.
/// The first failure of an episode is logged in full (a Warning by default); the next ones are silent (Debug at most) while the loop keeps trying; a failure
/// for a DIFFERENT reason is logged again (at most once per <see cref="DefaultReasonChangeGap"/>) because that is news; while it stays down there is one
/// reminder per <see cref="DefaultReminderEvery"/> ("Holyrics continua sem resposta ha 2 h"); and the first success after it logs ONE Information line
/// ("Holyrics voltou a responder apos 5 min, 31 tentativas"). A new failure after that starts a new episode. Thread-safe; the lines are written outside its lock.
/// </summary>
public sealed class OutageLogger
{
    /// <summary>The time between two reminders while a service stays down.</summary>
    public static readonly TimeSpan DefaultReminderEvery = TimeSpan.FromHours(1);

    /// <summary>The least time between two full lines about different reasons in the same episode.</summary>
    public static readonly TimeSpan DefaultReasonChangeGap = TimeSpan.FromMinutes(5);

    private readonly ILogger? _logger;
    private readonly OutageWording _wording;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _reminderEvery;
    private readonly TimeSpan _reasonChangeGap;
    private readonly object _gate = new();
    private bool _down;
    private long _startedAt;
    private long _lastFullLineAt;
    private int _attempts;
    private int _reminded;
    private string? _reasonKey;

    /// <param name="logger">Receives the lines (nothing is written when null).</param>
    /// <param name="wording">The words of the service.</param>
    /// <param name="clock">Dates the episode (a fake one in tests).</param>
    /// <param name="reminderEvery">The time between reminders; <see cref="DefaultReminderEvery"/> by default.</param>
    /// <param name="reasonChangeGap">The least time between full lines about different reasons; <see cref="DefaultReasonChangeGap"/> by default.</param>
    public OutageLogger(ILogger? logger, OutageWording wording, TimeProvider? clock = null, TimeSpan? reminderEvery = null, TimeSpan? reasonChangeGap = null)
    {
        ArgumentNullException.ThrowIfNull(wording);

        _logger = logger;
        _wording = wording;
        _clock = clock ?? TimeProvider.System;
        _reminderEvery = reminderEvery is { } every && every > TimeSpan.Zero ? every : DefaultReminderEvery;
        _reasonChangeGap = reasonChangeGap ?? DefaultReasonChangeGap;
    }

    /// <summary>Whether an episode is open (a failure was seen and no success since).</summary>
    public bool IsDown => Volatile.Read(ref _down);

    /// <summary>The failures of the open episode (0 when none).</summary>
    public int Attempts
    {
        get
        {
            lock (_gate)
            {
                return _down ? _attempts : 0;
            }
        }
    }

    /// <summary>"40 s", "5 min", "2 h", "2 h 5 min": how long an outage lasted, as the lines say it.</summary>
    public static string FormatDuration(TimeSpan duration)
    {
        var seconds = (long)Math.Floor(Math.Max(0, duration.TotalSeconds));
        if (seconds < 60)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{Math.Max(1, seconds)} s");
        }

        var minutes = seconds / 60;
        if (minutes < 60)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{minutes} min");
        }

        var hours = minutes / 60;
        var rest = minutes % 60;
        return rest == 0
            ? string.Create(CultureInfo.InvariantCulture, $"{hours} h")
            : string.Create(CultureInfo.InvariantCulture, $"{hours} h {rest} min");
    }

    /// <summary>One more failure. Starts an episode (and logs <paramref name="line"/>) when none is open; otherwise it is silent, except for a new reason or a due reminder.</summary>
    /// <param name="reasonKey">What makes two failures "the same reason" (for example the outcome or the exception type), not the full text.</param>
    /// <param name="line">The full line for the log (it already says the reason and the facts; never a secret).</param>
    /// <param name="currentReason">A short reason for the reminder lines; none when null.</param>
    /// <param name="level">The level of the full line; Warning by default.</param>
    public void Failed(string reasonKey, string line, string? currentReason = null, LogLevel level = LogLevel.Warning)
    {
        ArgumentNullException.ThrowIfNull(reasonKey);
        ArgumentNullException.ThrowIfNull(line);

        string? text = null;
        var textLevel = level;
        int attempt;
        lock (_gate)
        {
            var now = _clock.GetTimestamp();
            if (!_down)
            {
                _down = true;
                _startedAt = now;
                _lastFullLineAt = now;
                _attempts = 1;
                _reminded = 0;
                _reasonKey = reasonKey;
                text = line;
            }
            else
            {
                _attempts++;
                var periods = (int)(_clock.GetElapsedTime(_startedAt, now) / _reminderEvery);
                var reminderDue = periods > _reminded;
                if (reminderDue)
                {
                    _reminded = periods;
                }

                if (!string.Equals(reasonKey, _reasonKey, StringComparison.Ordinal) && _clock.GetElapsedTime(_lastFullLineAt, now) >= _reasonChangeGap)
                {
                    _reasonKey = reasonKey;
                    _lastFullLineAt = now;
                    text = line;
                }
                else if (reminderDue)
                {
                    text = _wording.Subject + " continua " + _wording.DownText + " h\U000000E1 " + FormatDuration(_reminderEvery * periods)
                        + (string.IsNullOrEmpty(currentReason) ? string.Empty : " (" + currentReason + ")");
                    textLevel = LogLevel.Information;
                }
            }

            attempt = _attempts;
        }

        if (text is not null)
        {
            _logger?.Log(textLevel, "{Message}", text);
        }
        else
        {
            _logger?.LogDebug("{Subject}: ainda {Down} (tentativa {Attempt})", _wording.Subject, _wording.DownText, attempt);
        }
    }

    /// <summary>A success. Ends the open episode with ONE Information line; does nothing when none is open.</summary>
    public void Succeeded()
    {
        if (!Volatile.Read(ref _down))
        {
            return; // the common case (a frame loop calls this thirty times a second): no lock
        }

        TimeSpan lasted;
        int attempts;
        lock (_gate)
        {
            if (!_down)
            {
                return;
            }

            lasted = _clock.GetElapsedTime(_startedAt, _clock.GetTimestamp());
            attempts = _attempts;
            _down = false;
            _attempts = 0;
            _reasonKey = null;
        }

        _logger?.LogInformation(
            "{Message}",
            _wording.Subject + " " + _wording.RecoveredText + " ap\U000000F3s " + FormatDuration(lasted) + ", " + attempts.ToString(CultureInfo.InvariantCulture) + (attempts == 1 ? " tentativa" : " tentativas"));
    }

    /// <summary>Ends the open episode without a word: the thing that was failing is no longer asked (the feature was switched off, the address changed).</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _down = false;
            _attempts = 0;
            _reasonKey = null;
        }
    }
}
