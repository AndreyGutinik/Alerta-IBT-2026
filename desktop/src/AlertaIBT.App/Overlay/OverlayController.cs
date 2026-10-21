using AlertaIBT.Core.Overlay;
using AlertaIBT.Core.Screens;
using AlertaIBT.Core.Settings;
using Microsoft.Extensions.Logging;

namespace AlertaIBT.App.Overlay;

/// <summary>
/// Plays one alert at a time on the bar over the LED. For each alert it lays the text out once, then on every rendered frame
/// asks <see cref="BarTimeline"/> where everything is for the time elapsed since the first frame (so a dropped frame
/// never slows the text) and hands that to the surface. The pass ends with <c>true</c> when the text has crossed the whole
/// screen, or with <c>false</c> when it is cut short: cancelled, the LED vanished or moved, the surface failed, or no frame
/// arrived for too long (the caller then puts the alert back at the front of the queue).
/// </summary>
/// <remarks>All state lives on the UI thread; the public methods may be called from any thread.</remarks>
public sealed class OverlayController : IDisposable
{
    /// <summary>How long past the timeline's own length a pass may take before it is given up (frames stopped coming).</summary>
    public static readonly TimeSpan WatchdogMargin = TimeSpan.FromSeconds(5);

    private readonly SettingsService _settings;
    private readonly ILedSource _led;
    private readonly Func<IBarSurface> _surfaceFactory;
    private readonly IFrameSource _frames;
    private readonly IUiDispatcher _ui;
    private readonly ILogger<OverlayController> _logger;
    private readonly TimeProvider _clock;

    private IBarSurface? _surface;
    private Run? _run;
    private volatile bool _captureExcluded;
    private bool _disposed;

    /// <param name="settings">Where the bar settings are read, once per alert.</param>
    /// <param name="led">Where the LED is.</param>
    /// <param name="surfaceFactory">Makes the surface; called again after the surface failed.</param>
    /// <param name="frames">The frame clock.</param>
    /// <param name="ui">The UI thread.</param>
    /// <param name="logger">Receives errors and the capture-exclusion state.</param>
    /// <param name="clock">Times the watchdog; the system clock by default.</param>
    public OverlayController(
        SettingsService settings,
        ILedSource led,
        Func<IBarSurface> surfaceFactory,
        IFrameSource frames,
        IUiDispatcher ui,
        ILogger<OverlayController> logger,
        TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(led);
        ArgumentNullException.ThrowIfNull(surfaceFactory);
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentNullException.ThrowIfNull(ui);
        ArgumentNullException.ThrowIfNull(logger);

        _settings = settings;
        _led = led;
        _surfaceFactory = surfaceFactory;
        _frames = frames;
        _ui = ui;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;

        _frames.Frame += OnFrame;
        _led.LedChanged += OnLedChanged;
    }

    /// <summary>
    /// Whether the bar window is excluded from screen capture. False until the window exists, and false (with an error in the
    /// log) if Windows refused the exclusion: then OBS could see the bar and the Barra light must turn red.
    /// </summary>
    public bool CaptureExcluded => _captureExcluded;

    /// <summary>Raised when <see cref="CaptureExcluded"/> changes. May be raised on the UI thread.</summary>
    public event Action? CaptureExcludedChanged;

    /// <summary>Whether an alert or a test is on the bar right now. Read it on the UI thread.</summary>
    public bool IsShowing => _run is not null;

    /// <summary>
    /// How far the alert on the bar has come, from 0 to 1 (the whole pass, slide in to slide out), for the progress line of the
    /// quick panel; <c>null</c> when no alert is on the bar (the 3 s test does not count). Read it on the UI thread.
    /// </summary>
    public double? PassProgress => _run is { IsPreview: false, Progress: var progress } ? progress : null;

    /// <summary>Creates the window now (hidden), so the capture-exclusion state is known before the first alert. Call on the UI thread.</summary>
    public void Start()
    {
        try
        {
            EnsureSurface();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Nao foi possivel criar a janela da barra");
        }
    }

    /// <summary>
    /// Shows <paramref name="text"/> on the bar, once. Returns true when it crossed the whole screen, false when it was cut
    /// short (cancelled, the LED vanished or moved, no LED, the window failed). Empty text shows nothing and counts as passed.
    /// </summary>
    /// <exception cref="InvalidOperationException">Another alert is already on the bar: alerts go one at a time.</exception>
    public Task<bool> ShowAsync(string text, CancellationToken ct) =>
        _ui.InvokeAsync(() => PlayAsync(text, ct, preview: false));

    /// <summary>
    /// The "show the bar for 3 seconds" test: the example text on the real bar, a whole pass in 3 s, never entering the queue.
    /// It does nothing while an alert is on the bar, and an alert that arrives cuts it short.
    /// </summary>
    public async Task PreviewAsync()
    {
        await _ui.InvokeAsync(async () =>
        {
            if (_run is { IsPreview: false })
            {
                _logger.LogInformation("Teste da barra ignorado: um aviso esta passando");
                return false;
            }

            return await PlayAsync(BarPreview.SampleText, CancellationToken.None, preview: true);
        });
    }

    /// <summary>Cuts short whatever is on the bar and releases the window.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _frames.Frame -= OnFrame;
        _led.LedChanged -= OnLedChanged;
        if (_run is { } run)
        {
            Complete(run, false);
        }

        DiscardSurface();
    }

    private async Task<bool> PlayAsync(string text, CancellationToken ct, bool preview)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (_disposed || ct.IsCancellationRequested)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        if (_run is { } current)
        {
            if (!current.IsPreview)
            {
                throw new InvalidOperationException("Another alert is already on the bar.");
            }

            Complete(current, false); // an alert is worth more than the test
        }

        var led = _led.Led;
        if (led is null)
        {
            _logger.LogInformation("Sem LED: a barra nao pode mostrar o aviso");
            return false;
        }

        var bar = _settings.Current.Bar;
        var slide = TimeSpan.FromMilliseconds(bar.SlideMs);
        PreparedBar prepared;
        BarTimeline timeline;
        try
        {
            var surface = EnsureSurface();
            prepared = surface.Prepare(text, bar, led);
            var speed = preview
                ? BarPreview.SpeedFor(led.Width, prepared.TextWidthPx, slide, BarPreview.Duration)
                : bar.Speed;
            timeline = new BarTimeline(led.Width, prepared.TextWidthPx, speed, slide);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "Falha ao preparar a barra; a janela sera recriada");
            DiscardSurface();
            return false;
        }

        var run = new Run(timeline, led, preview);
        _run = run;
        run.Watchdog = _clock.CreateTimer(
            _ => _ui.Post(() => Complete(run, false, "sem quadros ate o fim do prazo")),
            null,
            timeline.Total + WatchdogMargin,
            Timeout.InfiniteTimeSpan);
        run.Cancellation = ct.CanBeCanceled
            ? ct.Register(() => _ui.Post(() => Complete(run, false, "cancelado")))
            : default;

        _frames.Start();
        return await run.Done.Task;
    }

    private void OnFrame(TimeSpan time)
    {
        if (_run is not { } run || run.Finished)
        {
            return;
        }

        run.Origin ??= time;
        var elapsed = time - run.Origin.Value;
        run.Progress = run.Timeline.Total > TimeSpan.Zero ? Math.Clamp(elapsed / run.Timeline.Total, 0, 1) : 0;
        var frame = run.Timeline.At(elapsed);
        try
        {
            _surface?.Apply(frame);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "Falha ao desenhar a barra; a janela sera recriada");
            Complete(run, false);
            DiscardSurface();
            return;
        }

        if (frame.Phase == BarPhase.Done)
        {
            Complete(run, true);
        }
    }

    private void OnLedChanged(ScreenInfo? led) => _ui.Post(() =>
    {
        if (_run is { } run && run.Screen != led)
        {
            _logger.LogWarning("O LED mudou ou sumiu durante o aviso");
            Complete(run, false, "LED mudou");
        }
    });

    // Ends the pass: stops the frames, hides the bar and only then lets the caller of ShowAsync go on.
    private void Complete(Run run, bool passed, string? why = null)
    {
        if (run.Finished)
        {
            return;
        }

        run.Finished = true;
        run.Watchdog?.Dispose();
        run.Cancellation.Dispose();
        if (why is not null)
        {
            _logger.LogInformation("Passagem da barra encerrada antes do fim: {Why}", why);
        }

        if (ReferenceEquals(_run, run))
        {
            _run = null;
            _frames.Stop();
            try
            {
                _surface?.Hide();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.LogError(ex, "Falha ao esconder a barra");
            }
        }

        run.Done.TrySetResult(passed);
    }

    private IBarSurface EnsureSurface()
    {
        if (_surface is { } existing)
        {
            return existing;
        }

        var surface = _surfaceFactory();
        _surface = surface;
        surface.CaptureExcludedChanged += OnCaptureExcludedChanged;
        OnCaptureExcludedChanged();
        return surface;
    }

    private void DiscardSurface()
    {
        if (_surface is not { } surface)
        {
            return;
        }

        _surface = null;
        surface.CaptureExcludedChanged -= OnCaptureExcludedChanged;
        try
        {
            surface.Dispose();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "Falha ao liberar a janela da barra");
        }
    }

    private void OnCaptureExcludedChanged()
    {
        var excluded = _surface?.CaptureExcluded ?? false;
        if (_surface is not null && !excluded)
        {
            _logger.LogError("A barra NAO esta excluida da captura de tela (SetWindowDisplayAffinity falhou): o OBS poderia mostra-la");
        }
        else if (excluded)
        {
            _logger.LogInformation("Barra excluida da captura de tela");
        }

        if (_captureExcluded != excluded)
        {
            _captureExcluded = excluded;
            CaptureExcludedChanged?.Invoke();
        }
    }

    private sealed class Run(BarTimeline timeline, ScreenInfo screen, bool isPreview)
    {
        public BarTimeline Timeline { get; } = timeline;

        public ScreenInfo Screen { get; } = screen;

        public bool IsPreview { get; } = isPreview;

        public TaskCompletionSource<bool> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TimeSpan? Origin { get; set; }

        public double Progress { get; set; }

        public bool Finished { get; set; }

        public ITimer? Watchdog { get; set; }

        public CancellationTokenRegistration Cancellation { get; set; }
    }
}
