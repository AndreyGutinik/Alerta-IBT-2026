using System.Globalization;
using AlertaIBT.Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AlertaIBT.Core.Player;

/// <summary>What an alarm asks of the player (the <see cref="MusicPlayer"/>; a fake in tests).</summary>
public interface IAlarmTarget
{
    /// <summary>Starts the playlist in shuffle and repeat-all with the alarm's fade-in, unless something is already playing.</summary>
    AlarmStart StartFromAlarm(string playlistName);
}

/// <summary>The pure rules of the alarms, on local wall-clock time (so a daylight-saving change never moves "18:00").</summary>
public static class AlarmRules
{
    /// <summary>The local date as it is saved in <see cref="AlarmEntry.LastFiredOn"/>.</summary>
    public static string DateKey(DateTime local) => local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>
    /// The alarms that should start now: enabled, today is one of their days, the time is from the start to the "do not start after" time
    /// (both included: a PC that opens late still starts, a PC that opens after the latest time does not), and not fired today yet.
    /// Empty when the player or the alarms master switch is off.
    /// </summary>
    public static IReadOnlyList<AlarmEntry> Due(PlayerSettings player, DateTime localNow)
    {
        ArgumentNullException.ThrowIfNull(player);

        if (!player.Enabled || !player.AlarmsEnabled)
        {
            return [];
        }

        var today = DateKey(localNow);
        var time = TimeOnly.FromDateTime(localNow);
        return player.Alarms
            .Where(a => a.Enabled
                && a.HasDay(localNow.DayOfWeek)
                && time >= a.StartTime
                && time <= a.LatestTime
                && a.LastFiredOn != today)
            .ToList();
    }

    /// <summary>The next start time (local) among the enabled alarms, or <c>null</c> when none will ever start.</summary>
    public static (DateTime When, AlarmEntry Alarm)? Next(PlayerSettings player, DateTime localNow)
    {
        ArgumentNullException.ThrowIfNull(player);

        (DateTime When, AlarmEntry Alarm)? best = null;
        var today = DateKey(localNow);
        var time = TimeOnly.FromDateTime(localNow);
        for (var offset = 0; offset <= 7; offset++)
        {
            var date = localNow.Date.AddDays(offset);
            foreach (var alarm in player.Alarms.Where(a => a.Enabled && a.HasDay(date.DayOfWeek)))
            {
                if (offset == 0 && (time > alarm.LatestTime || alarm.LastFiredOn == today))
                {
                    continue; // today's start is over
                }

                var when = date + alarm.StartTime.ToTimeSpan();
                if (best is null || when < best.Value.When)
                {
                    best = (when, alarm);
                }
            }
        }

        return best;
    }

    /// <summary>The line for the screen: <c>Proximo despertador: domingo 18:00</c>, or why there is none.</summary>
    public static string Describe(PlayerSettings player, DateTime localNow)
    {
        ArgumentNullException.ThrowIfNull(player);

        if (!player.Enabled)
        {
            return "Player desligado";
        }

        if (!player.AlarmsEnabled)
        {
            return "Despertadores desligados";
        }

        if (Next(player, localNow) is not { } next)
        {
            return "Nenhum despertador ativo";
        }

        var day = next.When.Date == localNow.Date ? "hoje" : DayName(next.When.DayOfWeek);
        return $"Pr\U000000F3ximo despertador: {day} {next.When.ToString("HH:mm", CultureInfo.InvariantCulture)}";
    }

    /// <summary>The weekday in Portuguese, short form (<c>domingo</c>, <c>segunda</c>, ...).</summary>
    public static string DayName(DayOfWeek day) => day switch
    {
        DayOfWeek.Sunday => "domingo",
        DayOfWeek.Monday => "segunda",
        DayOfWeek.Tuesday => "ter\U000000E7a",
        DayOfWeek.Wednesday => "quarta",
        DayOfWeek.Thursday => "quinta",
        DayOfWeek.Friday => "sexta",
        _ => "s\U000000E1bado",
    };
}

/// <summary>
/// Starts the music at the alarm times ("despertadores"). It looks every <see cref="CheckInterval"/>, when the app starts and when a setting
/// changes, so a PC that opens late still starts (until the "do not start after" time) and an edited alarm is judged at once. An alarm
/// fires at most once a day: the date is saved in the settings, so a restart does not fire it twice. It never stops the music.
/// </summary>
public sealed class AlarmScheduler : IDisposable
{
    /// <summary>How often the clock is looked at.</summary>
    public static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(15);

    private readonly SettingsService _settings;
    private readonly IAlarmTarget _target;
    private readonly TimeProvider _clock;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private readonly HashSet<string> _reportedFailures = [];
    private ITimer? _timer;
    private bool _checking;
    private bool _again;
    private bool _disposed;
    private string _nextText = string.Empty;

    /// <param name="settings">The settings (the alarms and the master switches; the fired date is saved there).</param>
    /// <param name="target">The player.</param>
    /// <param name="clock">The time of day and the check timer.</param>
    /// <param name="logger">Receives what fired and what failed.</param>
    public AlarmScheduler(SettingsService settings, IAlarmTarget target, TimeProvider? clock = null, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(target);

        _settings = settings;
        _target = target;
        _clock = clock ?? TimeProvider.System;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>Raised (on any thread) when <see cref="NextAlarmText"/> changed.</summary>
    public event Action? NextAlarmChanged;

    /// <summary>The line for the screen, such as <c>Proximo despertador: domingo 18:00</c>.</summary>
    public string NextAlarmText
    {
        get
        {
            lock (_gate)
            {
                return _nextText.Length > 0 ? _nextText : AlarmRules.Describe(_settings.Current.Player, Now());
            }
        }
    }

    /// <summary>Starts looking: once now (an alarm missed while the app was closed starts at once) and then on a timer.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_disposed || _timer is not null)
            {
                return;
            }

            _timer = _clock.CreateTimer(_ => Check(), null, CheckInterval, CheckInterval);
        }

        _settings.Changed += OnSettingsChanged;
        Check();
    }

    /// <summary>Looks at the clock now: starts the alarms that are due and refreshes the next-alarm line. Never throws.</summary>
    public void Check()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            if (_checking)
            {
                _again = true; // a change made by this very check: look once more when it ends
                return;
            }

            _checking = true;
        }

        try
        {
            do
            {
                lock (_gate)
                {
                    _again = false;
                }

                FireDue();
            }
            while (TakeAgain());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro ao verificar os despertadores");
        }
        finally
        {
            lock (_gate)
            {
                _checking = false;
            }

            RefreshText();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        ITimer? timer;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            timer = _timer;
            _timer = null;
        }

        _settings.Changed -= OnSettingsChanged;
        timer?.Dispose();
    }

    private bool TakeAgain()
    {
        lock (_gate)
        {
            return _again && !_disposed;
        }
    }

    private DateTime Now() => _clock.GetLocalNow().DateTime;

    private void FireDue()
    {
        var now = Now();
        var today = AlarmRules.DateKey(now);
        foreach (var alarm in AlarmRules.Due(_settings.Current.Player, now))
        {
            var start = _target.StartFromAlarm(alarm.Playlist);
            switch (start.Outcome)
            {
                case AlarmStartOutcome.Started:
                    _logger.LogInformation("Despertador {Alarm}: musica iniciada ({Playlist})", alarm.Id, alarm.Playlist);
                    MarkFired(alarm.Id, today);
                    break;
                case AlarmStartOutcome.AlreadyPlaying:
                    _logger.LogInformation("Despertador {Alarm}: ja ha musica tocando, nada mudou", alarm.Id);
                    MarkFired(alarm.Id, today);
                    break;
                default:
                    // Not marked: the next look tries again until the "do not start after" time (the folder may still be loading).
                    if (_reportedFailures.Add(alarm.Id + today))
                    {
                        _logger.LogWarning("Despertador {Alarm}: nao foi possivel iniciar ({Reason}); tentando de novo ate {Latest}", alarm.Id, start.Error, alarm.Latest);
                    }

                    break;
            }
        }
    }

    private void MarkFired(string id, string today) =>
        _settings.Update(s => s with
        {
            Player = s.Player with
            {
                Alarms = s.Player.Alarms.Select(a => a.Id == id ? a with { LastFiredOn = today } : a).ToList(),
            },
        });

    private void OnSettingsChanged(AppSettings settings) => Check();

    private void RefreshText()
    {
        string text;
        bool changed;
        lock (_gate)
        {
            text = AlarmRules.Describe(_settings.Current.Player, Now());
            changed = text != _nextText;
            _nextText = text;
        }

        if (changed)
        {
            NextAlarmChanged?.Invoke();
        }
    }
}
