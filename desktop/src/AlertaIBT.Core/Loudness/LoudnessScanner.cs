using AlertaIBT.Core.Player;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AlertaIBT.Core.Loudness;

/// <summary>Decodes a whole file and measures it (the real decoder in the app, a fake in tests).</summary>
public interface ILoudnessMeasurer
{
    /// <summary>Measures <paramref name="path"/>; <c>null</c> when it cannot be decoded. Cancellable; never touches the file.</summary>
    LoudnessResult? Measure(string path, CancellationToken cancellation);
}

/// <summary>What the player asks of the loudness service (a fake in tests).</summary>
public interface ILoudnessProvider
{
    /// <summary>The measurement of a file, or <c>null</c> when it has none yet. Never blocks on decoding.</summary>
    TrackLoudness? Get(string path);

    /// <summary>Asks for a file to be measured soon (the track just started without a measurement).</summary>
    void Request(string path);

    /// <summary>Raised (on the scanner thread) when a file got its measurement.</summary>
    event Action<string>? Measured;
}

/// <summary>Where the background scan is.</summary>
/// <param name="Measured">Files that have an answer (measured, silent or failed).</param>
/// <param name="Total">Files wanted.</param>
/// <param name="Active">Whether files are still waiting to be measured.</param>
/// <param name="TagsWritten">Files whose tags are written.</param>
/// <param name="TagsPending">Files whose tags are still to be written.</param>
public readonly record struct ScanProgress(int Measured, int Total, bool Active, int TagsWritten, int TagsPending)
{
    /// <summary>Nothing to scan.</summary>
    public static ScanProgress Empty => default;
}

/// <summary>What one step of the scanner did, and how long to wait before the next.</summary>
/// <param name="Kind">What happened.</param>
/// <param name="Wait">How long to wait before the next step; <c>null</c> waits until something changes (idle).</param>
/// <param name="Path">The file the step worked on.</param>
public readonly record struct ScanStep(ScanStepKind Kind, TimeSpan? Wait, string? Path = null);

/// <summary>The kinds of <see cref="ScanStep"/>.</summary>
public enum ScanStepKind
{
    /// <summary>Nothing to do.</summary>
    Idle,

    /// <summary>Paused (the user is busy with the playlists, or a file is waiting its retry time).</summary>
    Waiting,

    /// <summary>A file was measured.</summary>
    Measured,

    /// <summary>A file could not be decoded (it plays at 0 dB).</summary>
    Failed,

    /// <summary>A file's tags were written.</summary>
    TagsWritten,

    /// <summary>A tag write was skipped or failed.</summary>
    TagsSkipped,
}

/// <summary>Tuning of the scanner (the defaults are the owner's choices; tests shorten them).</summary>
public sealed record ScannerOptions
{
    /// <summary>The share of one core the scan may use: after each file it waits so that decoding is this fraction of the time (0.25 = a quarter).</summary>
    public double MaxCpuShare { get; init; } = 0.25;

    /// <summary>At most one tag write per this time.</summary>
    public TimeSpan TagInterval { get; init; } = TimeSpan.FromSeconds(4);

    /// <summary>After the user changes the playlists the scan waits this long before it goes on.</summary>
    public TimeSpan IdleAfterActivity { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>A file that was open when its tags were due is tried again after this.</summary>
    public TimeSpan BusyRetry { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How often the cache is written to disk while the scan runs.</summary>
    public TimeSpan SaveEvery { get; init; } = TimeSpan.FromSeconds(10);
}

/// <summary>
/// Measures the loudness of the files of the playlists in the background, one file at a time, at below-normal priority and throttled to a
/// share of one core, and writes the tags into the FLACs after each measurement. It never blocks playback: a track that is not measured yet plays at
/// 0 dB and is asked for through <see cref="Request"/> so it is ready next time. The cache lets it resume where it stopped after a restart.
/// </summary>
public sealed class LoudnessScanner : ILoudnessProvider, IDisposable
{
    private readonly object _gate = new();
    private readonly LoudnessCache _cache;
    private readonly ILoudnessMeasurer _measurer;
    private readonly ILoudnessTagWriter? _writer;
    private readonly Func<bool> _writeTags;
    private readonly Func<string, bool> _isInUse;
    private readonly TimeProvider _clock;
    private readonly ILogger _logger;
    private readonly ScannerOptions _options;
    private readonly CancellationTokenSource _stop = new();
    private readonly ManualResetEventSlim _wake = new(false);
    private readonly Dictionary<string, DateTimeOffset> _retryAfter = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _requests = [];
    private IReadOnlyList<string> _wanted = [];
    private DateTimeOffset _pausedUntil;
    private DateTimeOffset _lastTagWrite = DateTimeOffset.MinValue;
    private ScanProgress _progress;
    private Thread? _thread;
    private bool _disposed;

    /// <param name="cache">The measurements.</param>
    /// <param name="measurer">Decodes and measures a file.</param>
    /// <param name="writer">Writes the tags, or <c>null</c> for none.</param>
    /// <param name="writeTags">Whether the switch "Gravar etiquetas nos arquivos" is on (read at each write).</param>
    /// <param name="isInUse">Whether the player holds the file (playing, waiting next, fading out): its tags are not written now.</param>
    /// <param name="clock">Times the throttle and the pauses.</param>
    /// <param name="logger">Receives what happens (file names only).</param>
    /// <param name="options">The tuning.</param>
    public LoudnessScanner(
        LoudnessCache cache,
        ILoudnessMeasurer measurer,
        ILoudnessTagWriter? writer = null,
        Func<bool>? writeTags = null,
        Func<string, bool>? isInUse = null,
        TimeProvider? clock = null,
        ILogger? logger = null,
        ScannerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(measurer);

        _cache = cache;
        _measurer = measurer;
        _writer = writer;
        _writeTags = writeTags ?? (() => false);
        _isInUse = isInUse ?? (_ => false);
        _clock = clock ?? TimeProvider.System;
        _logger = logger ?? NullLogger.Instance;
        _options = options ?? new ScannerOptions();
    }

    /// <inheritdoc />
    public event Action<string>? Measured;

    /// <summary>Raised (on the scanner thread) when the progress changed.</summary>
    public event Action<ScanProgress>? ProgressChanged;

    /// <summary>Where the scan is now.</summary>
    public ScanProgress Progress
    {
        get
        {
            lock (_gate)
            {
                return _progress;
            }
        }
    }

    /// <summary>The cache (for the tests and the statistics).</summary>
    public LoudnessCache Cache => _cache;

    /// <summary>Sets the files that are wanted: the files of <paramref name="primary"/> (the playlist in use) come first, then those of <paramref name="secondary"/>.</summary>
    public void Configure(IReadOnlyList<string> primary, IReadOnlyList<string>? secondary = null)
    {
        var all = new List<string>(primary);
        var seen = new HashSet<string>(primary, StringComparer.OrdinalIgnoreCase);
        foreach (var path in secondary ?? [])
        {
            if (seen.Add(path))
            {
                all.Add(path);
            }
        }

        lock (_gate)
        {
            _wanted = all;
        }

        RaiseProgress();
        _wake.Set();
    }

    /// <summary>Tells the scanner that the user is changing the playlists: it waits a few seconds of quiet before it goes on.</summary>
    public void NotifyUserActivity()
    {
        lock (_gate)
        {
            _pausedUntil = _clock.GetUtcNow() + _options.IdleAfterActivity;
        }
    }

    /// <inheritdoc />
    public TrackLoudness? Get(string path) => _cache.Get(path)?.ToLoudness();

    /// <inheritdoc />
    public void Request(string path)
    {
        lock (_gate)
        {
            if (!_requests.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                _requests.Add(path);
            }
        }

        _wake.Set();
    }

    /// <summary>Wakes the thread to look for work again (a setting changed, for instance the switch of the tags).</summary>
    public void Wake() => _wake.Set();

    /// <summary>Starts the background thread (below-normal priority).</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_thread is not null || _disposed)
            {
                return;
            }

            _thread = new Thread(Loop) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "IBT loudness scan" };
            _thread.Start();
        }

        _wake.Set();
    }

    /// <summary>Does one step of the work: a requested measurement, else a tag write that is due, else the next unmeasured file, else waits.</summary>
    public ScanStep ProcessNext(CancellationToken cancellation)
    {
        var now = _clock.GetUtcNow();
        string? requested = null;
        IReadOnlyList<string> wanted;
        TimeSpan? pause = null;
        lock (_gate)
        {
            wanted = _wanted;
            foreach (var path in _requests.ToList())
            {
                if (_cache.Get(path) is not null)
                {
                    _requests.Remove(path);
                }
                else if (requested is null && File.Exists(path))
                {
                    requested = path;
                }
                else if (!File.Exists(path))
                {
                    _requests.Remove(path);
                }
            }

            if (requested is null && now < _pausedUntil)
            {
                pause = _pausedUntil - now;
            }
        }

        if (pause is { } paused)
        {
            return new ScanStep(ScanStepKind.Waiting, paused);
        }

        if (requested is not null)
        {
            return MeasureOne(requested, cancellation);
        }

        var tagDue = TagWriteDue(now);
        var next = wanted.FirstOrDefault(p => _cache.Get(p) is null && File.Exists(p));
        if (tagDue && TryWriteOne(wanted, now, cancellation) is { } wrote)
        {
            return wrote;
        }

        if (next is not null)
        {
            return MeasureOne(next, cancellation);
        }

        // Nothing to measure: tags may still be waiting for their turn or for a busy file.
        var wait = TagWaitTime(wanted, now);
        return wait is null ? new ScanStep(ScanStepKind.Idle, null) : new ScanStep(ScanStepKind.Waiting, wait);
    }

    /// <summary>Stops the thread and writes the cache.</summary>
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

        _stop.Cancel();
        _wake.Set();
        thread?.Join(TimeSpan.FromSeconds(5));
        _cache.SaveIfDirty(TimeSpan.Zero);
        _stop.Dispose();
        _wake.Dispose();
    }

    private void Loop()
    {
        var token = _stop.Token;
        while (!token.IsCancellationRequested)
        {
            ScanStep step;
            try
            {
                _wake.Reset();
                step = ProcessNext(token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "A medicao de volume falhou; tento de novo em instantes");
                step = new ScanStep(ScanStepKind.Waiting, TimeSpan.FromSeconds(30));
            }

            try
            {
                if (step.Wait is { } wait)
                {
                    _wake.Wait(wait, token);
                }
                else
                {
                    _cache.SaveIfDirty(TimeSpan.Zero);
                    _wake.Wait(token);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private ScanStep MeasureOne(string path, CancellationToken cancellation)
    {
        var stamp = LoudnessCache.Stamp(path, new LoudnessEntry { Path = path });
        if (stamp is null)
        {
            return new ScanStep(ScanStepKind.Waiting, TimeSpan.Zero, path);
        }

        var started = _clock.GetTimestamp();
        LoudnessEntry entry;
        var kind = ScanStepKind.Measured;

        // Tags this app wrote earlier (checked against the audio MD5) are an answer already: no decoding.
        if (_writer is not null && _writer.Supports(path) && FlacLoudnessTagWriter.ReadMarked(path) is { } marked)
        {
            entry = stamp with { Status = MeasureStatus.Measured, Lufs = marked.IntegratedLufs, SamplePeak = marked.TruePeak, TruePeak = marked.TruePeak, Tags = TagState.Written, Note = "etiquetas do proprio app" };
        }
        else
        {
            LoudnessResult? result;
            try
            {
                result = _measurer.Measure(path, cancellation);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Nao foi possivel medir o volume de {File}", Path.GetFileName(path));
                result = null;
            }

            if (result is null)
            {
                entry = stamp with { Status = MeasureStatus.Failed, Tags = TagState.NotApplicable };
                kind = ScanStepKind.Failed;
            }
            else if (result.Value.IntegratedLufs is not { } lufs || !double.IsFinite(lufs))
            {
                entry = stamp with { Status = MeasureStatus.Silent, SamplePeak = result.Value.SamplePeak, TruePeak = result.Value.TruePeak, Tags = TagState.NotApplicable };
            }
            else
            {
                var supported = _writer is not null && _writer.Supports(path);
                entry = stamp with
                {
                    Status = MeasureStatus.Measured,
                    Lufs = lufs,
                    SamplePeak = result.Value.SamplePeak,
                    TruePeak = result.Value.TruePeak,
                    Tags = supported ? TagState.Pending : TagState.NotApplicable,
                };
            }
        }

        _cache.Set(entry);
        _cache.SaveIfDirty(_options.SaveEvery);
        lock (_gate)
        {
            _requests.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        }

        RaiseProgress();
        Measured?.Invoke(path);
        var elapsed = _clock.GetElapsedTime(started);
        var share = Math.Clamp(_options.MaxCpuShare, 0.01, 1.0);
        return new ScanStep(kind, TimeSpan.FromTicks((long)(elapsed.Ticks * ((1.0 / share) - 1.0))), path);
    }

    private bool TagWriteDue(DateTimeOffset now) => _writer is not null && _writeTags() && now - _lastTagWrite >= _options.TagInterval;

    // How long until a tag write can be tried (its turn, or the retry time of the busy files); null when none is waiting or the switch is off.
    private TimeSpan? TagWaitTime(IReadOnlyList<string> wanted, DateTimeOffset now)
    {
        if (_writer is null || !_writeTags())
        {
            return null;
        }

        var turn = _lastTagWrite + _options.TagInterval;
        DateTimeOffset? earliest = null;
        lock (_gate)
        {
            foreach (var path in PendingTags(wanted))
            {
                var ready = _retryAfter.TryGetValue(path, out var after) && after > turn ? after : turn;
                if (earliest is null || ready < earliest)
                {
                    earliest = ready;
                }
            }
        }

        if (earliest is not { } at)
        {
            return null;
        }

        var wait = at - now;
        return wait < TimeSpan.FromMilliseconds(50) ? TimeSpan.FromMilliseconds(50) : wait;
    }

    private IEnumerable<string> PendingTags(IReadOnlyList<string> wanted)
    {
        foreach (var path in wanted)
        {
            if (_cache.Get(path) is { Tags: TagState.Pending, Status: MeasureStatus.Measured })
            {
                yield return path;
            }
        }
    }

    private ScanStep? TryWriteOne(IReadOnlyList<string> wanted, DateTimeOffset now, CancellationToken cancellation)
    {
        foreach (var path in PendingTags(wanted))
        {
            lock (_gate)
            {
                if (_retryAfter.TryGetValue(path, out var after) && after > now)
                {
                    continue;
                }
            }

            if (_isInUse(path))
            {
                lock (_gate)
                {
                    _retryAfter[path] = now + _options.BusyRetry;
                }

                continue;
            }

            var entry = _cache.Get(path)!;
            var result = _writer!.Write(path, new LoudnessTagValues(entry.Lufs!.Value, Math.Max(entry.TruePeak, entry.SamplePeak)), cancellation);
            _lastTagWrite = _clock.GetUtcNow();
            switch (result.Outcome)
            {
                case TagWriteOutcome.Written:
                    // The file's size changed by a few bytes: the key follows so the file is not measured again.
                    _cache.Set(entry with { Tags = TagState.Written, Size = result.NewSize, WriteTicks = result.NewWriteTicks, Note = null });
                    _cache.SaveIfDirty(_options.SaveEvery);
                    RaiseProgress();
                    return new ScanStep(ScanStepKind.TagsWritten, _options.TagInterval, path);
                case TagWriteOutcome.Busy:
                    lock (_gate)
                    {
                        _retryAfter[path] = now + _options.BusyRetry;
                    }

                    return new ScanStep(ScanStepKind.TagsSkipped, TimeSpan.Zero, path);
                case TagWriteOutcome.Cancelled:
                    return new ScanStep(ScanStepKind.Idle, null, path);
                default:
                    _logger.LogWarning("Etiquetas de volume nao gravadas em {File}: {Reason}", Path.GetFileName(path), result.Reason);
                    _cache.Set(entry with { Tags = TagState.DoNotRetry, Note = result.Reason });
                    _cache.SaveIfDirty(_options.SaveEvery);
                    RaiseProgress();
                    return new ScanStep(ScanStepKind.TagsSkipped, _options.TagInterval, path);
            }
        }

        return null;
    }

    private void RaiseProgress()
    {
        IReadOnlyList<string> wanted;
        lock (_gate)
        {
            wanted = _wanted;
        }

        var measured = 0;
        var written = 0;
        var pending = 0;
        foreach (var path in wanted)
        {
            if (_cache.Get(path) is { } entry)
            {
                measured++;
                if (entry.Tags == TagState.Written)
                {
                    written++;
                }
                else if (_writer is not null && entry.Tags == TagState.Pending && entry.Status == MeasureStatus.Measured)
                {
                    pending++;
                }
            }
        }

        var progress = new ScanProgress(measured, wanted.Count, measured < wanted.Count, written, pending);
        lock (_gate)
        {
            if (progress == _progress)
            {
                return;
            }

            _progress = progress;
        }

        ProgressChanged?.Invoke(progress);
    }
}

/// <summary>Measures a file by decoding it through the player's backend at the file's own sample rate and channels (no resampling, no mixing).</summary>
public sealed class BackendLoudnessMeasurer(IPlayerBackend backend) : ILoudnessMeasurer
{
    /// <inheritdoc />
    public LoudnessResult? Measure(string path, CancellationToken cancellation)
    {
        using var native = backend.OpenNative(path);
        if (native is null)
        {
            return null;
        }

        var meter = new LoudnessMeter(native.SampleRate, native.Channels);
        var buffer = new float[native.SampleRate * native.Channels]; // one second
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            var read = native.Track.Read(buffer, 0, buffer.Length);
            if (read <= 0)
            {
                break;
            }

            meter.Add(buffer, 0, read);
        }

        return meter.Finish();
    }
}
