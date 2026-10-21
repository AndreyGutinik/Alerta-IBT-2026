using AlertaIBT.App.Overlay;
using AlertaIBT.App.Platform;
using AlertaIBT.Core.Correction;
using AlertaIBT.Core.Queue;
using AlertaIBT.Core.Screens;
using AlertaIBT.Core.Services;
using AlertaIBT.Core.Settings;
using Microsoft.Extensions.Logging;

namespace AlertaIBT.App.Services;

/// <summary>
/// Starts and stops the working parts of the app together: the saved queue, the alert service that plays it on the bar, the
/// Telegram engine and the local API, and keeps the <see cref="StatusHub"/> up to date with what the platform knows (the LED, the
/// capture exclusion, the correction mode). Everything with logic of its own lives in Core; this is the glue.
/// </summary>
public sealed class AlertRuntime : IDisposable
{
    private readonly AlertService _alerts;
    private readonly TelegramEngine _telegram;
    private readonly HttpControlServer _http;
    private readonly QueueStore _store;
    private readonly AlertQueue _queue;
    private readonly TelegramGroupNotifier _notifier;
    private readonly LazyCorrector _offline;
    private readonly StatusHub _hub;
    private readonly OverlayController _overlay;
    private readonly OverlayDisplay _display;
    private readonly LedWatcher _led;
    private readonly SettingsService _settings;
    private readonly ILogger<AlertRuntime> _logger;
    private int _started;
    private int _stopped;

    /// <summary>Wires nothing yet; <see cref="Start"/> does.</summary>
    public AlertRuntime(
        AlertService alerts,
        TelegramEngine telegram,
        HttpControlServer http,
        QueueStore store,
        AlertQueue queue,
        TelegramGroupNotifier notifier,
        LazyCorrector offline,
        StatusHub hub,
        OverlayController overlay,
        OverlayDisplay display,
        LedWatcher led,
        SettingsService settings,
        ILogger<AlertRuntime> logger)
    {
        _alerts = alerts;
        _telegram = telegram;
        _http = http;
        _store = store;
        _queue = queue;
        _notifier = notifier;
        _offline = offline;
        _hub = hub;
        _overlay = overlay;
        _display = display;
        _led = led;
        _settings = settings;
        _logger = logger;
    }

    /// <summary>The alert service, for the windows.</summary>
    public AlertService Alerts => _alerts;

    /// <summary>The status of the parts, for the windows.</summary>
    public StatusHub Status => _hub;

    /// <summary>
    /// Starts everything. Call on the UI thread, after the overlay window exists (so the capture-exclusion state is known).
    /// A part that fails to start is logged and shown in the status; the others still run.
    /// </summary>
    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }

        Guarded("a fila salva", () => _store.Attach(_queue));

        _hub.SetCorrectionMode(ModeOf(_settings.Current));
        _settings.Changed += OnSettingsChanged;

        _hub.SetCaptureExcluded(_overlay.CaptureExcluded);
        _overlay.CaptureExcludedChanged += OnCaptureExcludedChanged;
        UpdateLed(_led.Led);
        _led.LedChanged += UpdateLed;

        _offline.Warm();
        Guarded("o servico de avisos", _alerts.Start);
        Guarded("o Telegram", _telegram.Start);
        Guarded("a API local", () => _http.Start());
        _logger.LogInformation("Servico de avisos iniciado: {Pending} aviso(s) na fila salva", _queue.Pending.Count);
    }

    /// <summary>The Pausar/Retomar of the tray and the panels: the sermon starts or ends.</summary>
    public void TogglePause()
    {
        if (_alerts.IsPaused)
        {
            _alerts.Resume();
        }
        else
        {
            _alerts.Pause();
        }
    }

    /// <summary>
    /// Stops everything. The alert on the bar goes back to the queue, and the queue is saved one last time. Blocks briefly:
    /// call it on the way out.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0)
        {
            return;
        }

        _settings.Changed -= OnSettingsChanged;
        _overlay.CaptureExcludedChanged -= OnCaptureExcludedChanged;
        _led.LedChanged -= UpdateLed;
        try
        {
            _telegram.Dispose();
            _http.Dispose();
            _alerts.Dispose();
            _notifier.Dispose();
            _display.Dispose();
            _store.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro ao parar o servico de avisos");
        }
    }

    private static CorrectionMode ModeOf(AppSettings settings)
    {
        if (!settings.Correction.Enabled)
        {
            return CorrectionMode.Disabled;
        }

        return string.Equals(settings.Correction.Mode, "offline", StringComparison.Ordinal) ? CorrectionMode.OfflineOnly : CorrectionMode.Gemini;
    }

    private void OnSettingsChanged(AppSettings settings) => _hub.SetCorrectionMode(ModeOf(settings));

    private void OnCaptureExcludedChanged() => _hub.SetCaptureExcluded(_overlay.CaptureExcluded);

    private void UpdateLed(ScreenInfo? screen)
    {
        _hub.SetLedDescription(screen is null ? null : $"{screen.Width}x{screen.Height} em {screen.DeviceName}" + (_led.TestMode ? " (modo de teste)" : string.Empty));
        _hub.SetLedPresent(screen is not null);
    }

    // One part failing to start must not keep the others from running: a dead API still leaves Telegram and the bar working.
    private void Guarded(string what, Action start)
    {
        try
        {
            start();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Nao foi possivel iniciar {What}", what);
            _hub.ReportError("Inicio", ex);
        }
    }
}
