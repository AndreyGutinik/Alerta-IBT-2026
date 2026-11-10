using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AlertaIBT.Core.Diagnostics;

/// <summary>
/// The steps of closing the app, one by one: each is named in the trail, timed (a slow one is told in the log) and may be bounded (a step that waits for a
/// Windows service that never answers is given up after its limit instead of freezing the exit). Closing used to run on the UI thread with no limit, and a
/// quit that froze left the process alive for a day with no window, no tray icon and the single-instance lock held (the log of 2026-11-06 and 11-08: "encerrando"
/// and then nothing). <see cref="Current"/> is what <see cref="ShutdownGuard"/> reports when it has to give up.
/// </summary>
public static class ShutdownSteps
{
    /// <summary>A step slower than this is written to the log.</summary>
    public static readonly TimeSpan SlowAfter = TimeSpan.FromMilliseconds(500);

    private static volatile string _current = string.Empty;
    private static ILogger _logger = NullLogger.Instance;

    /// <summary>The step running now, or empty when none is.</summary>
    public static string Current => _current;

    /// <summary>Where slow or failed steps are told (the app's logger).</summary>
    public static void UseLogger(ILogger? logger) => _logger = logger ?? NullLogger.Instance;

    /// <summary>Runs <paramref name="step"/> named <paramref name="name"/>: traced, timed, and an exception is logged and swallowed so the next step still runs.</summary>
    public static void Run(string name, Action step)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(step);

        _current = name;
        Breadcrumbs.Add("encerrando: " + name);
        var clock = Stopwatch.StartNew();
        try
        {
            step();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "Encerrando: o passo {Step} falhou", name);
        }
        finally
        {
            Finished(name, clock.Elapsed);
        }
    }

    /// <summary>
    /// Like <see cref="Run"/>, but on a background thread and given up after <paramref name="limit"/>: returns false when the step did not finish in time (it may still be
    /// running; the process is about to end anyway). Only for steps that need no UI thread.
    /// </summary>
    public static bool RunBounded(string name, Action step, TimeSpan limit)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(step);

        _current = name;
        Breadcrumbs.Add("encerrando: " + name);
        var clock = Stopwatch.StartNew();
        var worker = new Thread(() =>
        {
            try
            {
                step();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.LogWarning(ex, "Encerrando: o passo {Step} falhou", name);
            }
        })
        { IsBackground = true, Name = "encerrando " + name };
        worker.Start();
        var finished = worker.Join(limit);
        if (!finished)
        {
            _logger.LogWarning("Encerrando: o passo {Step} nao terminou em {Seconds:0.#} s (alguma chamada ao Windows nao volta); o encerramento segue sem ele", name, limit.TotalSeconds);
            Breadcrumbs.Add("encerrando: " + name + " nao terminou");
        }

        Finished(name, clock.Elapsed);
        return finished;
    }

    private static void Finished(string name, TimeSpan took)
    {
        if (took >= SlowAfter)
        {
            _logger.LogInformation("Encerrando: o passo {Step} levou {Millis:0} ms", name, took.TotalMilliseconds);
        }
    }
}

/// <summary>
/// The last line of defence of closing the app. <see cref="Begin"/> starts a countdown; when <see cref="Complete"/> is not called within the limit it calls the
/// <c>onStuck</c> action with the step that was running (the app then writes the log line and the session marker and ends the process by force). A quit that
/// froze left a "zombie" process with no window; with this the worst a stuck quit can do is take <c>limit</c> seconds.
/// </summary>
public sealed class ShutdownGuard : IDisposable
{
    private readonly TimeSpan _limit;
    private readonly Action<string, TimeSpan> _onStuck;
    private readonly TimeProvider _clock;
    private readonly object _gate = new();
    private ITimer? _timer;
    private long _startedAt;
    private bool _completed;
    private bool _fired;

    /// <param name="limit">How long closing may take.</param>
    /// <param name="onStuck">Called once, on a timer thread, with the step that was running and how long closing has taken.</param>
    /// <param name="clock">The clock; a fake in tests.</param>
    public ShutdownGuard(TimeSpan limit, Action<string, TimeSpan> onStuck, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(onStuck);

        _limit = limit;
        _onStuck = onStuck;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>Whether the guard had to give up.</summary>
    public bool Fired
    {
        get
        {
            lock (_gate)
            {
                return _fired;
            }
        }
    }

    /// <summary>Starts the countdown.</summary>
    public void Begin()
    {
        lock (_gate)
        {
            if (_timer is not null || _completed)
            {
                return;
            }

            _startedAt = _clock.GetTimestamp();
            _timer = _clock.CreateTimer(_ => Check(), null, _limit, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Closing finished: the countdown stops.</summary>
    public void Complete()
    {
        ITimer? timer;
        lock (_gate)
        {
            _completed = true;
            timer = _timer;
            _timer = null;
        }

        timer?.Dispose();
    }

    /// <inheritdoc />
    public void Dispose() => Complete();

    // The timer fired (a test calls it by advancing the fake clock).
    private void Check()
    {
        TimeSpan took;
        lock (_gate)
        {
            if (_completed || _fired)
            {
                return;
            }

            _fired = true;
            took = _clock.GetElapsedTime(_startedAt);
        }

        _onStuck(ShutdownSteps.Current, took);
    }
}
