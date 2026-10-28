using System.Globalization;
using AlertaIBT.Core.Audio;
using AlertaIBT.Core.Player;
using AlertaIBT.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AlertaIBT.App.ViewModels;

/// <summary>
/// The music player for the screens: what the "Som" tab and the quick panel bind to. It shows the engine's state (track, position, playlist,
/// output, next alarm, last error), has a command for every action and a writable property for every player setting (each write goes to
/// the settings, which the engine follows). Everything here lives on the UI thread; the engine's commands are run off it (they may open a
/// file or a device) and a failure shows up in <see cref="LastError"/>.
/// </summary>
public sealed class PlayerViewModel : ObservableObject, IDisposable
{
    /// <summary>How often the position of the track is refreshed while it plays.</summary>
    public static readonly TimeSpan PositionInterval = TimeSpan.FromMilliseconds(500);

    private readonly SettingsService _settings;
    private readonly MusicPlayer _player;
    private readonly AlarmScheduler _alarms;
    private readonly IAudioSystem? _audio;
    private readonly Action<Action> _post;
    private readonly Action<Action> _run;
    private readonly ILogger _logger;
    private readonly ITimer _timer;
    private bool _syncing;
    private bool _disposed;

    private PlayerStatus _status;
    private PlayerSettings _options;
    private string _nextAlarmText = string.Empty;
    private string? _commandError;
    private IReadOnlyList<AudioDevice> _outputDevices = [];

    /// <param name="settings">The settings: every writable property is saved here.</param>
    /// <param name="player">The engine.</param>
    /// <param name="alarms">The alarms (for the next-alarm line).</param>
    /// <param name="audio">The Windows outputs for the output list; may be <c>null</c> (an empty list).</param>
    /// <param name="post">Runs an action on the UI thread; runs it at once when null (tests).</param>
    /// <param name="run">Runs an engine command off the UI thread; the thread pool when null.</param>
    /// <param name="clock">Drives the position refresh.</param>
    /// <param name="logger">Receives failures.</param>
    public PlayerViewModel(
        SettingsService settings,
        MusicPlayer player,
        AlarmScheduler alarms,
        IAudioSystem? audio = null,
        Action<Action>? post = null,
        Action<Action>? run = null,
        TimeProvider? clock = null,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(alarms);

        _settings = settings;
        _player = player;
        _alarms = alarms;
        _audio = audio;
        _post = post ?? (action => action());
        _run = run ?? (action => _ = Task.Run(action));
        _logger = logger ?? NullLogger.Instance;
        _status = player.GetStatus();
        _options = settings.Current.Player;
        _nextAlarmText = alarms.NextAlarmText;

        PlayCommand = new RelayCommand(() => Execute(_player.Play));
        PauseCommand = new RelayCommand(() => Execute(_player.Pause));
        StopCommand = new RelayCommand(() => Execute(_player.Stop));
        NextCommand = new RelayCommand(() => Execute(_player.Next));
        PreviousCommand = new RelayCommand(() => Execute(_player.Previous));
        PlayPauseCommand = new RelayCommand(() => Execute(() => IsPlaying && !IsFading ? _player.Pause() : _player.Play()));
        ToggleShuffleCommand = new RelayCommand(() => Shuffle = !Shuffle);
        CycleRepeatCommand = new RelayCommand(() => Repeat = Repeat switch { PlayerRepeat.Off => PlayerRepeat.All, PlayerRepeat.All => PlayerRepeat.One, _ => PlayerRepeat.Off });
        RefreshCommand = new RelayCommand(() => Execute(() =>
        {
            _player.Refresh();
            return PlayerResult.Ok;
        }));

        _player.StatusChanged += OnPlayerChanged;
        _settings.Changed += OnSettingsChanged;
        _alarms.NextAlarmChanged += OnAlarmTextChanged;
        if (_audio is not null)
        {
            _audio.DevicesChanged += OnDevicesChanged;
        }

        _timer = (clock ?? TimeProvider.System).CreateTimer(_ => _post(RefreshPosition), null, PositionInterval, PositionInterval);
        ReloadOutputDevices();
    }

    // ---- commands ----

    /// <summary>Starts the next track (random when shuffle is on).</summary>
    public IRelayCommand PlayCommand { get; }

    /// <summary>Fades out (per the setting) and pauses.</summary>
    public IRelayCommand PauseCommand { get; }

    /// <summary>Fades out (per the setting) and stops.</summary>
    public IRelayCommand StopCommand { get; }

    /// <summary>The next track.</summary>
    public IRelayCommand NextCommand { get; }

    /// <summary>Restarts the track after 3 s, else the previous one.</summary>
    public IRelayCommand PreviousCommand { get; }

    /// <summary>The one big button: pause while playing, play otherwise.</summary>
    public IRelayCommand PlayPauseCommand { get; }

    /// <summary>Turns shuffle on or off.</summary>
    public IRelayCommand ToggleShuffleCommand { get; }

    /// <summary>Off, then all, then one, then off again.</summary>
    public IRelayCommand CycleRepeatCommand { get; }

    /// <summary>"Atualizar": scans the current playlist's folder again and looks at the outputs again.</summary>
    public IRelayCommand RefreshCommand { get; }

    // ---- the state (read only) ----

    /// <summary>Whether the master switch "Player ligado" is on (the quick panel row is shown only then).</summary>
    public bool IsEnabled => _options.Enabled;

    /// <summary>What the player is doing.</summary>
    public PlayerState State => _status.State;

    /// <summary>The state in Portuguese: Tocando, Pausado, Parado or Aguardando sa\U000000EDda.</summary>
    public string StateText => _status.State switch
    {
        PlayerState.Playing => "Tocando",
        PlayerState.Paused => "Pausado",
        PlayerState.Waiting => "Aguardando sa\U000000EDda",
        _ => "Parado",
    };

    /// <summary>Whether a track is playing (it may be fading in or out).</summary>
    public bool IsPlaying => _status.IsPlaying;

    /// <summary>Whether a fade (pause or stop) is running.</summary>
    public bool IsFading => _status.Fading;

    /// <summary>Whether there is a current track.</summary>
    public bool HasTrack => _status.Track is not null;

    /// <summary>The title of the track, or empty.</summary>
    public string TrackTitle => _status.Track?.Title ?? string.Empty;

    /// <summary>The artist, or empty.</summary>
    public string TrackArtist => _status.Track?.Artist ?? string.Empty;

    /// <summary>The file name of the track, or empty.</summary>
    public string TrackFileName => _status.Track?.FileName ?? string.Empty;

    /// <summary>The line of the quick panel: the title, or "Nada tocando".</summary>
    public string QuickTitle => _status.Track is { } track
        ? (string.IsNullOrEmpty(track.Artist) ? track.Title : $"{track.Title} \U00002014 {track.Artist}")
        : "Nada tocando";

    /// <summary>How far into the track.</summary>
    public TimeSpan Position => _status.Position;

    /// <summary>The length of the track.</summary>
    public TimeSpan Duration => _status.Duration;

    /// <summary>Position over length, 0 to 1 (for a progress bar).</summary>
    public double Progress => _status.Duration > TimeSpan.Zero ? Math.Clamp(_status.Position / _status.Duration, 0, 1) : 0;

    /// <summary>"1:23 / 4:11", or empty when nothing plays.</summary>
    public string PositionText => _status.Duration > TimeSpan.Zero ? $"{Clock(_status.Position)} / {Clock(_status.Duration)}" : string.Empty;

    /// <summary>The name of the selected playlist.</summary>
    public string PlaylistName => _status.Playlist;

    /// <summary>How many tracks the selected playlist has.</summary>
    public int TrackCount => _status.TrackCount;

    /// <summary>"237 m\U000000FAsicas".</summary>
    public string TrackCountText => string.Create(CultureInfo.InvariantCulture, $"{_status.TrackCount} m\U000000FAsicas");

    /// <summary>Where the sound goes: <c>Sa\U000000EDda do player: WING-USB (Out 3-4)</c>, or that it is disconnected and the default output stands in.</summary>
    public string OutputText => _status.Output.Text;

    /// <summary>"Pr\U000000F3ximo despertador: domingo 18:00" (or why there is none).</summary>
    public string NextAlarmText => _nextAlarmText;

    /// <summary>The last problem (a file that could not be played, no output, a refused command), or empty.</summary>
    public string LastError => _commandError ?? _status.LastError ?? string.Empty;

    /// <summary>Whether there is a problem to show.</summary>
    public bool HasError => LastError.Length > 0;

    /// <summary>The resource key of the play/pause icon: pause while playing, play otherwise.</summary>
    public string PlayPauseIconKey => IsPlaying && !IsFading ? "LucidePause" : "LucidePlay";

    /// <summary>The tooltip of the play/pause button.</summary>
    public string PlayPauseTip => IsPlaying && !IsFading ? "Pausar a m\U000000FAsica" : "Tocar a m\U000000FAsica";

    /// <summary>The active outputs of Windows, for the output list.</summary>
    public IReadOnlyList<AudioDevice> OutputDevices => _outputDevices;

    /// <summary>The playlists.</summary>
    public IReadOnlyList<PlaylistEntry> Playlists => _options.Playlists;

    /// <summary>The alarms.</summary>
    public IReadOnlyList<AlarmEntry> Alarms => _options.Alarms;

    /// <summary>The words of the output rule, such as <c>WING + 3-4</c> (empty when there is none).</summary>
    public string OutputRuleText => new DeviceMatchRule(_options.OutputMatch).Text;

    /// <summary>The name of the saved output (the wished one), or empty.</summary>
    public string OutputDeviceName => _options.OutputDeviceName ?? string.Empty;

    // ---- the settings (read and write) ----

    /// <summary>"Player ligado": when off nothing plays, alarms do not fire, the API and the media keys are ignored.</summary>
    public bool PlayerOn
    {
        get => _options.Enabled;
        set => Write(value, _options.Enabled, p => p with { Enabled = value });
    }

    /// <summary>The player's own volume, 0 to 100 (the "Volumes" card row that replaced Spotify).</summary>
    public int Volume
    {
        get => _options.Volume;
        set => Write(Math.Clamp(value, 0, 100), _options.Volume, p => p with { Volume = Math.Clamp(value, 0, 100) });
    }

    /// <summary>"Come\U000000E7ar mudo": the player is muted (the row's mute box).</summary>
    public bool StartMuted
    {
        get => _options.StartMuted;
        set => Write(value, _options.StartMuted, p => p with { StartMuted = value });
    }

    /// <summary>Shuffle on or off.</summary>
    public bool Shuffle
    {
        get => _options.Shuffle;
        set => Write(value, _options.Shuffle, p => p with { Shuffle = value });
    }

    /// <summary>Repeat off, all or one.</summary>
    public PlayerRepeat Repeat
    {
        get => PlayerRepeatText.Parse(_options.Repeat);
        set => Write(value, Repeat, p => p with { Repeat = PlayerRepeatText.ToText(value) });
    }

    /// <summary>The icon key and tooltip text of the repeat button.</summary>
    public string RepeatText => Repeat switch { PlayerRepeat.Off => "Repetir: desligado", PlayerRepeat.One => "Repetir: esta m\U000000FAsica", _ => "Repetir: toda a lista" };

    /// <summary>The resource key of the repeat icon: the 1 version for "esta musica", the plain one otherwise.</summary>
    public string RepeatIconKey => Repeat == PlayerRepeat.One ? "LucideRepeat1" : "LucideRepeat";

    /// <summary>"Teclas de m\U000000EDdia controlam o player".</summary>
    public bool MediaKeys
    {
        get => _options.MediaKeys;
        set => Write(value, _options.MediaKeys, p => p with { MediaKeys = value });
    }

    /// <summary>"Despertadores ligados".</summary>
    public bool AlarmsEnabled
    {
        get => _options.AlarmsEnabled;
        set => Write(value, _options.AlarmsEnabled, p => p with { AlarmsEnabled = value });
    }

    /// <summary>"Ao parar (pausa/stop pela API ou bot\U000000E3o)": the fade-out in seconds, one of <see cref="PlayerSettings.FadeChoices"/> (0 is "seco").</summary>
    public int StopFadeSeconds
    {
        get => _options.StopFadeSeconds;
        set => Write(value, _options.StopFadeSeconds, p => p with { StopFadeSeconds = value });
    }

    /// <summary>"Ao come\U000000E7ar pelo despertador": the fade-in in seconds, one of <see cref="PlayerSettings.FadeChoices"/>.</summary>
    public int AlarmFadeSeconds
    {
        get => _options.AlarmFadeSeconds;
        set => Write(value, _options.AlarmFadeSeconds, p => p with { AlarmFadeSeconds = value });
    }

    /// <summary>The selected playlist (by name).</summary>
    public string CurrentPlaylist
    {
        get => _options.CurrentPlaylist;
        set => Write(value, _options.CurrentPlaylist, p => p with { CurrentPlaylist = value });
    }

    // ---- editing the lists and the output ----

    /// <summary>Adds a playlist (a name and a folder) when the name is new; returns whether it was added.</summary>
    public bool AddPlaylist(string name, string folder)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length == 0 || string.IsNullOrWhiteSpace(folder) || _options.FindPlaylist(trimmed) is not null)
        {
            return false;
        }

        Change(p => p with { Playlists = [.. p.Playlists, new PlaylistEntry(trimmed, folder.Trim())] });
        return true;
    }

    /// <summary>Removes a playlist (the selected one moves to the first that is left).</summary>
    public void RemovePlaylist(string name) =>
        Change(p => p with { Playlists = p.Playlists.Where(l => !string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase)).ToList() });

    /// <summary>
    /// Renames a playlist (the selected one and the alarms that play it follow). False when the name is blank, the playlist does not exist or
    /// another one already has the new name.
    /// </summary>
    public bool RenamePlaylist(string name, string newName)
    {
        var trimmed = newName?.Trim() ?? string.Empty;
        var current = _options.FindPlaylist(name);
        if (current is null || trimmed.Length == 0)
        {
            return false;
        }

        var sameName = string.Equals(current.Name, trimmed, StringComparison.OrdinalIgnoreCase);
        if (!sameName && _options.FindPlaylist(trimmed) is not null)
        {
            return false;
        }

        var old = current.Name;
        Change(p => p with
        {
            Playlists = p.Playlists.Select(l => string.Equals(l.Name, old, StringComparison.OrdinalIgnoreCase) ? l with { Name = trimmed } : l).ToList(),
            CurrentPlaylist = string.Equals(p.CurrentPlaylist, old, StringComparison.OrdinalIgnoreCase) ? trimmed : p.CurrentPlaylist,
            Alarms = p.Alarms.Select(a => string.Equals(a.Playlist, old, StringComparison.OrdinalIgnoreCase) ? a with { Playlist = trimmed } : a).ToList(),
        });
        return true;
    }

    /// <summary>Changes the folder of a playlist.</summary>
    public void SetPlaylistFolder(string name, string folder) =>
        Change(p => p with { Playlists = p.Playlists.Select(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase) ? l with { Folder = folder } : l).ToList() });

    /// <summary>Adds an alarm with the owner defaults (18:00, not after 18:25, Sunday only) on the selected playlist; returns it.</summary>
    public AlarmEntry AddAlarm()
    {
        var alarm = AlarmEntry.CreateDefault("alarme-" + Guid.NewGuid().ToString("N")[..8], _options.CurrentPlaylist);
        Change(p => p with { Alarms = [.. p.Alarms, alarm] });
        return alarm;
    }

    /// <summary>Replaces the alarm that has the same <see cref="AlarmEntry.Id"/>. Changing the start time lets it fire again today.</summary>
    public void UpdateAlarm(AlarmEntry alarm)
    {
        ArgumentNullException.ThrowIfNull(alarm);

        Change(p => p with
        {
            Alarms = p.Alarms
                .Select(a => a.Id == alarm.Id ? (a.Start == alarm.Start ? alarm : alarm with { LastFiredOn = null }) : a)
                .ToList(),
        });
    }

    /// <summary>Removes an alarm.</summary>
    public void RemoveAlarm(string id) => Change(p => p with { Alarms = p.Alarms.Where(a => a.Id != id).ToList() });

    /// <summary>
    /// Chooses the output by hand (<c>null</c> goes back to the church default: the rule <c>WING + 3-4</c>). A chosen device is kept by its id
    /// and its name, and the rule becomes its name so the same device is found again on another USB port.
    /// </summary>
    public void ChooseOutput(AudioDevice? device) =>
        Change(p => device is null
            ? p with { OutputDeviceId = null, OutputDeviceName = null, OutputMatch = PlayerSettings.DefaultOutputMatch }
            : p with { OutputDeviceId = device.Id, OutputDeviceName = device.Name, OutputMatch = [device.Name] });

    /// <summary>Sets the name rule of the output from a line such as <c>WING + 3-4</c>.</summary>
    public void SetOutputRule(string text) => Change(p => p with { OutputMatch = DeviceMatchRule.Parse(text).AllOf });

    /// <summary>Looks at the active outputs again.</summary>
    public void ReloadOutputDevices()
    {
        try
        {
            _outputDevices = _audio?.ActiveRenderDevices() ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Nao foi possivel listar as saidas de audio");
            _outputDevices = [];
        }

        OnPropertyChanged(nameof(OutputDevices));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _player.StatusChanged -= OnPlayerChanged;
        _settings.Changed -= OnSettingsChanged;
        _alarms.NextAlarmChanged -= OnAlarmTextChanged;
        if (_audio is not null)
        {
            _audio.DevicesChanged -= OnDevicesChanged;
        }

        _timer.Dispose();
    }

    // ---- internals ----

    private static string Clock(TimeSpan span) =>
        string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalMinutes}:{span.Seconds:00}");

    private void Execute(Func<PlayerResult> command) => _run(() =>
    {
        try
        {
            var result = command();
            _post(() => ShowCommandResult(result));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Um comando do player falhou");
            _post(() => ShowCommandResult(PlayerResult.Fail(ex.Message)));
        }
    });

    private void ShowCommandResult(PlayerResult result)
    {
        var message = result.Success ? null : result.Error;
        if (message != _commandError)
        {
            _commandError = message;
            OnPropertyChanged(nameof(LastError));
            OnPropertyChanged(nameof(HasError));
        }

        Refresh();
    }

    private void Write<T>(T value, T current, Func<PlayerSettings, PlayerSettings> edit)
    {
        if (_syncing || EqualityComparer<T>.Default.Equals(value, current))
        {
            return;
        }

        Change(edit);
    }

    private void Change(Func<PlayerSettings, PlayerSettings> edit) =>
        _settings.Update(s => s with { Player = edit(s.Player) });

    private void OnPlayerChanged() => _post(Refresh);

    private void OnSettingsChanged(AppSettings settings) => _post(Refresh);

    private void OnAlarmTextChanged() => _post(() =>
    {
        _nextAlarmText = _alarms.NextAlarmText;
        OnPropertyChanged(nameof(NextAlarmText));
    });

    private void OnDevicesChanged() => _post(ReloadOutputDevices);

    // The position moves while it plays; nothing else is refreshed by the timer.
    private void RefreshPosition()
    {
        if (_disposed || !_status.IsPlaying)
        {
            return;
        }

        _status = _player.GetStatus();
        OnPropertyChanged(nameof(Position));
        OnPropertyChanged(nameof(Duration));
        OnPropertyChanged(nameof(Progress));
        OnPropertyChanged(nameof(PositionText));
    }

    // Takes the engine's status and the settings again and tells the screen what changed.
    private void Refresh()
    {
        if (_disposed)
        {
            return;
        }

        var status = _player.GetStatus();
        var options = _settings.Current.Player;
        var before = (Status: _status, Options: _options);
        _status = status;
        _options = options;
        _nextAlarmText = _alarms.NextAlarmText;

        _syncing = true;
        try
        {
            if (before.Status != status)
            {
                foreach (var name in StatusProperties)
                {
                    OnPropertyChanged(name);
                }
            }

            if (!before.Options.Equals(options))
            {
                foreach (var name in SettingProperties)
                {
                    OnPropertyChanged(name);
                }
            }

            OnPropertyChanged(nameof(NextAlarmText));
        }
        finally
        {
            _syncing = false;
        }
    }

    private static readonly string[] StatusProperties =
    [
        nameof(State), nameof(StateText), nameof(IsPlaying), nameof(IsFading), nameof(HasTrack), nameof(TrackTitle), nameof(TrackArtist),
        nameof(TrackFileName), nameof(QuickTitle), nameof(Position), nameof(Duration), nameof(Progress), nameof(PositionText),
        nameof(PlaylistName), nameof(TrackCount), nameof(TrackCountText), nameof(OutputText), nameof(LastError), nameof(HasError),
        nameof(PlayPauseIconKey), nameof(PlayPauseTip),
    ];

    private static readonly string[] SettingProperties =
    [
        nameof(IsEnabled), nameof(PlayerOn), nameof(Volume), nameof(StartMuted), nameof(Shuffle), nameof(Repeat), nameof(RepeatText), nameof(RepeatIconKey),
        nameof(MediaKeys), nameof(AlarmsEnabled), nameof(StopFadeSeconds), nameof(AlarmFadeSeconds), nameof(CurrentPlaylist), nameof(Playlists),
        nameof(Alarms), nameof(OutputRuleText), nameof(OutputDeviceName),
    ];
}
