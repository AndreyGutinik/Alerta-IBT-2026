using AlertaIBT.Core.Correction;
using Microsoft.Extensions.Logging;

namespace AlertaIBT.Core.Services;

/// <summary>What the monitor needs to know about the settings, read on every decision.</summary>
/// <param name="Enabled">The correction is switched on.</param>
/// <param name="GeminiMode">The mode is Gemini (not offline only).</param>
/// <param name="Key">The Gemini API key in clear text, or <c>null</c>/blank when there is none; never logged.</param>
/// <param name="Model">The Gemini model id.</param>
public sealed record GeminiProbeSettings(bool Enabled, bool GeminiMode, string? Key, string Model);

/// <summary>
/// Keeps the Gemini dot honest without waiting for the first alert: it probes Gemini a few seconds after the app starts,
/// when the key, the model or the correction switch change (debounced), and every 30 minutes while the app is idle. A real
/// correction is a probe too: the periodic probe is skipped while one has updated the health in the last 20 minutes.
/// After a failure it retries with backoff (30 s, 1 min, 2 min... up to 30 min) so a dead network or an exhausted quota is
/// not hammered. It never probes with the correction off, in the offline mode or without a key (the dot says "Sem chave").
/// </summary>
/// <remarks>Thread-safe. Timers run on the thread pool; the hub and the pipeline raise their events on those threads.</remarks>
public sealed class GeminiHealthMonitor : IDisposable
{
    /// <summary>The first probe, after the app has started (so it does not compete with the other start-up work).</summary>
    public static readonly TimeSpan StartDelay = TimeSpan.FromSeconds(8);

    /// <summary>How long a settings change waits for the next one before probing.</summary>
    public static readonly TimeSpan Debounce = TimeSpan.FromSeconds(3);

    /// <summary>The time between two probes while all is well.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(30);

    /// <summary>The periodic probe is skipped while a real call is newer than this.</summary>
    public static readonly TimeSpan RealCallWindow = TimeSpan.FromMinutes(20);

    /// <summary>The wait after the first failure; it doubles on every failure after that.</summary>
    public static readonly TimeSpan BackoffStart = TimeSpan.FromSeconds(30);

    /// <summary>The longest wait after failures.</summary>
    public static readonly TimeSpan BackoffMax = TimeSpan.FromMinutes(30);

    private static readonly TimeSpan AgeTick = TimeSpan.FromMinutes(1);

    private readonly IGeminiHealthProbe _probe;
    private readonly CorrectionPipeline _pipeline;
    private readonly StatusHub _hub;
    private readonly Func<GeminiProbeSettings> _settings;
    private readonly TimeProvider _clock;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _gate = new();

    private ITimer? _probeTimer;
    private ITimer? _ageTimer;
    private GeminiProbeSettings? _last;
    private bool _periodic;
    private bool _running;
    private bool _rerun;
    private bool _disposed;
    private int _failures;
    private int _generation;

    /// <param name="probe">Sends the probe.</param>
    /// <param name="pipeline">Hears what the probe found, as a real call would; tells when the last real call was.</param>
    /// <param name="hub">Shows the result on the Gemini dot.</param>
    /// <param name="settings">Gives the current settings.</param>
    /// <param name="clock">Time source of the timers; the system clock when omitted.</param>
    /// <param name="logger">Receives the unexpected errors of the monitor itself.</param>
    public GeminiHealthMonitor(
        IGeminiHealthProbe probe,
        CorrectionPipeline pipeline,
        StatusHub hub,
        Func<GeminiProbeSettings> settings,
        TimeProvider? clock = null,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(hub);
        ArgumentNullException.ThrowIfNull(settings);

        _probe = probe;
        _pipeline = pipeline;
        _hub = hub;
        _settings = settings;
        _clock = clock ?? TimeProvider.System;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    }

    /// <summary>The wait before the retry that follows the <paramref name="failures"/>-th failure in a row.</summary>
    public static TimeSpan BackoffFor(int failures)
    {
        if (failures <= 1)
        {
            return BackoffStart;
        }

        // 30 s * 2^(n-1), capped (the exponent is capped first so it cannot overflow).
        var doubled = BackoffStart * Math.Pow(2, Math.Min(failures - 1, 16));
        return doubled > BackoffMax ? BackoffMax : doubled;
    }

    /// <summary>Starts the timers; the first probe comes <see cref="StartDelay"/> later. Calling it again does nothing.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_disposed || _probeTimer is not null)
            {
                return;
            }

            _last = ReadSettings();
            _probeTimer = _clock.CreateTimer(OnProbeTimer, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _ageTimer = _clock.CreateTimer(OnAgeTimer, null, AgeTick, AgeTick);
            Schedule(StartDelay, periodic: false);
        }
    }

    /// <summary>
    /// Call after a setting changed (and was saved): when the key, the model or the correction switch is different, a probe
    /// follows <see cref="Debounce"/> later, and the backoff starts over.
    /// </summary>
    public void OnSettingsChanged()
    {
        lock (_gate)
        {
            if (_disposed || _probeTimer is null)
            {
                return;
            }

            var current = ReadSettings();
            if (current == _last)
            {
                return;
            }

            _last = current;
            _failures = 0;
            _generation++;
            Schedule(Debounce, periodic: false);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _probeTimer?.Dispose();
            _ageTimer?.Dispose();
        }

        _stop.Cancel();
        _stop.Dispose();
    }

    // Reads the settings; an unreadable key counts as no key. Also keeps the dot's "Sem chave" in step.
    private GeminiProbeSettings ReadSettings()
    {
        GeminiProbeSettings settings;
        try
        {
            settings = _settings();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Nao foi possivel ler as configuracoes do Gemini");
            settings = new GeminiProbeSettings(false, false, null, string.Empty);
        }

        _hub.SetGeminiKeyMissing(string.IsNullOrWhiteSpace(settings.Key));
        return settings;
    }

    private void Schedule(TimeSpan delay, bool periodic)
    {
        _periodic = periodic;
        _probeTimer?.Change(delay, Timeout.InfiniteTimeSpan);
    }

    private void OnProbeTimer(object? state) => _ = RunSafelyAsync();

    private async Task RunSafelyAsync()
    {
        try
        {
            await RunAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro ao verificar o Gemini");
        }
    }

    private async Task RunAsync()
    {
        bool periodic;
        int generation;
        GeminiProbeSettings settings;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            if (_running)
            {
                // A debounce ended while a probe is in flight: its result is about old settings; probe again right after it.
                _rerun = true;
                return;
            }

            _running = true;
            periodic = _periodic;
            generation = _generation;
            settings = ReadSettings();
            _last = settings;
        }

        var announcedChecking = false;
        try
        {
            if (!settings.Enabled || !settings.GeminiMode || string.IsNullOrWhiteSpace(settings.Key))
            {
                // Nothing to ask: a change of the settings schedules the next look.
                return;
            }

            if (periodic && _pipeline.LastGeminiCallAt is { } lastCall)
            {
                var wait = lastCall + RealCallWindow - _clock.GetUtcNow();
                if (wait > TimeSpan.Zero)
                {
                    lock (_gate)
                    {
                        if (!_disposed && generation == _generation)
                        {
                            Schedule(wait, periodic: true);
                        }
                    }

                    return;
                }
            }

            if (_hub.GeminiState == GeminiState.Unknown)
            {
                _hub.SetGeminiChecking(true);
                announcedChecking = true;
            }

            var result = await _probe.ProbeAsync(_stop.Token).ConfigureAwait(false);
            Apply(result, generation);
        }
        finally
        {
            if (announcedChecking)
            {
                _hub.SetGeminiChecking(false);
            }

            lock (_gate)
            {
                _running = false;
                if (_rerun && !_disposed)
                {
                    _rerun = false;
                    Schedule(TimeSpan.Zero, periodic: false);
                }
            }
        }
    }

    // Shows the result and schedules the next probe, unless the settings changed while it was in flight (then the result is
    // about an old key or model, and the debounced probe is already scheduled).
    private void Apply(GeminiProbeResult result, int generation)
    {
        var now = _clock.GetUtcNow();
        lock (_gate)
        {
            if (_disposed || generation != _generation || result.Failure == GeminiFailureKind.Cancelled)
            {
                return;
            }

            switch (result.Status)
            {
                case GeminiProbeStatus.Online:
                    _failures = 0;
                    Schedule(Interval, periodic: true);
                    break;

                case GeminiProbeStatus.Failed:
                    _failures++;
                    Schedule(BackoffFor(_failures), periodic: false);
                    break;

                default:
                    // No key: the dot says so; setting a key schedules the next probe.
                    break;
            }
        }

        // The hub and the pipeline raise events: outside the lock.
        switch (result.Status)
        {
            case GeminiProbeStatus.Online:
                _pipeline.ReportProbe(true);
                _hub.SetGeminiProbe(true, null, now);
                break;

            case GeminiProbeStatus.Failed:
                _pipeline.ReportProbe(false);
                _hub.SetGeminiProbe(false, result.Reason, now);
                break;

            default:
                _hub.SetGeminiKeyMissing(true);
                break;
        }
    }

    // Once a minute: a real call newer than the last probe moves the "checked at" time, and the age in the tooltip moves on.
    private void OnAgeTimer(object? state)
    {
        try
        {
            if (_pipeline.LastGeminiCallAt is { } lastCall)
            {
                _hub.SetGeminiCheckedAt(lastCall);
            }

            _hub.RefreshGeminiAge();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro ao atualizar a idade da verificacao do Gemini");
        }
    }
}
