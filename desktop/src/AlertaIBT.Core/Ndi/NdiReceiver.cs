using Microsoft.Extensions.Logging;

namespace AlertaIBT.Core.Ndi;

/// <summary>Timings of an <see cref="NdiReceiver"/>; the defaults are for the app, tests shorten them.</summary>
/// <param name="CaptureTimeoutMs">How long one capture call waits.</param>
/// <param name="LostAfter">Silence after which the state turns to Lost (the receiver keeps waiting: the SDK reconnects by itself).</param>
/// <param name="RecreateAfter">Silence after which the native receiver is thrown away and created again.</param>
/// <param name="Backoff">The waits between failed creations or recreations; the last one repeats.</param>
public sealed record NdiReceiverOptions(int CaptureTimeoutMs, TimeSpan LostAfter, TimeSpan RecreateAfter, IReadOnlyList<TimeSpan> Backoff)
{
    /// <summary>The app's values: lost after 1.5 s of silence, recreated after 8 s, backoff 0.5 s, 1, 2, 4, 8.</summary>
    public static NdiReceiverOptions Default { get; } = new(
        100,
        TimeSpan.FromSeconds(1.5),
        TimeSpan.FromSeconds(8),
        [TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8)]);
}

/// <summary>
/// One NDI connection that stays up: a capture thread pulls video frames (video only, highest bandwidth, BGRA), frees each one right after the
/// handlers saw it, and reconnects with backoff when the sender goes away or the runtime fails. Connecting by name and by direct address are
/// both supported (<see cref="NdiSourceRef"/>). Counters and the last-frame timestamp are readable from any thread.
/// </summary>
public sealed class NdiReceiver : ICameraReceiver
{
    private const int FourCcBgra = 0x41524742; // 'B','G','R','A' little-endian
    private const int FourCcBgrx = 0x58524742; // 'B','G','R','X'

    private readonly INdiRuntime _runtime;
    private readonly string _name;
    private readonly TimeProvider _clock;
    private readonly ILogger? _logger;
    private readonly NdiReceiverOptions _options;
    private readonly object _gate = new();
    private readonly ManualResetEventSlim _wake = new(false);
    private Thread? _thread;
    private bool _disposed;
    private NdiSourceRef? _wanted;
    private long _generation;

    private int _state = (int)NdiConnectionState.Idle;
    private long _nativeDropped;
    private volatile bool _hasFrame;
    private long _lastFrame;
    private long _received;
    private long _dropped;
    private int _width;
    private int _height;
    private double _fps;
    private string? _lastError;
    private NdiSourceRef? _source;

    /// <param name="runtime">The NDI runtime.</param>
    /// <param name="name">The receiver name other NDI tools show for this connection.</param>
    /// <param name="clock">Stamps the frames.</param>
    /// <param name="logger">Receives state changes and errors.</param>
    /// <param name="options">Timings; <see cref="NdiReceiverOptions.Default"/> when null.</param>
    public NdiReceiver(INdiRuntime runtime, string name, TimeProvider? clock = null, ILogger? logger = null, NdiReceiverOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(runtime);

        _runtime = runtime;
        _name = name;
        _clock = clock ?? TimeProvider.System;
        _logger = logger;
        _options = options ?? NdiReceiverOptions.Default;
    }

    /// <inheritdoc />
    public event NdiFrameHandler? Frame;

    /// <inheritdoc />
    public event Action? StateChanged;

    /// <inheritdoc />
    public NdiConnectionState State => (NdiConnectionState)Volatile.Read(ref _state);

    /// <inheritdoc />
    public NdiSourceRef? Source
    {
        get
        {
            lock (_gate)
            {
                return _source;
            }
        }
    }

    /// <inheritdoc />
    public bool HasFrameSinceConnect => _hasFrame;

    /// <inheritdoc />
    public long LastFrameTimestamp => Interlocked.Read(ref _lastFrame);

    /// <inheritdoc />
    public long FramesReceived => Interlocked.Read(ref _received);

    /// <inheritdoc />
    public long FramesDropped => Interlocked.Read(ref _dropped) + Interlocked.Read(ref _nativeDropped);

    /// <inheritdoc />
    public int Width => Volatile.Read(ref _width);

    /// <inheritdoc />
    public int Height => Volatile.Read(ref _height);

    /// <inheritdoc />
    public double Fps => Volatile.Read(ref _fps);

    /// <inheritdoc />
    public string? LastError => Volatile.Read(ref _lastError);

    /// <inheritdoc />
    public void Connect(NdiSourceRef? source)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            _wanted = source;
            _source = source;
            _generation++;
            _hasFrame = false;
            if (source is not null && _thread is null)
            {
                _thread = new Thread(Run) { IsBackground = true, Name = $"NDI receiver {_name}" };
                _thread.Start();
            }
        }

        if (source is null)
        {
            SetState(NdiConnectionState.Idle);
        }
        else
        {
            SetState(NdiConnectionState.Connecting);
        }

        _wake.Set();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Thread? thread;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            thread = _thread;
        }

        _wake.Set();
        thread?.Join(TimeSpan.FromSeconds(3));
        _wake.Dispose();
    }

    private void SetState(NdiConnectionState state)
    {
        if (Interlocked.Exchange(ref _state, (int)state) == (int)state)
        {
            return;
        }

        _logger?.LogInformation("NDI {Receiver}: {State} ({Source})", _name, state, Source);
        try
        {
            StateChanged?.Invoke();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger?.LogError(ex, "Erro ao avisar a mudanca de estado do NDI");
        }
    }

    private void SetError(string? text) => Volatile.Write(ref _lastError, text);

    private void Run()
    {
        INdiNativeReceiver? native = null;
        long builtFor = -1;
        long silenceStart = 0;
        var attempt = 0;
        long fpsStart = _clock.GetTimestamp();
        long fpsFrames = 0;

        try
        {
            while (true)
            {
                NdiSourceRef? wanted;
                long generation;
                lock (_gate)
                {
                    if (_disposed)
                    {
                        return;
                    }

                    wanted = _wanted;
                    generation = _generation;
                }

                if (wanted is null)
                {
                    DisposeNative(ref native);
                    builtFor = -1;
                    _wake.Wait(250);
                    _wake.Reset();
                    continue;
                }

                if (native is null || builtFor != generation)
                {
                    DisposeNative(ref native);
                    try
                    {
                        native = _runtime.CreateReceiver(wanted, _name);
                        builtFor = generation;
                        silenceStart = _clock.GetTimestamp();
                        SetError(null);
                        SetState(NdiConnectionState.Connecting);
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        SetError(ex.Message);
                        _logger?.LogWarning("NDI {Receiver}: nao foi possivel conectar a {Source}: {Reason}", _name, wanted, ex.Message);
                        SetState(_runtime.IsAvailable ? NdiConnectionState.Lost : NdiConnectionState.Failed);
                        var delay = _options.Backoff[Math.Min(attempt, _options.Backoff.Count - 1)];
                        attempt++;
                        _wake.Wait(delay);
                        _wake.Reset();
                        continue;
                    }
                }

                var kind = native.Capture(_options.CaptureTimeoutMs, out var handle);
                var now = _clock.GetTimestamp();
                switch (kind)
                {
                    case NdiCaptureKind.Video:
                        try
                        {
                            attempt = 0;
                            silenceStart = now;
                            Interlocked.Exchange(ref _lastFrame, now);
                            Volatile.Write(ref _width, handle.Width);
                            Volatile.Write(ref _height, handle.Height);
                            Interlocked.Increment(ref _received);
                            fpsFrames++;
                            if (handle.FourCc is FourCcBgra or FourCcBgrx || handle.FourCc == 0)
                            {
                                var view = native.View(handle, now);
                                try
                                {
                                    Frame?.Invoke(view);
                                }
                                catch (Exception ex) when (ex is not OutOfMemoryException)
                                {
                                    Interlocked.Increment(ref _dropped);
                                    _logger?.LogError(ex, "Erro ao tratar um quadro do NDI");
                                }
                            }
                            else
                            {
                                Interlocked.Increment(ref _dropped);
                            }

                            _hasFrame = true;
                            SetState(NdiConnectionState.Connected);
                        }
                        finally
                        {
                            native.Release(handle);
                        }

                        break;
                    case NdiCaptureKind.Error:
                        SetError("O NDI informou um erro de conexao");
                        DisposeNative(ref native);
                        SetState(NdiConnectionState.Lost);
                        var wait = _options.Backoff[Math.Min(attempt, _options.Backoff.Count - 1)];
                        attempt++;
                        _wake.Wait(wait);
                        _wake.Reset();
                        break;
                    default:
                        var silence = _clock.GetElapsedTime(silenceStart, now);
                        if (silence >= _options.RecreateAfter)
                        {
                            // Long silence: throw the connection away and build it again after a pause.
                            _logger?.LogWarning("NDI {Receiver}: {Seconds:0} s sem quadros, recriando a conexao", _name, silence.TotalSeconds);
                            DisposeNative(ref native);
                            SetState(NdiConnectionState.Lost);
                            var pause = _options.Backoff[Math.Min(attempt, _options.Backoff.Count - 1)];
                            attempt++;
                            silenceStart = _clock.GetTimestamp();
                            _wake.Wait(pause);
                            _wake.Reset();
                        }
                        else if (silence >= _options.LostAfter && State == NdiConnectionState.Connected)
                        {
                            SetState(NdiConnectionState.Lost);
                        }

                        break;
                }

                var elapsed = _clock.GetElapsedTime(fpsStart, now);
                if (elapsed >= TimeSpan.FromSeconds(1))
                {
                    Volatile.Write(ref _fps, fpsFrames / elapsed.TotalSeconds);
                    fpsStart = now;
                    fpsFrames = 0;
                    if (native is not null)
                    {
                        Interlocked.Exchange(ref _nativeDropped, native.DroppedByRuntime);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            SetError(ex.Message);
            _logger?.LogError(ex, "A thread do receptor NDI {Receiver} terminou com erro", _name);
            SetState(NdiConnectionState.Failed);
        }
        finally
        {
            DisposeNative(ref native);
        }
    }

    private void DisposeNative(ref INdiNativeReceiver? native)
    {
        if (native is null)
        {
            return;
        }

        try
        {
            Interlocked.Add(ref _dropped, native.DroppedByRuntime);
            Interlocked.Exchange(ref _nativeDropped, 0);
            native.Dispose();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger?.LogWarning(ex, "Erro ao fechar o receptor NDI");
        }

        native = null;
    }
}
