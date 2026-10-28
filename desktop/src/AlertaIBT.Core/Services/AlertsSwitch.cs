using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AlertaIBT.Core.Services;

/// <summary>One part of the alert side of the app that the master switch starts and stops (the alert queue, Telegram, the site).</summary>
/// <param name="Name">What the log calls it.</param>
/// <param name="Start">Starts it (again, after a stop). Returns at once.</param>
/// <param name="Stop">Stops it and waits until it is quiet.</param>
public sealed record AlertsPart(string Name, Action Start, Func<Task> Stop);

/// <summary>
/// The master switch "Alertas ligados" (for a smaller temple that only wants the sound): off stops the alert queue (so the bar on the LED
/// ends and hides), Telegram and the site connector; on starts them again, cleanly. The parts start in the order given and stop in the
/// reverse order (the sources of alerts first, the queue last). Everything else (the player, the mouse lock, the history, the updates) is
/// not part of it. The switch itself is the setting <c>System.AlertsEnabled</c>; this class only follows <paramref name="wanted"/>.
/// </summary>
/// <remarks>
/// <see cref="Initialize"/> starts what is wanted at once (the app's start). After that <see cref="Request"/> returns immediately and the work
/// goes on a background task, one change at a time: the last value wins, so quick flips end in the last one. A part that fails is logged and
/// the others still go.
/// </remarks>
public sealed class AlertsSwitch : IDisposable
{
    private readonly Func<bool> _wanted;
    private readonly IReadOnlyList<AlertsPart> _parts;
    private readonly StatusHub? _hub;
    private readonly ILogger _logger;
    private readonly object _gate = new();

    private bool _running;
    private bool _working;
    private bool _disposed;
    private Task _worker = Task.CompletedTask;

    /// <param name="wanted">What the setting says now.</param>
    /// <param name="parts">The parts, in start order.</param>
    /// <param name="hub">Told whether the alerts are on, so the windows show it; may be <c>null</c>.</param>
    /// <param name="logger">Receives what happens.</param>
    public AlertsSwitch(Func<bool> wanted, IReadOnlyList<AlertsPart> parts, StatusHub? hub = null, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(wanted);
        ArgumentNullException.ThrowIfNull(parts);

        _wanted = wanted;
        _parts = parts;
        _hub = hub;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>Raised after the parts were started or stopped, with whether the alerts are on now. On a background thread.</summary>
    public event Action<bool>? Changed;

    /// <summary>Whether the parts are running.</summary>
    public bool IsOn
    {
        get
        {
            lock (_gate)
            {
                return _running;
            }
        }
    }

    /// <summary>The start of the app: starts the parts when the setting is on (and only then), before it returns.</summary>
    public void Initialize()
    {
        var on = _wanted();
        lock (_gate)
        {
            if (_disposed || _running)
            {
                return;
            }
        }

        _hub?.SetAlertsOn(on);
        if (!on)
        {
            _logger.LogInformation("Alertas desligados: a barra, o Telegram e o site nao iniciam");
            return;
        }

        StartParts();
        lock (_gate)
        {
            _running = true;
        }
    }

    /// <summary>The setting may have changed: brings the parts in line with it, in the background. Returns at once.</summary>
    public void Request()
    {
        lock (_gate)
        {
            if (_disposed || _working)
            {
                return;
            }

            _working = true;
            _worker = Task.Run(WorkAsync);
        }
    }

    /// <summary>Waits until every change asked so far has been carried out (for tests and for shutdown).</summary>
    public Task SettledAsync()
    {
        lock (_gate)
        {
            return _worker;
        }
    }

    /// <summary>Stops following the setting. Does not stop the parts: whoever owns them disposes them.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
        }

        try
        {
            _worker.Wait(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro ao parar o interruptor dos alertas");
        }
    }

    private async Task WorkAsync()
    {
        while (true)
        {
            var want = _wanted();
            lock (_gate)
            {
                if (_disposed || want == _running)
                {
                    _working = false;
                    return;
                }
            }

            // The windows learn first (the banner, the quick panel); then the parts follow.
            _hub?.SetAlertsOn(want);
            if (want)
            {
                _logger.LogInformation("Alertas ligados: iniciando a fila, o Telegram e o site");
                StartParts();
            }
            else
            {
                _logger.LogInformation("Alertas desligados: parando o site, o Telegram e a fila");
                await StopPartsAsync().ConfigureAwait(false);
            }

            lock (_gate)
            {
                _running = want;
            }

            try
            {
                Changed?.Invoke(want);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao avisar a mudanca dos alertas");
            }
        }
    }

    private void StartParts()
    {
        foreach (var part in _parts)
        {
            try
            {
                part.Start();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Nao foi possivel iniciar {What}", part.Name);
                _hub?.ReportError("Alertas", ex);
            }
        }
    }

    private async Task StopPartsAsync()
    {
        foreach (var part in _parts.Reverse())
        {
            try
            {
                await part.Stop().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Nao foi possivel parar {What}", part.Name);
                _hub?.ReportError("Alertas", ex);
            }
        }
    }
}
