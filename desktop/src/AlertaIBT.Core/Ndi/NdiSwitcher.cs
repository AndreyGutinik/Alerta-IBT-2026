using AlertaIBT.Core.Settings;
using Microsoft.Extensions.Logging;

namespace AlertaIBT.Core.Ndi;

/// <summary>Result of a switcher command.</summary>
/// <param name="Success">Whether it did (or already was) what was asked.</param>
/// <param name="Message">Why not (pt-BR), or a note; <c>null</c> when plain success.</param>
public sealed record NdiResult(bool Success, string? Message = null)
{
    /// <summary>Plain success.</summary>
    public static NdiResult Ok { get; } = new(true);

    /// <summary>A failure with its reason.</summary>
    public static NdiResult Fail(string message) => new(false, message);
}

/// <summary>The state of the telao camera as the UI and the API show it.</summary>
/// <param name="LayerOn">The telao layer switch (always false at start).</param>
/// <param name="WindowVisible">Whether the camera window is really showing (layer on and frames fresh, or the test pattern).</param>
/// <param name="TestPattern">Whether the color bars are showing.</param>
/// <param name="PreviewSlot">Slot (0-based) in Preview, or <c>null</c>.</param>
/// <param name="ProgramSlot">Slot (0-based) on air, or <c>null</c>.</param>
/// <param name="PreviewName">Name of the Preview camera, or <c>null</c>.</param>
/// <param name="ProgramName">Name of the on-air camera, or <c>null</c>.</param>
/// <param name="TakePending">A Take is waiting for the Preview's first frame.</param>
/// <param name="PreviewState">State of the Preview receiver.</param>
/// <param name="ProgramState">State of the Program receiver.</param>
/// <param name="ProgramFps">Measured frames per second of the on-air source.</param>
/// <param name="ProgramWidth">Width of the on-air picture.</param>
/// <param name="ProgramHeight">Height of the on-air picture.</param>
/// <param name="ProgramLastFrameAgeMs">Milliseconds since the on-air source's last frame, or <c>null</c> when none ever came.</param>
/// <param name="ProgramFramesReceived">Frames received by the on-air receiver.</param>
/// <param name="ProgramFramesDropped">Frames dropped by the on-air receiver.</param>
/// <param name="PreviewFps">Measured frames per second of the Preview source.</param>
/// <param name="LastError">The last error text, or <c>null</c>.</param>
public sealed record NdiSwitcherStatus(
    bool LayerOn,
    bool WindowVisible,
    bool TestPattern,
    int? PreviewSlot,
    int? ProgramSlot,
    string? PreviewName,
    string? ProgramName,
    bool TakePending,
    NdiConnectionState PreviewState,
    NdiConnectionState ProgramState,
    double ProgramFps,
    int ProgramWidth,
    int ProgramHeight,
    double? ProgramLastFrameAgeMs,
    long ProgramFramesReceived,
    long ProgramFramesDropped,
    double PreviewFps,
    string? LastError);

/// <summary>A frame of the on-air camera plus its camera's vertical position (0..100).</summary>
public delegate void NdiProgramFrameHandler(in NdiFrameView frame, double verticalPosition);

/// <summary>
/// The Preview/Program switcher (like OBS studio mode). Two receivers stay connected all the time, so a Take (cut) only swaps which one feeds the
/// telao: instant, never black. Selecting a camera reconnects only the Preview receiver; Take swaps the two (the old program stays connected as
/// the new preview); a Take before the preview's first frame waits for it (and gives up with an error after <see cref="NdiConstants.TakeTimeout"/>);
/// the layer is always OFF at start; an on-air source that goes silent freezes its last picture for <see cref="NdiConstants.HoldTime"/> and then
/// the window hides (Holyrics shows normally), coming back by itself when frames return and the layer is still ON. Slots are 0-based here
/// (the UI shows them 1-based). Every public method is thread safe and returns at once.
/// </summary>
public sealed class NdiSwitcher : IDisposable
{
    /// <summary>The thumbnails refresh at 20 fps at most.</summary>
    public static readonly TimeSpan ThumbnailInterval = TimeSpan.FromMilliseconds(50);

    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(250);

    private readonly Func<NdiSettings> _settings;
    private readonly TimeProvider _clock;
    private readonly ILogger? _logger;
    private readonly Func<NdiSourceRef, NdiSourceRef> _enrich;
    private readonly TimeSpan _hold;
    private readonly TimeSpan _takeTimeout;
    private readonly object _gate = new();
    private readonly Lane _laneA;
    private readonly Lane _laneB;
    private readonly NdiThumbnail?[] _slotThumbs = new NdiThumbnail?[NdiSettings.SlotCount];
    private readonly ITimer _timer;

    private volatile Lane _program;
    private volatile Lane _preview;
    private volatile bool _pending;
    private long _pendingSince;
    private bool _layerOn;
    private bool _testPattern;
    private bool _windowVisible;
    private string? _lastError;
    private bool _disposed;

    /// <param name="receiverFactory">Creates a receiver given its name (called twice, once per lane).</param>
    /// <param name="settings">The current NDI settings (read live: names, sources and positions may change).</param>
    /// <param name="clock">The clock for freshness and timeouts.</param>
    /// <param name="logger">Receives the commands and errors.</param>
    /// <param name="enrich">Adds a known address to a name source (from the finder); identity when null.</param>
    /// <param name="holdTime">How long the last picture stays when the on-air source stops (5 s by default).</param>
    /// <param name="takeTimeout">How long a Take waits for the first frame of the Preview.</param>
    public NdiSwitcher(
        Func<string, ICameraReceiver> receiverFactory,
        Func<NdiSettings> settings,
        TimeProvider? clock = null,
        ILogger? logger = null,
        Func<NdiSourceRef, NdiSourceRef>? enrich = null,
        TimeSpan? holdTime = null,
        TimeSpan? takeTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(receiverFactory);
        ArgumentNullException.ThrowIfNull(settings);

        _settings = settings;
        _clock = clock ?? TimeProvider.System;
        _logger = logger;
        _enrich = enrich ?? (source => source);
        _hold = holdTime ?? NdiConstants.HoldTime;
        _takeTimeout = takeTimeout ?? NdiConstants.TakeTimeout;
        _laneA = new Lane(0, receiverFactory("IBT Controller A"));
        _laneB = new Lane(1, receiverFactory("IBT Controller B"));
        _program = _laneA;
        _preview = _laneB;
        _laneA.Receiver.Frame += OnFrameA;
        _laneB.Receiver.Frame += OnFrameB;
        _laneA.Receiver.StateChanged += OnReceiverStateChanged;
        _laneB.Receiver.StateChanged += OnReceiverStateChanged;
        _timer = _clock.CreateTimer(_ => Tick(), null, TickInterval, TickInterval);
    }

    /// <summary>Raised (any thread) when anything the UI shows changed. Coarse on purpose: read <see cref="GetStatus"/> again.</summary>
    public event Action? Changed;

    /// <summary>Raised (any thread) with a message for the user when something failed (a Take that timed out, a source that cannot connect).</summary>
    public event Action<string>? Error;

    /// <summary>Raised (any thread) when the camera window should show or hide.</summary>
    public event Action<bool>? WindowVisibleChanged;

    /// <summary>Raised on the capture thread for every on-air frame.</summary>
    public event NdiProgramFrameHandler? ProgramFrame;

    /// <summary>Raised when the on-air camera's vertical position was changed in the settings without a new frame (repaint with the stored picture).</summary>
    public event Action<double>? ProgramPositionChanged;

    /// <summary>Raised (any thread, at most 20 per second per lane) with the slot whose thumbnail changed (<c>-1</c> when its lane has no slot).</summary>
    public event Action<int>? ThumbnailUpdated;

    /// <summary>The telao layer switch. Always false when the app starts.</summary>
    public bool LayerOn
    {
        get
        {
            lock (_gate)
            {
                return _layerOn;
            }
        }
    }

    /// <summary>Whether the camera window should be showing now.</summary>
    public bool WindowVisible
    {
        get
        {
            lock (_gate)
            {
                return _windowVisible;
            }
        }
    }

    /// <summary>Whether the test pattern (color bars) shows.</summary>
    public bool TestPattern
    {
        get
        {
            lock (_gate)
            {
                return _testPattern;
            }
        }
    }

    /// <summary>The receivers (program first), for diagnostics and tests.</summary>
    public IReadOnlyList<ICameraReceiver> Receivers => [_program.Receiver, _preview.Receiver];

    /// <summary>Puts the telao layer ON or OFF.</summary>
    public NdiResult SetLayer(bool on)
    {
        lock (_gate)
        {
            _layerOn = on;
        }

        _logger?.LogInformation("NDI telao: camada {State}", on ? "ligada" : "desligada");
        Refresh();
        return NdiResult.Ok;
    }

    /// <summary>Shows or hides the color bars (also visible with the layer OFF, so the owner can test; they never touch the cameras).</summary>
    public NdiResult SetTestPattern(bool on)
    {
        lock (_gate)
        {
            _testPattern = on;
        }

        Refresh();
        return NdiResult.Ok;
    }

    /// <summary>
    /// Puts camera <paramref name="slot"/> in Preview: only the Preview receiver reconnects (it keeps its last picture meanwhile). Selecting the
    /// camera that is on air, or the one already in Preview, changes nothing and succeeds.
    /// </summary>
    public NdiResult SelectPreview(int slot)
    {
        if (slot is < 0 or >= NdiSettings.SlotCount)
        {
            return NdiResult.Fail("C\U000000E2mera inexistente");
        }

        var settings = _settings();
        var camera = settings.Cameras[slot];
        var source = NdiSourceParser.Parse(camera.Source);
        if (source is null)
        {
            return RaiseFail($"{camera.Name} n\U000000E3o tem uma fonte NDI escolhida");
        }

        Lane preview;
        lock (_gate)
        {
            if (_disposed)
            {
                return NdiResult.Fail("desligado");
            }

            var program = _program;
            if (program.Slot == slot || (program.Slot is int onAir && NdiSourceParser.Same(settings.Cameras[onAir].Source, camera.Source)))
            {
                return NdiResult.Ok;
            }

            preview = _preview;
            if (preview.Slot == slot)
            {
                return NdiResult.Ok;
            }

            SaveThumbnailLocked(preview);
            preview.Slot = slot;
            _pending = false;
            _lastError = null;
            var cached = _slotThumbs[slot];
            if (cached is not null && !preview.Thumbnail.HasPicture)
            {
                preview.Thumbnail.Restore(cached);
            }
        }

        _logger?.LogInformation("NDI telao: preview = {Camera} ({Source})", camera.Name, source);
        preview.Receiver.Connect(_enrich(source));
        Refresh();
        return NdiResult.Ok;
    }

    /// <summary>
    /// The cut: swaps Preview and Program. If the Preview has not received its first frame yet, the cut waits for it (never black) and the result
    /// says it is pending; after the timeout an error is raised and nothing is cut.
    /// </summary>
    public NdiResult Take()
    {
        bool swapped = false;
        bool noPreview = false;
        lock (_gate)
        {
            if (_disposed)
            {
                return NdiResult.Fail("desligado");
            }

            var preview = _preview;
            if (preview.Slot is null)
            {
                noPreview = true;
            }
            else if (preview.Receiver.HasFrameSinceConnect)
            {
                SwapLocked();
                swapped = true;
            }
            else
            {
                _pending = true;
                _pendingSince = _clock.GetTimestamp();
                swapped = false;
            }
        }

        if (noPreview)
        {
            return RaiseFail("Escolha uma c\U000000E2mera no Preview antes de cortar");
        }

        if (!swapped)
        {
            _logger?.LogInformation("NDI telao: cortar adiado, esperando o primeiro quadro do preview");
        }

        Refresh();
        return swapped ? NdiResult.Ok : new NdiResult(true, "esperando o primeiro quadro do preview");
    }

    /// <summary>Tells the switcher the settings changed (a source edited, a position moved): reconnects a lane whose source changed and repaints.</summary>
    public void ApplySettings()
    {
        var settings = _settings();
        Lane? program = null;
        Lane? preview = null;
        NdiSourceRef? programSource = null;
        NdiSourceRef? previewSource = null;
        double? position = null;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            var p = _program;
            var v = _preview;
            if (p.Slot is int ps)
            {
                var wanted = NdiSourceParser.Parse(settings.Cameras[ps].Source);
                if (wanted != Unenriched(p.Receiver.Source))
                {
                    program = p;
                    programSource = wanted;
                }

                position = settings.Cameras[ps].VerticalPosition;
            }

            if (v.Slot is int vs)
            {
                var wanted = NdiSourceParser.Parse(settings.Cameras[vs].Source);
                if (wanted != Unenriched(v.Receiver.Source))
                {
                    preview = v;
                    previewSource = wanted;
                }
            }
        }

        if (program is not null)
        {
            program.Receiver.Connect(programSource is null ? null : _enrich(programSource));
        }

        if (preview is not null)
        {
            preview.Receiver.Connect(previewSource is null ? null : _enrich(previewSource));
        }

        if (position is { } value)
        {
            ProgramPositionChanged?.Invoke(value);
        }

        Refresh();
    }

    /// <summary>The state now.</summary>
    public NdiSwitcherStatus GetStatus()
    {
        var settings = _settings();
        Lane program;
        Lane preview;
        bool layer;
        bool visible;
        bool pattern;
        bool pending;
        string? error;
        lock (_gate)
        {
            program = _program;
            preview = _preview;
            layer = _layerOn;
            visible = _windowVisible;
            pattern = _testPattern;
            pending = _pending;
            error = _lastError;
        }

        var rx = program.Receiver;
        var last = rx.LastFrameTimestamp;
        return new NdiSwitcherStatus(
            layer,
            visible,
            pattern,
            preview.Slot,
            program.Slot,
            NameOf(settings, preview.Slot),
            NameOf(settings, program.Slot),
            pending,
            preview.Receiver.State,
            rx.State,
            Math.Round(rx.Fps, 1),
            rx.Width,
            rx.Height,
            last == 0 ? null : Math.Round(_clock.GetElapsedTime(last).TotalMilliseconds),
            rx.FramesReceived,
            rx.FramesDropped,
            Math.Round(preview.Receiver.Fps, 1),
            error ?? rx.LastError ?? preview.Receiver.LastError);
    }

    /// <summary>The thumbnail of a camera slot: the live one while the camera is in Preview or on air, else the last one it had. <c>null</c> when none yet.</summary>
    public NdiThumbnail? GetThumbnail(int slot)
    {
        if (slot is < 0 or >= NdiSettings.SlotCount)
        {
            return null;
        }

        lock (_gate)
        {
            foreach (var lane in new[] { _program, _preview })
            {
                if (lane.Slot == slot)
                {
                    return lane.Thumbnail.Snapshot() ?? _slotThumbs[slot];
                }
            }

            return _slotThumbs[slot];
        }
    }

    /// <summary>The thumbnail of the Preview lane (holds its last picture while it reconnects), or <c>null</c>.</summary>
    public NdiThumbnail? GetPreviewThumbnail() => _preview.Thumbnail.Snapshot();

    /// <summary>The thumbnail of the Program lane, or <c>null</c>.</summary>
    public NdiThumbnail? GetProgramThumbnail() => _program.Thumbnail.Snapshot();

    /// <summary>Copies the Preview thumbnail into a buffer you own (no allocation); false when none or too small.</summary>
    public bool TryCopyPreviewThumbnail(Span<byte> destination, out int width, out int height, out long version) =>
        _preview.Thumbnail.TryCopy(destination, out width, out height, out version);

    /// <summary>Copies the Program thumbnail into a buffer you own (no allocation); false when none or too small.</summary>
    public bool TryCopyProgramThumbnail(Span<byte> destination, out int width, out int height, out long version) =>
        _program.Thumbnail.TryCopy(destination, out width, out height, out version);

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
        }

        _timer.Dispose();
        _laneA.Receiver.Frame -= OnFrameA;
        _laneB.Receiver.Frame -= OnFrameB;
        _laneA.Receiver.Dispose();
        _laneB.Receiver.Dispose();
    }

    private static string? NameOf(NdiSettings settings, int? slot) => slot is int s && s < settings.Cameras.Count ? settings.Cameras[s].Name : null;

    // The reference as typed, without what the finder added: compares with what the settings say.
    private NdiSourceRef? Unenriched(NdiSourceRef? source) =>
        source is null ? null : source.Url is not null && source.Name is not null ? NdiSourceParser.Parse(source.Name) : source;

    private void OnFrameA(in NdiFrameView frame) => OnFrame(_laneA, frame);

    private void OnFrameB(in NdiFrameView frame) => OnFrame(_laneB, frame);

    private void OnFrame(Lane lane, in NdiFrameView frame)
    {
        if (lane.Thumbnail.Update(frame, ThumbnailInterval, _clock))
        {
            ThumbnailUpdated?.Invoke(lane.Slot ?? -1);
        }

        if (_pending && ReferenceEquals(lane, _preview))
        {
            var done = false;
            lock (_gate)
            {
                if (_pending && ReferenceEquals(lane, _preview))
                {
                    SwapLocked();
                    done = true;
                }
            }

            if (done)
            {
                _logger?.LogInformation("NDI telao: corte feito ao chegar o primeiro quadro");
                Refresh();
            }
        }

        if (ReferenceEquals(lane, _program))
        {
            if (!_windowVisible && _layerOn)
            {
                Refresh(); // frames are back: the camera comes back by itself
            }

            var slot = lane.Slot;
            var settings = _settings();
            var position = slot is int s && s < settings.Cameras.Count ? settings.Cameras[s].VerticalPosition : NdiSettings.DefaultVerticalPosition;
            ProgramFrame?.Invoke(frame, position);
        }
    }

    private void OnReceiverStateChanged() => Refresh();

    // Swaps the two lanes. Must hold _gate.
    private void SwapLocked()
    {
        var oldProgram = _program;
        var oldPreview = _preview;
        SaveThumbnailLocked(oldProgram);
        _program = oldPreview;
        _preview = oldProgram;
        _pending = false;
        _lastError = null;
    }

    private void SaveThumbnailLocked(Lane lane)
    {
        if (lane.Slot is int slot && lane.Thumbnail.Snapshot() is { } picture)
        {
            _slotThumbs[slot] = picture;
        }
    }

    private void Tick()
    {
        string? timeout = null;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            if (_pending && _clock.GetElapsedTime(_pendingSince) >= _takeTimeout)
            {
                _pending = false;
                timeout = "A c\U000000E2mera do Preview n\U000000E3o enviou imagem: o corte foi cancelado";
                _lastError = timeout;
            }
        }

        if (timeout is not null)
        {
            _logger?.LogWarning("NDI telao: {Message}", timeout);
            Error?.Invoke(timeout);
        }

        Refresh();
    }

    // Recomputes whether the window shows and raises the events if it changed (or always Changed).
    private void Refresh()
    {
        bool visible;
        bool changed;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            var rx = _program.Receiver;
            var last = rx.LastFrameTimestamp;
            var fresh = last != 0 && rx.HasFrameSinceConnect && _clock.GetElapsedTime(last) <= _hold;
            visible = (_layerOn && fresh) || _testPattern;
            changed = visible != _windowVisible;
            _windowVisible = visible;
        }

        if (changed)
        {
            WindowVisibleChanged?.Invoke(visible);
        }

        Changed?.Invoke();
    }

    private NdiResult RaiseFail(string message)
    {
        lock (_gate)
        {
            _lastError = message;
        }

        Error?.Invoke(message);
        Changed?.Invoke();
        return NdiResult.Fail(message);
    }

    private sealed class Lane(int id, ICameraReceiver receiver)
    {
        public int Id { get; } = id;

        public ICameraReceiver Receiver { get; } = receiver;

        public ThumbnailBuffer Thumbnail { get; } = new();

        public int? Slot { get; set; }
    }
}
