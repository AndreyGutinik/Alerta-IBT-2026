using System.Diagnostics;
using AlertaIBT.App.Overlay;
using AlertaIBT.Core.Ndi;
using AlertaIBT.Core.Screens;
using AlertaIBT.Core.Settings;
using Microsoft.Extensions.Logging;

namespace AlertaIBT.App.Ndi;

/// <summary>
/// Glues the NDI engine to the screen: when the switcher says the camera window should show, it creates (once, lazily: nothing GPU-related happens while
/// the layer is off, which is always the case at start) the video window on the right monitor and the Direct3D renderer, feeds it the on-air frames through a
/// <see cref="FrameMailbox"/>, moves the picture when the vertical position changes, follows the monitor and the "Esconder da live" setting, keeps the window at the
/// bottom of the z-order, and draws the test pattern when asked. Everything that touches the window runs on the UI thread.
/// </summary>
public sealed class NdiTelaoHost : IDisposable
{
    private static readonly TimeSpan BottomInterval = TimeSpan.FromSeconds(2);

    private readonly NdiService _service;
    private readonly SettingsService _settings;
    private readonly ILedSource _led;
    private readonly IUiDispatcher _ui;
    private readonly Func<IReadOnlyList<ScreenInfo>> _screens;
    private readonly ILogger? _logger;
    private readonly FrameMailbox _mailbox = new();
    private readonly object _gate = new();
    private NdiVideoWindow? _window;
    private D3dVideoRenderer? _renderer;
    private NdiTestPattern? _pattern;
    private byte[]? _patternBuffer;
    private Timer? _patternTimer;
    private Timer? _bottomTimer;
    private Stopwatch? _patternClock;
    private ScreenInfo? _placedOn;
    private bool _patternActive;
    private bool _showing;
    private bool _applyQueued;
    private bool _disposed;
    private IReadOnlyList<ScreenInfo>? _screenCache;

    /// <param name="service">The NDI engine.</param>
    /// <param name="settings">The settings (monitor, hide from capture).</param>
    /// <param name="led">Where the LED is (the monitor of the alert bar).</param>
    /// <param name="ui">The UI thread.</param>
    /// <param name="screens">Lists the monitors (<c>MonitorService.Enumerate</c> in the app).</param>
    /// <param name="logger">Log.</param>
    public NdiTelaoHost(
        NdiService service,
        SettingsService settings,
        ILedSource led,
        IUiDispatcher ui,
        Func<IReadOnlyList<ScreenInfo>> screens,
        ILogger? logger = null)
    {
        _service = service;
        _settings = settings;
        _led = led;
        _ui = ui;
        _screens = screens;
        _logger = logger;
    }

    /// <summary>The window, or <c>null</c> until the camera was shown the first time.</summary>
    public NdiVideoWindow? Window => _window;

    /// <summary>The renderer, or <c>null</c> until the camera was shown the first time.</summary>
    public D3dVideoRenderer? Renderer => _renderer;

    /// <summary>Subscribes to the engine. Call once, on the UI thread, at startup.</summary>
    public void Start()
    {
        _service.WindowVisibleChanged += _ => QueueApply();
        _service.Changed += QueueApply;
        _service.ProgramFrame += OnProgramFrame;
        _service.ProgramPositionChanged += position => _renderer?.SetPosition(position);
        _led.LedChanged += _ =>
        {
            _screenCache = null;
            QueueApply();
        };
        _settings.Changed += _ => QueueApply();
    }

    /// <summary>Tells the host that Windows reported a display change (monitors moved, resolution changed).</summary>
    public void NotifyDisplayChanged()
    {
        _screenCache = null;
        QueueApply();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
        }

        _patternTimer?.Dispose();
        _bottomTimer?.Dispose();
        _renderer?.Dispose();
        _window?.Dispose();
    }

    private void OnProgramFrame(in NdiFrameView frame, double position)
    {
        if (_patternActive || _renderer is null)
        {
            return;
        }

        _mailbox.Post(frame, position);
        _renderer.Wake();
    }

    // Many events arrive in a burst (every 250 ms tick, every state change): one Apply per burst.
    private void QueueApply()
    {
        lock (_gate)
        {
            if (_disposed || _applyQueued)
            {
                return;
            }

            _applyQueued = true;
        }

        _ui.Post(() =>
        {
            lock (_gate)
            {
                _applyQueued = false;
                if (_disposed)
                {
                    return;
                }
            }

            try
            {
                Apply();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger?.LogError(ex, "Erro ao aplicar o estado da camera no telao");
            }
        });
    }

    private void Apply()
    {
        var wantShow = _service.Switcher.WindowVisible;
        var pattern = _service.Switcher.TestPattern;
        if (!wantShow)
        {
            StopPattern();
            if (_showing)
            {
                _window?.Hide();
                _bottomTimer?.Dispose();
                _bottomTimer = null;
                _showing = false;
                _logger?.LogInformation("Camera do telao escondida");
            }

            return;
        }

        var ndi = _settings.Current.Ndi;
        _screenCache ??= _screens();
        var screen = _service.ChooseMonitor(_screenCache, _led.Led);
        if (screen is null)
        {
            if (_showing)
            {
                _window?.Hide();
                _showing = false;
            }

            _logger?.LogWarning("Camera do telao: nenhum monitor para usar (o LED 2560x768 nao esta ligado)");
            return;
        }

        EnsureSurface(screen);
        var window = _window!;
        if (_placedOn != screen)
        {
            window.Place(screen);
            _renderer!.SetTargetSize(screen.Width, screen.Height);
            _placedOn = screen;
            _pattern = null;
        }

        if (window.CaptureExcluded != ndi.HideFromCapture)
        {
            window.SetHiddenFromCapture(ndi.HideFromCapture);
        }

        if (pattern)
        {
            StartPattern(screen);
        }
        else
        {
            StopPattern();
        }

        if (!_showing)
        {
            _showing = true;
            _renderer!.RequestRepaint();
            _ = ShowAfterFirstPaintAsync(window, _renderer.FramesPresented);
        }
    }

    // Shows the window only once the renderer has painted something, so no black flash comes before the first picture.
    private async Task ShowAfterFirstPaintAsync(NdiVideoWindow window, long before)
    {
        var renderer = _renderer!;
        var deadline = Stopwatch.StartNew();
        while (renderer.FramesPresented == before && deadline.ElapsedMilliseconds < 400 && renderer.Error is null)
        {
            await Task.Delay(5).ConfigureAwait(true);
        }

        if (_showing && !_disposed)
        {
            window.Show();
            _logger?.LogInformation("Camera do telao mostrada");
            _bottomTimer ??= new Timer(_ => _ui.Post(() => _window?.KeepAtBottom()), null, BottomInterval, BottomInterval);
        }
    }

    private void EnsureSurface(ScreenInfo screen)
    {
        if (_window is not null)
        {
            return;
        }

        _window = new NdiVideoWindow(_logger);
        _renderer = new D3dVideoRenderer(_window.Handle, screen.Width, screen.Height, _mailbox, _logger);
        _renderer.Start();
    }

    private void StartPattern(ScreenInfo screen)
    {
        if (_patternActive)
        {
            return;
        }

        _pattern = new NdiTestPattern(screen.Width, screen.Height);
        _patternBuffer = new byte[screen.Width * screen.Height * 4];
        _patternClock = Stopwatch.StartNew();
        _patternActive = true;
        _patternTimer = new Timer(_ => PostPatternFrame(), null, 0, 16);
    }

    private void StopPattern()
    {
        if (!_patternActive)
        {
            return;
        }

        _patternActive = false;
        _patternTimer?.Dispose();
        _patternTimer = null;
    }

    private void PostPatternFrame()
    {
        var pattern = _pattern;
        var buffer = _patternBuffer;
        var clock = _patternClock;
        var renderer = _renderer;
        if (!_patternActive || pattern is null || buffer is null || clock is null || renderer is null)
        {
            return;
        }

        pattern.Render(buffer, clock.Elapsed.TotalSeconds);
        _mailbox.Post(new NdiFrameView(pattern.Width, pattern.Height, pattern.Width * 4, buffer, 0), 50);
        renderer.Wake();
    }
}
