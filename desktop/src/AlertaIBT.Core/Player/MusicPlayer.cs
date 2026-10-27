using AlertaIBT.Core.Audio;
using AlertaIBT.Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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
    private int _generation;
    private bool _fading;
    private bool _disposed;
    private bool _changed;
    private int _depth;
    private PlayerSettings? _applied;

    /// <param name="settings">The settings (the player section).</param>
    /// <param name="library">The playlists.</param>
    /// <param name="backend">Decoding and output.</param>
    /// <param name="audio">The Windows outputs (names and ids).</param>
    /// <param name="metadata">Titles and artists.</param>
    /// <param name="clock">Times the retries.</param>
    /// <param name="logger">Receives what happens (names and counts only).</param>
    /// <param name="post">Runs work that must not happen on the audio or a Windows callback thread; the thread pool by default, inline in tests.</param>
    /// <param name="random">The draw of the shuffle; a seeded one in tests.</param>
    public MusicPlayer(
        SettingsService settings,
        PlaylistLibrary library,
        IPlayerBackend backend,
        IAudioSystem audio,
        ITrackMetadataReader metadata,
        TimeProvider? clock = null,
        ILogger? logger = null,
        Action<Action>? post = null,
        Random? random = null)
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
        _library.Configure(Options.Playlists);
        Locked(() =>
        {
            ApplyOptions(Options);
            RefreshOutputLocked();
            return PlayerResult.Ok;
        });
    }

    /// <inheritdoc />
    public PlayerResult Play() => Locked(() => PlayLocked(fadeInSeconds: 0));

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
            return PlayLocked(Options.AlarmFadeSeconds);
        });
        return result.Success ? new AlarmStart(AlarmStartOutcome.Started, null) : new AlarmStart(AlarmStartOutcome.Failed, result.Error);
    }

    /// <inheritdoc />
    public PlayerResult Pause() => Locked(() => FadeOutLocked(PlayerState.Paused));

    /// <inheritdoc />
    public PlayerResult Stop() => Locked(() => FadeOutLocked(PlayerState.Stopped));

    /// <inheritdoc />
    public PlayerResult Next() => Locked(() =>
    {
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
            return new PlayerStatus(
                options.Enabled,
                _state,
                _fading,
                _current,
                mixer?.Position ?? TimeSpan.Zero,
                mixer?.Duration ?? TimeSpan.Zero,
                _playlist,
                _queue.Tracks.Count,
                options.Volume,
                options.StartMuted,
                options.Shuffle,
                PlayerRepeatText.Parse(options.Repeat),
                _choice is null ? OutputStatus.Unknown : DescribeOutput(options, _choice),
                _lastError);
        }
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
            _retry?.Dispose();
            _retry = null;
            _generation++;
            CloseSessionLocked();
        }
    }

    // ---- commands (the lock is held) ----

    private PlayerResult PlayLocked(int fadeInSeconds)
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
            return PlayerResult.Ok;
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
                }

                _changed = true;
            }

            return PlayerResult.Ok;
        }

        var generation = ++_generation;
        _fading = true;
        _changed = true;
        var session = _session;
        var frames = FadeEnvelope.FramesFor(Options.StopFadeSeconds, session.Mixer.SampleRate);
        session.Mixer.FadeTo(0f, frames, () => Locked(() => FinishFadeOutLocked(generation, session, target)));
        return PlayerResult.Ok;
    }

    private PlayerResult FinishFadeOutLocked(int generation, Session session, PlayerState target)
    {
        if (generation != _generation || !ReferenceEquals(session, _session))
        {
            return PlayerResult.Ok; // a newer command took over
        }

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
    private PlayerResult OpenAndPlayLocked(string? path, int fadeInSeconds, bool skipFade = false)
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
                if (_state == PlayerState.Playing && !skipFade)
                {
                    // A track change inside a running output: the new track starts at full gain.
                    session.Mixer.SetTrack(pcm);
                    session.Mixer.ResetGain();
                }
                else
                {
                    session.Mixer.SetTrack(pcm);
                    session.Mixer.FadeFromSilence(FadeEnvelope.FramesFor(fadeInSeconds > 0 ? fadeInSeconds : skipFade ? SkipFade.TotalSeconds : 0, session.Mixer.SampleRate));
                }

                _current = info;
                _state = PlayerState.Playing;
                _fading = false;
                _waitingPath = null;
                _lastError = null;
                _changed = true;
                _logger.LogInformation("Tocando: {File}", info.FileName);
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
        PlayerMixer? mixer = null;
        var output = _backend.OpenOutput(wantedId, (rate, channels) =>
        {
            mixer = new PlayerMixer(rate, channels, _post);
            return mixer;
        });
        var session = new Session(output, mixer ?? throw new InvalidOperationException("The output did not ask for a source."), wantedId);
        var options = Options;
        mixer.SetVolume(options.Volume / 100f, options.StartMuted, immediate: true);
        mixer.TrackEnded += () => OnTrackEnded(session);
        output.Failed += error => OnOutputFailed(session, error);
        _session = session;
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
        }
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

    private void OnSettingsChanged(AppSettings settings) => Locked(() =>
    {
        _library.Configure(settings.Player.Playlists);
        ApplyOptions(settings.Player);
        RefreshOutputLocked();
        return PlayerResult.Ok;
    });

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
        if (!options.Equals(_applied))
        {
            _applied = options;
            _changed = true;
        }

        _queue.Shuffle = options.Shuffle;
        _queue.Repeat = PlayerRepeatText.Parse(options.Repeat);
        _session?.Mixer.SetVolume(options.Volume / 100f, options.StartMuted);

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
