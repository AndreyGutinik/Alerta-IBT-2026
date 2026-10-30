using AlertaIBT.Core.Audio;
using AlertaIBT.Core.Loudness;
using AlertaIBT.Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using AlertaIBT.Core.Diagnostics;

namespace AlertaIBT.Core.Player;

/// <summary>An output stream that is already playing (the player's mixer is what it pulls). Disposing stops it.</summary>
public interface IPlayerOutput : IDisposable
{
    /// <summary>The endpoint id of the device in use.</summary>
    string? DeviceId { get; }

    /// <summary>The friendly name of the device in use.</summary>
    string? DeviceName { get; }

    /// <summary>Raised on any thread when the output died (device unplugged, driver reset): the player closes it and waits.</summary>
    event Action<Exception?>? Failed;
}

/// <summary>The Windows side of the player (decoding and the shared-mode output); a fake in tests, NAudio in the app.</summary>
public interface IPlayerBackend
{
    /// <summary>
    /// Opens a shared-mode output on <paramref name="deviceId"/> (the Windows default when <c>null</c>) and starts it. The backend learns the
    /// device's mix format, asks <paramref name="createSource"/> for the source of that format (frames per second, channels) and pulls it.
    /// Throws when the device cannot be opened.
    /// </summary>
    IPlayerOutput OpenOutput(string? deviceId, Func<int, int, IPcmSource> createSource);

    /// <summary>Opens a file as float samples in the given format: decoded as it is (24-bit stays 24-bit) and, when the rate differs, resampled with the best quality. Throws when it cannot be read.</summary>
    IPcmTrack OpenTrack(string path, int sampleRate, int channels);

    /// <summary>
    /// Finds where the sound of a file really starts and ends (leading silence, quiet tail) by decoding only its head and its tail; the answer is
    /// cached per file. <c>null</c> (the default) means "unknown": the header lengths are used. May take tens of milliseconds: never on the audio thread.
    /// </summary>
    TrackProfile? Analyze(string path, int sampleRate, int channels) => null;

    /// <summary>
    /// Opens a file for measuring its loudness: decoded to float at the file's OWN sample rate and channel count (no resampling, no channel mapping).
    /// <c>null</c> (the default) means the backend cannot do it. Dispose the result.
    /// </summary>
    NativePcm? OpenNative(string path) => null;
}

/// <summary>A file decoded as it is: the samples with the rate and the channels of the file.</summary>
/// <param name="Track">The decoded samples.</param>
/// <param name="SampleRate">Frames per second of the file.</param>
/// <param name="Channels">Samples per frame of the file.</param>
public sealed record NativePcm(IPcmTrack Track, int SampleRate, int Channels) : IDisposable
{
    /// <summary>Releases the decoder.</summary>
    public void Dispose() => Track.Dispose();
}

/// <summary>
/// The music player's engine: it owns the queue, the output (opened when something plays, closed when it stops), the fades, and the state
/// the screen shows. Everything is serialized by one lock and nothing is done on the audio thread. A command never throws: a failure
/// is a <see cref="PlayerResult"/> and the <see cref="PlayerStatus.LastError"/>.
/// </summary>
/// <remarks>
/// Never call the commands from the UI thread when they can open a file or a device (they may take a few hundred milliseconds): the
/// view-model and the media keys use the thread pool.
/// </remarks>
public sealed class MusicPlayer : IPlayerControl, IAlarmTarget, IDisposable
{
    /// <summary>How long an explicit skip (next, previous, restart) fades out and in, so a cut never clicks.</summary>
    public static readonly TimeSpan SkipFade = TimeSpan.FromMilliseconds(150);

    /// <summary>How long a "play" that arrives during a fade-out takes to bring the sound back.</summary>
    public static readonly TimeSpan ResumeFade = TimeSpan.FromSeconds(1);

    /// <summary>"Previous" restarts the track when it is further than this into it.</summary>
    public static readonly TimeSpan RestartAfter = TimeSpan.FromSeconds(3);

    /// <summary>How often a player that has no output (device missing) tries again.</summary>
    public static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(5);

    /// <summary>How long a change of output waits for a quicker second change before it moves the sound (rapid changes coalesce to the last one).</summary>
    public static readonly TimeSpan OutputMoveDelay = TimeSpan.FromMilliseconds(40);

    /// <summary>The micro-fade of the move: the old output fades out and the new one fades in over this time (150 ms), so nothing clicks.</summary>
    public static readonly TimeSpan OutputMoveFade = TimeSpan.FromMilliseconds(150);

    private const int MaxSkips = 8;

    private readonly SettingsService _settings;
    private readonly PlaylistLibrary _library;
    private readonly IPlayerBackend _backend;
    private readonly IAudioSystem _audio;
    private readonly ITrackMetadataReader _metadata;
    private readonly TimeProvider _clock;
    private readonly ILogger _logger;
    private readonly Action<Action> _post;
    private readonly PlaybackQueue _queue;
    private readonly object _gate = new();

    private Session? _session;
    private PlayerState _state = PlayerState.Stopped;
    private TrackInfo? _current;
    private string? _waitingPath;
    private string _playlist = string.Empty;
    private string? _lastError;
    private OutputChoice? _choice;
    private ITimer? _retry;
    private ITimer? _moveTimer;
    private int _generation;
    private bool _fading;
    private bool _disposed;
    private bool _changed;
    private int _depth;
    private PlayerSettings? _applied;
    private TimeSpan _pausedAt;
    private TimeSpan _pausedDuration;
    private bool _pausedSeeked;
    private IPcmTrack? _preloaded;
    private string? _preloadedPath;
    private bool _preloadedSkipped;
    private string? _preloading;
    private string? _preloadFailed;
    private int _trackToken;
    private int _preloadToken;
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<IPcmTrack, string> _preloadedPaths = new();
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<IPcmTrack, TrackNorm> _norms = new();
    private readonly ILoudnessProvider? _loudness;
    private NormalizationInfo? _currentNorm;

    /// <param name="settings">The settings (the player section).</param>
    /// <param name="library">The playlists.</param>
    /// <param name="backend">Decoding and output.</param>
    /// <param name="audio">The Windows outputs (names and ids).</param>
    /// <param name="metadata">Titles and artists.</param>
    /// <param name="clock">Times the retries.</param>
    /// <param name="logger">Receives what happens (names and counts only).</param>
    /// <param name="post">Runs work that must not happen on the audio or a Windows callback thread; the thread pool by default, inline in tests.</param>
    /// <param name="random">The draw of the shuffle; a seeded one in tests.</param>
    /// <param name="loudness">The measured loudness of the files (for "Igualar volume"); <c>null</c> plays every track at 0 dB.</param>
    public MusicPlayer(
        SettingsService settings,
        PlaylistLibrary library,
        IPlayerBackend backend,
        IAudioSystem audio,
        ITrackMetadataReader metadata,
        TimeProvider? clock = null,
        ILogger? logger = null,
        Action<Action>? post = null,
        Random? random = null,
        ILoudnessProvider? loudness = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(audio);
        ArgumentNullException.ThrowIfNull(metadata);

        _settings = settings;
        _library = library;
        _backend = backend;
        _audio = audio;
        _metadata = metadata;
        _clock = clock ?? TimeProvider.System;
        _logger = logger ?? NullLogger.Instance;
        _post = post ?? (action => ThreadPool.QueueUserWorkItem(_ => action()));
        _queue = new PlaybackQueue(random);
        _loudness = loudness;
    }

    /// <summary>Raised (outside the lock, on any thread) after anything the screen shows changed. The listener posts to the UI thread.</summary>
    public event Action? StatusChanged;

    /// <summary>The player's own settings, read now.</summary>
    private PlayerSettings Options => _settings.Current.Player;

    /// <summary>
    /// Connects the player to the settings, the playlists and the Windows outputs, and looks at the output once. Call once, after the
    /// object graph is built (nothing here plays anything).
    /// </summary>
    public void Start()
    {
        _settings.Changed += OnSettingsChanged;
        _library.Changed += OnLibraryChanged;
        _audio.DevicesChanged += OnDevicesChanged;
        _audio.DefaultDeviceChanged += OnDevicesChanged;
        if (_loudness is not null)
        {
            _loudness.Measured += OnMeasured;
        }

        _library.Configure(Options.Playlists);
        Locked(() =>
        {
            ApplyOptions(Options);
            _outputKey = string.Join('|', Options.OutputDeviceId, string.Join('+', Options.OutputMatch));
            RefreshOutputLocked();
            return PlayerResult.Ok;
        });
    }

    /// <inheritdoc />
    public PlayerResult Play() => Locked(() =>
    {
        Breadcrumbs.Add("player: Play");
        return PlayLocked(Options.FadeInSeconds);
    });

    /// <summary>
    /// The alarm start: selects <paramref name="playlistName"/>, sets shuffle on and repeat all (saved, so the screen shows it), and starts the next
    /// track with the fade-in of the alarm. When something is already playing nothing changes.
    /// </summary>
    public AlarmStart StartFromAlarm(string playlistName)
    {
        var options = Options;
        if (!options.Enabled)
        {
            return new AlarmStart(AlarmStartOutcome.Failed, PlayerResult.DisabledText);
        }

        AlarmStart? early = null;
        Locked(() =>
        {
            if (_state == PlayerState.Playing && !_fading)
            {
                early = new AlarmStart(AlarmStartOutcome.AlreadyPlaying, null);
            }

            return PlayerResult.Ok;
        });
        if (early is not null)
        {
            return early;
        }

        // Settings first and outside the lock: the change handler takes the lock too.
        var wanted = options.FindPlaylist(playlistName)?.Name ?? options.CurrentPlaylist;
        if (!options.Shuffle || options.Repeat != "all" || !string.Equals(options.CurrentPlaylist, wanted, StringComparison.Ordinal))
        {
            _settings.Update(s => s with { Player = s.Player with { Shuffle = true, Repeat = "all", CurrentPlaylist = wanted } });
        }

        var result = Locked(() =>
        {
            ApplyOptions(Options);
            return PlayLocked(Options.FadeInSeconds);
        });
        return result.Success ? new AlarmStart(AlarmStartOutcome.Started, null) : new AlarmStart(AlarmStartOutcome.Failed, result.Error);
    }

    /// <inheritdoc />
    public PlayerResult Pause() => Locked(() =>
    {
        Breadcrumbs.Add("player: Pause");
        return FadeOutLocked(PlayerState.Paused);
    });

    /// <inheritdoc />
    public PlayerResult Stop() => Locked(() =>
    {
        Breadcrumbs.Add("player: Stop");
        return FadeOutLocked(PlayerState.Stopped);
    });

    /// <inheritdoc />
    public PlayerResult Next() => Locked(() =>
    {
        Breadcrumbs.Add("player: Next");
        if (!Options.Enabled)
        {
            return PlayerResult.Disabled;
        }

        if (_state != PlayerState.Playing)
        {
            return PlayLocked(0);
        }

        var path = _queue.NextForPlay();
        return path is null ? PlayerResult.Fail("a lista est\U000000E1 vazia") : SkipLocked(path);
    });

    /// <inheritdoc />
    public PlayerResult Previous() => Locked(() =>
    {
        Breadcrumbs.Add("player: Previous");
        if (!Options.Enabled)
        {
            return PlayerResult.Disabled;
        }

        if (_state != PlayerState.Playing)
        {
            return PlayLocked(0);
        }

        var position = _session?.Mixer.Position ?? TimeSpan.Zero;
        var path = position > RestartAfter ? null : _queue.Previous();
        path ??= _queue.Current ?? _current?.Path;
        return path is null ? PlayerResult.Fail("n\U000000E3o h\U000000E1 faixa anterior") : SkipLocked(path);
    });

    /// <summary>
    /// Jumps to <paramref name="position"/> of the current track (the fade and the volume stay as they are; a 12 ms duck hides the jump). On a
    /// paused player it only moves the resume point: "Play" then continues that track from there instead of starting another one.
    /// </summary>
    public PlayerResult Seek(TimeSpan position) => Locked(() =>
    {
        Breadcrumbs.Add("player: Seek");
        if (!Options.Enabled)
        {
            return PlayerResult.Disabled;
        }

        if (_state == PlayerState.Playing && _session is { } session)
        {
            return session.Mixer.Seek(position) ? PlayerResult.Ok : PlayerResult.Fail("nada tocando");
        }

        if (_state == PlayerState.Paused && _current is not null && _pausedDuration > TimeSpan.Zero)
        {
            var last = _pausedDuration - TimeSpan.FromMilliseconds(200);
            var to = position > last ? last : position;
            _pausedAt = to < TimeSpan.Zero ? TimeSpan.Zero : to;
            _pausedSeeked = true;
            _changed = true;
            return PlayerResult.Ok;
        }

        return PlayerResult.Fail("nada tocando");
    });

    /// <summary>Scans the current playlist again now ("Atualizar") and looks at the outputs again.</summary>
    public void Refresh()
    {
        _library.Refresh(Options.CurrentPlaylist);
        Locked(() =>
        {
            RefreshOutputLocked();
            return PlayerResult.Ok;
        });
    }

    /// <inheritdoc />
    public PlayerStatus GetStatus()
    {
        lock (_gate)
        {
            var options = Options;
            var mixer = _session?.Mixer;
            var paused = _state == PlayerState.Paused && _current is not null && mixer is null;
            return new PlayerStatus(
                options.Enabled,
                _state,
                _fading,
                _current,
                paused ? _pausedAt : mixer?.Position ?? TimeSpan.Zero,
                paused ? _pausedDuration : mixer?.Duration ?? TimeSpan.Zero,
                _playlist,
                _queue.Tracks.Count,
                options.Volume,
                options.StartMuted,
                options.Shuffle,
                PlayerRepeatText.Parse(options.Repeat),
                _choice is null ? OutputStatus.Unknown : DescribeOutput(options, _choice),
                _lastError,
                IndexOfCurrentLocked(),
                CurrentNormalizationLocked());
        }
    }

    // The 1-based place of the current track in the playlist (0 when nothing plays or it is not in the list).
    private int IndexOfCurrentLocked()
    {
        if (_current is null)
        {
            return 0;
        }

        var tracks = _queue.Tracks;
        for (var i = 0; i < tracks.Count; i++)
        {
            if (string.Equals(tracks[i], _current.Path, StringComparison.OrdinalIgnoreCase))
            {
                return i + 1;
            }
        }

        return 0;
    }

    /// <summary>Stops everything at once (no fade) and lets go of the settings and the outputs.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _settings.Changed -= OnSettingsChanged;
            _library.Changed -= OnLibraryChanged;
            _audio.DevicesChanged -= OnDevicesChanged;
            _audio.DefaultDeviceChanged -= OnDevicesChanged;
            if (_loudness is not null)
            {
                _loudness.Measured -= OnMeasured;
            }

            _retry?.Dispose();
            _retry = null;
            _moveTimer?.Dispose();
            _moveTimer = null;
            _generation++;
            CloseSessionLocked();
        }
    }

    // ---- commands (the lock is held) ----

    private PlayerResult PlayLocked(double fadeInSeconds)
    {
        if (!Options.Enabled)
        {
            return PlayerResult.Disabled;
        }

        if (_state == PlayerState.Playing)
        {
            if (!_fading)
            {
                return PlayerResult.Ok;
            }

            // Play during a fade-out: bring the same sound back instead of starting another track.
            _generation++;
            _fading = false;
            _session?.Mixer.FadeTo(1f, FadeEnvelope.FramesFor(ResumeFade.TotalSeconds, _session.Mixer.SampleRate));
            EnsurePreloadLocked();
            return PlayerResult.Ok;
        }

        if (_state == PlayerState.Paused && _pausedSeeked && _current is { } resumeTrack)
        {
            // The listener moved the slider while paused: continue that track from there.
            var at = _pausedAt;
            _retry?.Dispose();
            _retry = null;
            return OpenAndPlayLocked(resumeTrack.Path, fadeInSeconds, startAt: at);
        }

        SyncQueueLocked();
        if (_queue.Tracks.Count == 0)
        {
            _library.Refresh(Options.CurrentPlaylist);
            SyncQueueLocked();
        }

        var path = _queue.NextForPlay();
        if (path is null)
        {
            _lastError = "A lista est\U000000E1 vazia: nenhuma m\U000000FAsica na pasta.";
            _changed = true;
            return PlayerResult.Fail("a lista est\U000000E1 vazia");
        }

        _retry?.Dispose();
        _retry = null;
        return OpenAndPlayLocked(path, fadeInSeconds);
    }

    private PlayerResult FadeOutLocked(PlayerState target)
    {
        if (!Options.Enabled)
        {
            return PlayerResult.Disabled;
        }

        if (_state != PlayerState.Playing || _session is null)
        {
            // Nothing is sounding: only the label changes (a paused player can be stopped, a waiting one gives up).
            if (_state is PlayerState.Waiting or PlayerState.Paused && _state != target)
            {
                _generation++;
                _retry?.Dispose();
                _retry = null;
                _waitingPath = null;
                _state = target;
                if (target == PlayerState.Stopped)
                {
                    _current = null;
                    _pausedSeeked = false;
                }

                _changed = true;
            }

            return PlayerResult.Ok;
        }

        var generation = ++_generation;
        _fading = true;
        _changed = true;
        var session = _session;
        var frames = FadeEnvelope.FramesFor(Options.FadeOutSeconds, session.Mixer.SampleRate);
        session.Mixer.FadeTo(0f, frames, () => Locked(() => FinishFadeOutLocked(generation, session, target)));
        return PlayerResult.Ok;
    }

    private PlayerResult FinishFadeOutLocked(int generation, Session session, PlayerState target)
    {
        if (generation != _generation || !ReferenceEquals(session, _session))
        {
            return PlayerResult.Ok; // a newer command took over
        }

        _pausedAt = session.Mixer.Position;
        _pausedDuration = session.Mixer.Duration;
        _pausedSeeked = false;
        CloseSessionLocked();
        _fading = false;
        _state = target;
        if (target == PlayerState.Stopped)
        {
            _current = null;
        }

        _changed = true;
        return PlayerResult.Ok;
    }

    // An explicit skip: fade out for a moment, then the chosen track fades in.
    private PlayerResult SkipLocked(string path)
    {
        var generation = ++_generation;
        _fading = true;
        _changed = true;
        var session = _session!;
        session.Mixer.ClearNext(); // no crossfade can begin while the skip fades out
        ForgetPreloadLocked();
        session.Mixer.FadeTo(
            0f,
            FadeEnvelope.FramesFor(SkipFade.TotalSeconds, session.Mixer.SampleRate),
            () => Locked(() =>
            {
                if (generation != _generation || !ReferenceEquals(session, _session))
                {
                    return PlayerResult.Ok;
                }

                _fading = false;
                return OpenAndPlayLocked(path, 0, skipFade: true);
            }));
        return PlayerResult.Ok;
    }

    // The track ended by itself: what repeat says.
    private void OnTrackEnded(Session session)
    {
        Locked(() =>
        {
            if (_disposed || !ReferenceEquals(session, _session) || _state != PlayerState.Playing || _fading)
            {
                return PlayerResult.Ok;
            }

            var path = _queue.NextAuto();
            if (path is null)
            {
                _logger.LogInformation("Fim da lista: o player parou");
                CloseSessionLocked();
                _state = PlayerState.Stopped;
                _current = null;
                _changed = true;
                return PlayerResult.Ok;
            }

            return OpenAndPlayLocked(path, 0);
        });
    }

    // The next track took over inside the mixer (gapless, or the first sample of a crossfade): the queue, the title and the cover follow it now.
    private void OnTrackAdvanced(Session session, IPcmTrack track)
    {
        Locked(() =>
        {
            if (_disposed || !ReferenceEquals(session, _session) || _state != PlayerState.Playing || !ReferenceEquals(session.Mixer.Track, track) || !_preloadedPaths.TryGetValue(track, out var path))
            {
                return PlayerResult.Ok; // a newer command replaced the sound meanwhile
            }

            _queue.AdvanceTo(path);
            _current = _metadata.Read(path);
            RefreshCurrentNormLocked(track);
            _preloaded = null;
            _preloadedPath = null;
            _preloading = null;
            _trackToken++;
            _pausedSeeked = false;
            _lastError = null;
            _changed = true;
            _logger.LogInformation("Tocando: {File}", _current.FileName);
            EnsurePreloadLocked();
            return PlayerResult.Ok;
        });
    }

    // Makes sure the track the end of this one will bring is decoded ahead of time in the mixer (so the change has no gap), or that none is waiting
    // when the music should stop there. The decoding runs off the lock.
    private void EnsurePreloadLocked()
    {
        if (_disposed || _state != PlayerState.Playing || _fading || _session is not { } session || !Options.Enabled)
        {
            return;
        }

        if (_choice is { } choice && !string.Equals(session.RequestedId, choice.Device?.Id, StringComparison.OrdinalIgnoreCase))
        {
            // The wished output is another one now: the next track must open on it when this one ends, so nothing is carried over inside this session.
            if (_preloaded is not null)
            {
                session.Mixer.ClearNext();
                ForgetPreloadLocked();
            }

            return;
        }

        var wanted = _queue.PeekAuto();
        if (wanted is null)
        {
            if (_preloaded is not null)
            {
                session.Mixer.ClearNext();
                ForgetPreloadLocked();
            }

            return;
        }

        if (_preloaded is not null && string.Equals(wanted, _preloadedPath, StringComparison.OrdinalIgnoreCase) && _preloadedSkipped != (Options.CrossfadeSeconds > 0))
        {
            // The crossfade was switched on or off: the waiting track must (or must not) have its leading silence skipped.
            session.Mixer.ClearNext();
            _preloaded = null;
            _preloadedPath = null;
        }

        if (string.Equals(wanted, _preloadedPath, StringComparison.OrdinalIgnoreCase)
            || string.Equals(wanted, _preloading, StringComparison.OrdinalIgnoreCase)
            || string.Equals(wanted, _preloadFailed, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (_preloaded is not null)
        {
            session.Mixer.ClearNext(); // the queue changed its mind (shuffle or repeat were switched)
            _preloaded = null;
            _preloadedPath = null;
        }

        _preloading = wanted;
        var token = _trackToken;
        var preload = ++_preloadToken;
        _post(() => PreloadNext(session, token, preload, wanted));
    }

    private void ForgetPreloadLocked()
    {
        _preloaded = null;
        _preloadedPath = null;
        _preloading = null;
        _preloadFailed = null;
        _trackToken++;
        _preloadToken++;
    }

    // Decodes `path` outside the lock and, when nothing changed meanwhile, hands it to the mixer as the next track.
    private void PreloadNext(Session session, int token, int preload, string path)
    {
        IPcmTrack? pcm = null;
        TrackProfile? profile = null;
        var skipped = false;
        try
        {
            pcm = _backend.OpenTrack(path, session.Mixer.SampleRate, session.Mixer.Channels);
            if (Options.CrossfadeSeconds > 0)
            {
                // With a crossfade the new track enters at its first sound, not at its leading silence.
                profile = SafeAnalyze(path, session);
                if (profile is { } p && p.EffectiveStart >= TimeSpan.FromMilliseconds(100) && p.EffectiveStart < p.RealDuration)
                {
                    pcm.Seek(p.EffectiveStart);
                }

                skipped = true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Nao foi possivel abrir {File} adiantado; o player tenta de novo quando a faixa acabar", Path.GetFileName(path));
        }

        Locked(() =>
        {
            if (preload != _preloadToken)
            {
                return PlayerResult.Ok;
            }

            _preloading = null;
            if (pcm is null)
            {
                _preloadFailed = path;
                return PlayerResult.Ok;
            }

            if (!_disposed && token == _trackToken && ReferenceEquals(session, _session) && _state == PlayerState.Playing && !_fading
                && string.Equals(_queue.PeekAuto(), path, StringComparison.OrdinalIgnoreCase))
            {
                _preloadedPaths.AddOrUpdate(pcm, path);
                session.Mixer.SetNext(pcm, profile, Register(pcm, path).Gain.Linear);
                _preloaded = pcm;
                _preloadedPath = path;
                _preloadedSkipped = skipped;
                pcm = null;
            }

            return PlayerResult.Ok;
        });

        pcm?.Dispose();
    }

    // Where the sound of `path` really starts and ends; null when the backend cannot say (then the header lengths are used).
    private TrackProfile? SafeAnalyze(string path, Session session)
    {
        try
        {
            return _backend.Analyze(path, session.Mixer.SampleRate, session.Mixer.Channels);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Nao foi possivel medir o inicio e o fim de {File}", Path.GetFileName(path));
            return null;
        }
    }

    // Measures the track that just started (off the lock) so a crossfade at its end is anchored on its last sound, not on its quiet tail.
    private void AnalyzeCurrent(Session session, IPcmTrack pcm, string path)
    {
        if (Options.CrossfadeSeconds <= 0)
        {
            return;
        }

        _post(() =>
        {
            var profile = SafeAnalyze(path, session);
            if (profile is not null)
            {
                session.Mixer.SetProfile(pcm, profile);
            }
        });
    }

    /// <summary>
    /// Applies the player volume to the sound that is playing right now (0 to 100), without waiting for the settings to be saved: it takes a handful of
    /// microseconds and never the player's lock, so a slider is heard at once. The ramp of "Suavizar mudanca de volume" applies. Safe from any thread.
    /// </summary>
    /// <param name="volume">0 to 100.</param>
    /// <param name="muted">Whether the player is muted.</param>
    public void ApplyLiveVolume(int volume, bool muted) =>
        Volatile.Read(ref _session)?.Mixer.SetVolume(Math.Clamp(volume, 0, 100) / 100f, muted, rampSeconds: _settings.Current.Audio.VolumeRampSeconds);

    // ---- loudness (Igualar volume) ----

    // What a track brought to the mixer: the measurement it had when it started (null: it plays at 0 dB), kept so a change of the target recomputes from it.
    private sealed class TrackNorm(string path, TrackLoudness? applied, NormalizationGain gain)
    {
        public string Path { get; } = path;

        public TrackLoudness? Applied { get; set; } = applied;

        public NormalizationGain Gain { get; set; } = gain;
    }

    // Looks the track up in the loudness cache (never decoding) and remembers what it got. A track with no measurement plays at 0 dB and is asked for.
    private TrackNorm Register(IPcmTrack pcm, string path)
    {
        var options = Options;
        TrackLoudness? loudness = null;
        if (_loudness is not null)
        {
            loudness = _loudness.Get(path);
            if (loudness is null)
            {
                _loudness.Request(path);
            }
        }

        var norm = new TrackNorm(path, loudness, NormalizationGain.Compute(loudness, options.NormalizeTargetLufs, options.NormalizeEnabled));
        _norms.AddOrUpdate(pcm, norm);
        return norm;
    }

    // The info of the playing track for the screen.
    private void RefreshCurrentNormLocked(IPcmTrack track)
    {
        var options = Options;
        if (_norms.TryGetValue(track, out var norm))
        {
            var latest = _loudness?.Get(norm.Path)?.Lufs;
            var gain = NormalizationGain.Compute(norm.Applied, options.NormalizeTargetLufs, options.NormalizeEnabled);
            norm.Gain = gain;
            _currentNorm = new NormalizationInfo(options.NormalizeEnabled, options.NormalizeTargetLufs, gain.MeasuredLufs, gain.GainDb, gain.Capped, latest);
        }
        else
        {
            _currentNorm = null;
        }

        _changed = true;
    }

    private NormalizationInfo? CurrentNormalizationLocked() => _current is null ? null : _currentNorm;

    // A file got its measurement. A waiting next track that had none is still silent in the mixer: it gets its gain now (it has made no sound yet).
    // The track that is playing keeps 0 dB until its next turn, so the level never jumps in the middle of a song.
    private void OnMeasured(string path) => Locked(() =>
    {
        if (_disposed || _loudness is null)
        {
            return PlayerResult.Ok;
        }

        if (_preloaded is { } next && string.Equals(_preloadedPath, path, StringComparison.OrdinalIgnoreCase)
            && _norms.TryGetValue(next, out var norm) && norm.Applied is null && _session is { } session)
        {
            var options = Options;
            norm.Applied = _loudness.Get(path);
            norm.Gain = NormalizationGain.Compute(norm.Applied, options.NormalizeTargetLufs, options.NormalizeEnabled);
            session.Mixer.SetTrackGain(next, norm.Gain.Linear, immediate: true);
        }

        if (_current is { } current && string.Equals(current.Path, path, StringComparison.OrdinalIgnoreCase) && _session?.Mixer.Track is { } playing)
        {
            RefreshCurrentNormLocked(playing); // the tooltip can now say the track is measured
        }

        return PlayerResult.Ok;
    });

    /// <summary>Whether the player holds a file now (playing, decoded ahead, being decoded, or waiting for an output): its tags must not be written.</summary>
    public bool IsInUse(string path)
    {
        lock (_gate)
        {
            return string.Equals(_current?.Path, path, StringComparison.OrdinalIgnoreCase)
                || string.Equals(_preloadedPath, path, StringComparison.OrdinalIgnoreCase)
                || string.Equals(_preloading, path, StringComparison.OrdinalIgnoreCase)
                || string.Equals(_waitingPath, path, StringComparison.OrdinalIgnoreCase);
        }
    }

    private void OnOutputFailed(Session session, Exception? error)
    {
        Locked(() =>
        {
            if (_disposed || !ReferenceEquals(session, _session))
            {
                return PlayerResult.Ok;
            }

            _logger.LogWarning(error, "A saida de audio do player falhou; vou tentar de novo");
            _waitingPath = _current?.Path ?? _queue.Current;
            CloseSessionLocked();
            _fading = false;
            _lastError = "A sa\U000000EDda de \U000000E1udio falhou: " + (error?.Message ?? "desconectada") + ". Tentando de novo.";
            WaitLocked();
            return PlayerResult.Ok;
        });
    }

    // ---- opening ----

    // Opens `path` (skipping the files that cannot be read) and starts it, with the fade-in wanted.
    private PlayerResult OpenAndPlayLocked(string? path, double fadeInSeconds, bool skipFade = false, TimeSpan? startAt = null)
    {
        Session session;
        var choice = ResolveOutputLocked();
        try
        {
            session = EnsureSessionLocked(choice);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Nao foi possivel abrir a saida de audio do player");
            _waitingPath = path;
            _lastError = "Sem sa\U000000EDda de \U000000E1udio: " + ex.Message + ". Tentando de novo.";
            WaitLocked();
            return PlayerResult.Fail("sem sa\U000000EDda de \U000000E1udio");
        }

        string? lastProblem = null;
        for (var attempt = 0; attempt < MaxSkips && path is not null; attempt++)
        {
            try
            {
                var pcm = _backend.OpenTrack(path, session.Mixer.SampleRate, session.Mixer.Channels);
                var info = _metadata.Read(path);
                if (startAt is { } from && from > TimeSpan.Zero)
                {
                    pcm.Seek(from);
                }

                if (_state == PlayerState.Playing && !skipFade)
                {
                    // A track change inside a running output: the new track starts at full gain.
                    session.Mixer.SetTrack(pcm, Register(pcm, path).Gain.Linear);
                    session.Mixer.ResetGain();
                }
                else
                {
                    session.Mixer.StartTrack(pcm, FadeEnvelope.FramesFor(fadeInSeconds > 0 ? fadeInSeconds : skipFade ? SkipFade.TotalSeconds : 0, session.Mixer.SampleRate), Register(pcm, path).Gain.Linear);
                }

                RefreshCurrentNormLocked(pcm);

                _pausedSeeked = false;
                AnalyzeCurrent(session, pcm, path);

                _current = info;
                _state = PlayerState.Playing;
                _fading = false;
                _waitingPath = null;
                _lastError = null;
                _changed = true;
                _logger.LogInformation("Tocando: {File}", info.FileName);
                ForgetPreloadLocked();
                EnsurePreloadLocked();
                return PlayerResult.Ok;
            }
            catch (Exception ex)
            {
                lastProblem = $"N\U000000E3o foi poss\U000000EDvel tocar {Path.GetFileName(path)}: {ex.Message}";
                _logger.LogWarning(ex, "Nao foi possivel abrir {File}; pulando", Path.GetFileName(path));
                path = _queue.NextAuto() ?? _queue.NextForPlay();
            }
        }

        // Every attempt failed: stop cleanly with the reason.
        CloseSessionLocked();
        _state = PlayerState.Stopped;
        _current = null;
        _fading = false;
        _lastError = lastProblem ?? "Nenhuma m\U000000FAsica p\U000000F4de ser tocada.";
        _changed = true;
        return PlayerResult.Fail("n\U000000E3o foi poss\U000000EDvel tocar a m\U000000FAsica");
    }

    private Session EnsureSessionLocked(OutputChoice choice)
    {
        var wantedId = choice.Device?.Id;
        if (_session is { } current && string.Equals(current.RequestedId, wantedId, StringComparison.OrdinalIgnoreCase))
        {
            return current;
        }

        // The wished device came back (or went): the next track opens on the right one.
        CloseSessionLocked();
        var session = CreateSessionLocked(choice);
        _session = session;
        return session;
    }

    // Opens an output for `choice` and the mixer it pulls; does not touch the session in use.
    private Session CreateSessionLocked(OutputChoice choice)
    {
        var wantedId = choice.Device?.Id;
        PlayerMixer? mixer = null;
        var output = _backend.OpenOutput(wantedId, (rate, channels) =>
        {
            mixer = new PlayerMixer(rate, channels, _post);
            return mixer;
        });
        var session = new Session(output, mixer ?? throw new InvalidOperationException("The output did not ask for a source."), wantedId);
        var options = Options;
        mixer.SetVolume(options.Volume / 100f, options.StartMuted, immediate: true);
        mixer.SetCrossfade(options.CrossfadeSeconds);
        mixer.SetCrossfadeCurve(options.CrossfadeCurve);
        mixer.TrackEnded += () => OnTrackEnded(session);
        mixer.TrackAdvanced += track => OnTrackAdvanced(session, track);
        output.Failed += error => OnOutputFailed(session, error);
        return session;
    }

    private void CloseSessionLocked()
    {
        var session = _session;
        _session = null;
        if (session is null)
        {
            return;
        }

        try
        {
            session.Output.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Erro ao fechar a saida de audio do player");
        }

        session.Mixer.SetTrack(null);
        ForgetPreloadLocked();
    }

    // The player has no output now: try again every few seconds until one opens.
    private void WaitLocked()
    {
        _state = PlayerState.Waiting;
        _fading = false;
        _changed = true;
        _retry ??= _clock.CreateTimer(_ => Locked(RetryLocked), null, RetryInterval, RetryInterval);
    }

    private PlayerResult RetryLocked()
    {
        if (_disposed || _state != PlayerState.Waiting || !Options.Enabled)
        {
            _retry?.Dispose();
            _retry = null;
            return PlayerResult.Ok;
        }

        var path = _waitingPath ?? _queue.NextForPlay();
        if (path is null)
        {
            return PlayerResult.Ok;
        }

        _state = PlayerState.Stopped; // OpenAndPlay puts it to Playing or back to Waiting
        var result = OpenAndPlayLocked(path, 0);
        if (_state == PlayerState.Playing)
        {
            _retry?.Dispose();
            _retry = null;
        }

        return result;
    }

    // ---- the output choice ----

    private OutputChoice ResolveOutputLocked()
    {
        var options = Options;
        var active = _audio.ActiveRenderDevices();
        var choice = OutputResolver.Resolve(active, _audio.GetDefaultDevices().Multimedia, options.OutputDeviceId, options.OutputRule);
        _choice = choice;
        _logger.LogInformation("Saida do player: {Kind} ({Name})", choice.Kind, choice.Device?.Name ?? "padrao do Windows");
        if (choice.Kind is OutputChoiceKind.ById or OutputChoiceKind.ByRule && choice.Device is { } found
            && (options.OutputDeviceId != found.Id || options.OutputDeviceName != found.Name))
        {
            // Remember it by id and name, off the lock (the change handler takes the lock).
            _post(() => _settings.Update(s => s with { Player = s.Player with { OutputDeviceId = found.Id, OutputDeviceName = found.Name } }));
        }

        return choice;
    }

    private void RefreshOutputLocked()
    {
        var options = Options;
        var choice = OutputResolver.Resolve(_audio.ActiveRenderDevices(), _audio.GetDefaultDevices().Multimedia, options.OutputDeviceId, options.OutputRule);
        if (choice != _choice)
        {
            _choice = choice;
            _changed = true;
            EnsurePreloadLocked();
            ScheduleMoveLocked();
        }
    }

    // The wished output changed while a session may be sounding on another one: move the sound there in a moment (rapid changes coalesce).
    private void ScheduleMoveLocked()
    {
        if (_session is null || _state != PlayerState.Playing)
        {
            return;
        }

        _moveTimer ??= _clock.CreateTimer(_ => Locked(MoveToWishedOutputLocked), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _moveTimer.Change(OutputMoveDelay, Timeout.InfiniteTimeSpan);
    }

    // Moves the playing track, from the same position, to the output the settings ask for now: a new output opens, the track continues there with a
    // 150 ms fade-in and the old output fades out over the same 150 ms and closes. A device that is not there falls back to the Windows default (the dot says so).
    private PlayerResult MoveToWishedOutputLocked()
    {
        if (_disposed || _session is not { } old || _state != PlayerState.Playing || _fading || _current is not { } current)
        {
            return PlayerResult.Ok;
        }

        var choice = ResolveOutputLocked();
        if (string.Equals(old.RequestedId, choice.Device?.Id, StringComparison.OrdinalIgnoreCase))
        {
            _changed = true;
            return PlayerResult.Ok; // already there (the change was undone or never mattered)
        }

        var position = old.Mixer.Position;
        Session fresh;
        IPcmTrack pcm;
        try
        {
            fresh = CreateSessionLocked(choice);
            pcm = _backend.OpenTrack(current.Path, fresh.Mixer.SampleRate, fresh.Mixer.Channels);
            if (position > TimeSpan.Zero)
            {
                pcm.Seek(position);
            }
        }
        catch (Exception ex)
        {
            // The new output (or the file) would not open: the sound stays where it is and the screen says why.
            _logger.LogWarning(ex, "Nao foi possivel mover o player para a nova saida de audio");
            _lastError = "N\U000000E3o consegui abrir a nova sa\U000000EDda de \U000000E1udio: " + ex.Message;
            _changed = true;
            return PlayerResult.Fail("sem sa\U000000EDda de \U000000E1udio");
        }

        fresh.Mixer.StartTrack(pcm, FadeEnvelope.FramesFor(OutputMoveFade.TotalSeconds, fresh.Mixer.SampleRate), Register(pcm, current.Path).Gain.Linear);
        _session = fresh;
        RefreshCurrentNormLocked(pcm);
        AnalyzeCurrent(fresh, pcm, current.Path);
        _lastError = null;
        _changed = true;
        _logger.LogInformation("Player: som movido para {Device}", choice.Device?.Name ?? "padrao do Windows");
        ForgetPreloadLocked();
        EnsurePreloadLocked();
        FadeAndCloseOld(old);
        return PlayerResult.Ok;
    }

    // The old output fades out over the micro-fade and is closed (also after a second if the device is gone and nothing pulls it any more).
    private void FadeAndCloseOld(Session old)
    {
        var closed = 0;
        void Close()
        {
            if (Interlocked.Exchange(ref closed, 1) != 0)
            {
                return;
            }

            try
            {
                old.Output.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Erro ao fechar a saida de audio antiga do player");
            }

            old.Mixer.SetTrack(null);
        }

        old.Mixer.FadeTo(0f, FadeEnvelope.FramesFor(OutputMoveFade.TotalSeconds, old.Mixer.SampleRate), Close);
        var guard = default(ITimer);
        guard = _clock.CreateTimer(
            _ =>
            {
                Close();
                guard?.Dispose();
            },
            null,
            TimeSpan.FromSeconds(1),
            Timeout.InfiniteTimeSpan);
    }

    private OutputStatus DescribeOutput(PlayerSettings options, OutputChoice choice)
    {
        const string Prefix = "Sa\U000000EDda do player: ";
        if (choice.Kind == OutputChoiceKind.None)
        {
            return new OutputStatus(Prefix + "nenhuma sa\U000000EDda de \U000000E1udio ativa", choice.Kind, null);
        }

        if (!choice.IsFallback)
        {
            return new OutputStatus(Prefix + choice.Device!.Name, choice.Kind, choice.Device.Name);
        }

        var wished = string.IsNullOrWhiteSpace(options.OutputDeviceName) ? options.OutputRule.Text : options.OutputDeviceName;
        var verb = _state == PlayerState.Playing ? "tocando" : "tocar\U000000E1";
        return new OutputStatus($"{Prefix}{wished} \U00002014 desconectado, {verb} na sa\U000000EDda padr\U000000E3o", choice.Kind, choice.Device?.Name);
    }

    // ---- settings, library, devices ----

    // The handler runs on the thread that changed the setting (the UI thread for a click): it must never wait for Windows. The devices are asked only
    // when a setting about the OUTPUT changed (the device events and the device list keep their own path); any other change (volume, the tags switch,
    // "Igualar volume"...) touches the audio service not at all, so a service that is stuck cannot freeze the screen.
    private void OnSettingsChanged(AppSettings settings) => Locked(() =>
    {
        _library.Configure(settings.Player.Playlists);
        ApplyOptions(settings.Player);
        var outputKey = string.Join('|', settings.Player.OutputDeviceId, string.Join('+', settings.Player.OutputMatch));
        if (!string.Equals(outputKey, _outputKey, StringComparison.Ordinal))
        {
            _outputKey = outputKey;
            RefreshOutputLocked();
        }

        return PlayerResult.Ok;
    });

    private string? _outputKey;

    private void OnLibraryChanged(string name) => Locked(() =>
    {
        if (string.Equals(name, _playlist, StringComparison.OrdinalIgnoreCase))
        {
            SyncQueueLocked();
        }

        return PlayerResult.Ok;
    });

    private void OnDevicesChanged() => _post(() => Locked(() =>
    {
        if (!_disposed)
        {
            RefreshOutputLocked();
        }

        return PlayerResult.Ok;
    }));

    // Makes the queue, the volume and the master switch match the settings (idempotent).
    private void ApplyOptions(PlayerSettings options)
    {
        var crossfadeBefore = _applied?.CrossfadeSeconds ?? 0;
        var normalizeChanged = _applied is not null && (_applied.NormalizeEnabled != options.NormalizeEnabled || _applied.NormalizeTargetLufs != options.NormalizeTargetLufs);
        if (!options.Equals(_applied))
        {
            _applied = options;
            _changed = true;
        }

        _queue.Shuffle = options.Shuffle;
        _queue.Repeat = PlayerRepeatText.Parse(options.Repeat);
        _session?.Mixer.SetVolume(options.Volume / 100f, options.StartMuted, rampSeconds: _settings.Current.Audio.VolumeRampSeconds);
        _session?.Mixer.SetCrossfade(options.CrossfadeSeconds);
        _session?.Mixer.SetCrossfadeCurve(options.CrossfadeCurve);
        if (normalizeChanged && _session is { } normalizing)
        {
            // The target (or the switch) changed while tracks are held: each keeps its own measurement and ramps to its new gain over 300 ms.
            normalizing.Mixer.SetGains(track => _norms.TryGetValue(track, out var norm) ? NormalizationGain.Compute(norm.Applied, options.NormalizeTargetLufs, options.NormalizeEnabled).Linear : null);
            if (normalizing.Mixer.Track is { } held)
            {
                RefreshCurrentNormLocked(held);
            }
        }

        if (options.CrossfadeSeconds > 0 && !(crossfadeBefore > 0) && _session is { } running && running.Mixer.Track is { } playing && _current is { } now)
        {
            AnalyzeCurrent(running, playing, now.Path); // the crossfade was just switched on: measure the track that is already playing
        }

        if (!string.Equals(_playlist, options.CurrentPlaylist, StringComparison.Ordinal))
        {
            _playlist = options.CurrentPlaylist;
            _queue.Reset();
            _changed = true;
        }

        SyncQueueLocked();

        if (!options.Enabled && (_state != PlayerState.Stopped || _session is not null))
        {
            // The master switch went off: nothing plays, at once.
            _generation++;
            _retry?.Dispose();
            _retry = null;
            CloseSessionLocked();
            _state = PlayerState.Stopped;
            _current = null;
            _waitingPath = null;
            _fading = false;
            _changed = true;
        }

        EnsurePreloadLocked();
    }

    private void SyncQueueLocked()
    {
        var tracks = _library.Tracks(_playlist);
        if (!tracks.SequenceEqual(_queue.Tracks, StringComparer.Ordinal))
        {
            _queue.SetTracks(tracks);
            _changed = true;
        }
    }

    // Runs `work` under the lock and, when it changed anything the screen shows, raises StatusChanged after the lock is released.
    private PlayerResult Locked(Func<PlayerResult> work)
    {
        PlayerResult result;
        var raise = false;
        lock (_gate)
        {
            if (_disposed)
            {
                return PlayerResult.Fail("player encerrado");
            }

            _depth++;
            try
            {
                result = work();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro inesperado no player");
                _lastError = "Erro inesperado: " + ex.Message;
                _changed = true;
                result = PlayerResult.Fail("erro inesperado");
            }
            finally
            {
                _depth--;
            }

            if (_depth == 0)
            {
                raise = _changed;
                _changed = false;
            }
        }

        if (raise)
        {
            try
            {
                StatusChanged?.Invoke();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Um ouvinte do player falhou");
            }
        }

        return result;
    }

    private sealed class Session(IPlayerOutput output, PlayerMixer mixer, string? requestedId)
    {
        public string? RequestedId { get; } = requestedId;

        public IPlayerOutput Output { get; } = output;

        public PlayerMixer Mixer { get; } = mixer;
    }
}

/// <summary>What an alarm start did.</summary>
public enum AlarmStartOutcome
{
    /// <summary>Music started.</summary>
    Started,

    /// <summary>Music was already playing: it was left alone.</summary>
    AlreadyPlaying,

    /// <summary>It could not start (player off, empty or missing folder, no output): the scheduler may try again until the latest time.</summary>
    Failed,
}

/// <summary>The result of <see cref="MusicPlayer.StartFromAlarm"/>.</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="Error">Why, when it failed.</param>
public sealed record AlarmStart(AlarmStartOutcome Outcome, string? Error);
