using AlertaIBT.Core.Settings;
using Microsoft.Extensions.Logging;

namespace AlertaIBT.Core.Services;

/// <summary>
/// Clears the history every Monday at 00:00 (local time) while the app runs, and makes up for the Mondays the PC was off: at start,
/// and whenever <see cref="CheckNow"/> is called (the site came online), a Monday 00:00 that passed since
/// <see cref="SystemSettings.LastHistoryClearAt"/> is owed, and the history is cleared at once.
/// </summary>
/// <remarks>
/// <para>
/// The timer looks again at least every minute, so a sleeping PC that wakes, a clock that was changed and a daylight saving jump
/// are all noticed within a minute; it is shorter when Monday 00:00 is nearer, so the clear happens at 00:00 and not a minute later.
/// </para>
/// <para>
/// The first run (no <see cref="SystemSettings.LastHistoryClearAt"/>) only starts the count: nothing is cleared that the owner did
/// not ask for. While the switch is off the count moves on without clearing, so turning it on never wipes an old week.
/// </para>
/// </remarks>
public sealed class HistoryClearScheduler : IDisposable
{
    /// <summary>The longest the timer sleeps before it looks again.</summary>
    public static readonly TimeSpan RecheckEvery = TimeSpan.FromMinutes(1);

    private readonly HistoryCleaner _cleaner;
    private readonly Func<AppSettings> _settings;
    private readonly Action<Func<AppSettings, AppSettings>> _update;
    private readonly TimeProvider _clock;
    private readonly TimeZoneInfo _zone;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private ITimer? _timer;
    private int _checking;
    private bool _started;
    private bool _disposed;

    /// <param name="cleaner">Does the clearing.</param>
    /// <param name="settings">Gives the current settings.</param>
    /// <param name="update">Changes the settings (the moment the count starts from).</param>
    /// <param name="clock">The source of the current time.</param>
    /// <param name="zone">Whose midnight counts; the system time zone by default.</param>
    /// <param name="logger">Receives what happened.</param>
    public HistoryClearScheduler(
        HistoryCleaner cleaner,
        Func<AppSettings> settings,
        Action<Func<AppSettings, AppSettings>> update,
        TimeProvider? clock,
        TimeZoneInfo? zone,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(cleaner);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(update);
        ArgumentNullException.ThrowIfNull(logger);

        _cleaner = cleaner;
        _settings = settings;
        _update = update;
        _clock = clock ?? TimeProvider.System;
        _zone = zone ?? TimeZoneInfo.Local;
        _logger = logger;
    }

    /// <summary>Looks at once (the catch-up) and starts the timer. Does nothing the second time.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_started || _disposed)
            {
                return;
            }

            _started = true;
            _timer = _clock.CreateTimer(_ => Tick(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        Tick();
    }

    /// <summary>Looks now: the history is cleared if a Monday 00:00 has passed since the last clear. Never throws.</summary>
    /// <returns>A task that ends when the look (and the clear, if any) is done.</returns>
    public async Task CheckNow()
    {
        if (Interlocked.Exchange(ref _checking, 1) != 0)
        {
            return;
        }

        try
        {
            await CheckAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro na limpeza semanal do historico");
        }
        finally
        {
            Volatile.Write(ref _checking, 0);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
        }
    }

    // Raised by the timer: checks, then sets the next alarm. Async void by design: it must never throw into the timer thread.
    private async void Tick()
    {
        try
        {
            await CheckNow().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro na limpeza semanal do historico");
        }
        finally
        {
            Rearm();
        }
    }

    private void Rearm()
    {
        lock (_gate)
        {
            if (_disposed || _timer is null)
            {
                return;
            }

            var now = _clock.GetUtcNow();
            var untilMonday = HistoryClearSchedule.NextMonday(now, _zone) - now;
            var wait = untilMonday < RecheckEvery ? untilMonday : RecheckEvery;
            _timer.Change(wait > TimeSpan.Zero ? wait : TimeSpan.FromMilliseconds(1), Timeout.InfiniteTimeSpan);
        }
    }

    private async Task CheckAsync()
    {
        var now = _clock.GetUtcNow();
        var system = _settings().System;

        // First run: the count starts now. A last clear in the future (the clock was set back) starts it again.
        if (system.LastHistoryClearAt is not { } last || last > now)
        {
            Remember(now);
            return;
        }

        if (!HistoryClearSchedule.IsDue(last, now, _zone))
        {
            return;
        }

        if (!system.ClearHistoryWeekly)
        {
            Remember(now);
            return;
        }

        _logger.LogInformation("Limpeza semanal do historico (segunda-feira 00:00; ultima limpeza: {Last:u})", last);
        await _cleaner.ClearAllAsync().ConfigureAwait(false);
    }

    private void Remember(DateTimeOffset moment) =>
        _update(s => s with { System = s.System with { LastHistoryClearAt = moment } });
}
