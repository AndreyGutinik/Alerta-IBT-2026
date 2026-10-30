using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AlertaIBT.Core.Diagnostics;

/// <summary>
/// Notices when the screen (the UI thread) stops answering and writes it to the log. A timer on a background thread asks the UI thread to answer a
/// tiny message every <see cref="PingEvery"/>; when an answer is more than <see cref="Threshold"/> late it logs a warning "Interface parada ha X s" with the
/// <see cref="BreadcrumbTrail"/> (what the app did just before) and, when the UI answers again, how long it was stopped. While the stall goes on it
/// repeats the warning every <see cref="RepeatEvery"/>. The managed stack of the UI thread cannot be read from another thread without a debugger (.NET
/// has no safe way), so the breadcrumbs and the <c>context</c> text (counters of the process) are what the log gets. The cost is one delegate post
/// every 250 ms and a few field reads.
/// </summary>
public sealed class UiStallWatchdog : IDisposable
{
    /// <summary>How often the UI thread is asked to answer.</summary>
    public static readonly TimeSpan PingEvery = TimeSpan.FromMilliseconds(250);

    /// <summary>The wait after which the UI thread counts as stopped.</summary>
    public static readonly TimeSpan Threshold = TimeSpan.FromSeconds(2);

    /// <summary>While a stall lasts the warning is repeated this often.</summary>
    public static readonly TimeSpan RepeatEvery = TimeSpan.FromSeconds(10);

    private readonly Action<Action> _postToUi;
    private readonly ILogger _logger;
    private readonly TimeProvider _clock;
    private readonly BreadcrumbTrail _trail;
    private readonly Func<string>? _context;
    private readonly object _gate = new();
    private ITimer? _timer;
    private long? _pingSentAt;       // timestamp of the ping not yet answered; null when none is waiting
    private long? _stallStartedAt;   // when the answer that never came was asked; null when not stalled
    private long _lastWarnedAt;
    private long? _lastTickAt;
    private TimeSpan _longest;
    private int _stalls;
    private bool _disposed;

    /// <param name="postToUi">Queues an action on the UI thread (it must run after the input events, never at the top priority).</param>
    /// <param name="logger">Receives the warnings.</param>
    /// <param name="clock">Times the pings; a fake clock in tests.</param>
    /// <param name="trail">What the app did last.</param>
    /// <param name="context">Extra text for the warning (counters of the process), or <c>null</c>; called on the timer thread.</param>
    public UiStallWatchdog(Action<Action> postToUi, ILogger? logger = null, TimeProvider? clock = null, BreadcrumbTrail? trail = null, Func<string>? context = null)
    {
        ArgumentNullException.ThrowIfNull(postToUi);

        _postToUi = postToUi;
        _logger = logger ?? NullLogger.Instance;
        _clock = clock ?? TimeProvider.System;
        _trail = trail ?? BreadcrumbTrail.Default;
        _context = context;
    }

    /// <summary>The longest stall seen so far.</summary>
    public TimeSpan LongestStall
    {
        get
        {
            lock (_gate)
            {
                return _longest;
            }
        }
    }

    /// <summary>How many stalls were seen.</summary>
    public int Stalls
    {
        get
        {
            lock (_gate)
            {
                return _stalls;
            }
        }
    }

    /// <summary>Whether the UI thread is stopped right now.</summary>
    public bool IsStalled
    {
        get
        {
            lock (_gate)
            {
                return _stallStartedAt is not null;
            }
        }
    }

    /// <summary>Starts the background timer.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_timer is not null || _disposed)
            {
                return;
            }

            _timer = _clock.CreateTimer(_ => Tick(), null, PingEvery, PingEvery);
        }
    }

    /// <summary>One look (the timer calls it; a test calls it by advancing the fake clock).</summary>
    public void Tick()
    {
        var now = _clock.GetTimestamp();
        bool send;
        string? warning = null;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            // The timer itself did not run for a long time: the PC slept (or the whole process was frozen). That is not the UI's fault: start over.
            if (_lastTickAt is { } last && _clock.GetElapsedTime(last, now) > Threshold && _stallStartedAt is null)
            {
                _pingSentAt = null;
            }

            _lastTickAt = now;
            send = _pingSentAt is null;
            if (send)
            {
                _pingSentAt = now;
            }
            else
            {
                var waited = _clock.GetElapsedTime(_pingSentAt!.Value, now);
                if (waited > Threshold)
                {
                    var first = _stallStartedAt is null;
                    if (first)
                    {
                        _stallStartedAt = _pingSentAt;
                        _stalls++;
                    }

                    if (first || _clock.GetElapsedTime(_lastWarnedAt, now) >= RepeatEvery)
                    {
                        _lastWarnedAt = now;
                        warning = Describe(waited, first);
                    }
                }
            }
        }

        if (warning is not null)
        {
            _logger.LogWarning("{Warning}", warning);
        }

        if (send)
        {
            try
            {
                _postToUi(OnAnswered);
            }
            catch (Exception ex)
            {
                // The dispatcher is going away (the app is closing): nothing more to watch.
                _logger.LogDebug(ex, "O vigia da interface nao conseguiu perguntar");
                lock (_gate)
                {
                    _pingSentAt = null;
                }
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        ITimer? timer;
        lock (_gate)
        {
            _disposed = true;
            timer = _timer;
            _timer = null;
        }

        timer?.Dispose();
    }

    // Runs on the UI thread: it answers.
    private void OnAnswered()
    {
        string? recovered = null;
        var now = _clock.GetTimestamp();
        lock (_gate)
        {
            if (_stallStartedAt is { } started)
            {
                var lasted = _clock.GetElapsedTime(started, now);
                if (lasted > _longest)
                {
                    _longest = lasted;
                }

                recovered = string.Create(CultureInfo.InvariantCulture, $"Interface voltou a responder; ficou parada por {lasted.TotalSeconds:0.0} s");
                _stallStartedAt = null;
            }

            _pingSentAt = null;
        }

        if (recovered is not null)
        {
            _logger.LogWarning("{Recovered}", recovered);
        }
    }

    private string Describe(TimeSpan waited, bool first)
    {
        var now = _clock.GetUtcNow();
        string extra;
        try
        {
            extra = _context?.Invoke() is { Length: > 0 } text ? " [" + text + "]" : string.Empty;
        }
        catch (Exception)
        {
            extra = string.Empty;
        }

        var head = string.Create(CultureInfo.InvariantCulture, $"Interface {(first ? "parada" : "continua parada")} ha {waited.TotalSeconds:0.0} s");
        return $"{head}{extra}. Ultimas operacoes: {_trail.Format(now)}";
    }
}
