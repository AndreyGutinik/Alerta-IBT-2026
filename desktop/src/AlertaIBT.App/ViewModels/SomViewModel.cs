using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using AlertaIBT.Core.Audio;
using AlertaIBT.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AlertaIBT.App.ViewModels;

/// <summary>
/// The "Som" tab: the volumes the app sets, the routing of the outputs (the Windows default output and its lock, the player, Holyrics), the
/// music player with its playlists and fades, and the alarms. Every control saves 600 ms after its last change through the
/// <see cref="Autosaver"/>; the player's own buttons act at once. The sound itself is the business of <see cref="AudioController"/> and the
/// player engine; this class only shows their state and writes the settings they follow.
/// </summary>
public sealed partial class SomViewModel : ObservableObject, IDisposable
{
    private readonly SettingsService _settings;
    private readonly Autosaver _saver;
    private readonly AudioController _audio;
    private readonly Func<string?> _pickFolder;
    private readonly Action<Action> _post;
    private readonly ILogger _logger;
    private bool _quiet;
    private bool _disposed;

    [ObservableProperty]
    private bool _adjustAtStart;

    [ObservableProperty]
    private bool _lockOutput;

    [ObservableProperty]
    private bool _holyricsRoute;

    [ObservableProperty]
    private string _appliedLine = string.Empty;

    [ObservableProperty]
    private bool _isApplying;

    [ObservableProperty]
    private bool _isRenaming;

    [ObservableProperty]
    private string _renameText = string.Empty;

    [ObservableProperty]
    private string _playlistMessage = string.Empty;

    [ObservableProperty]
    private string _playlistInfo = string.Empty;

    /// <param name="settings">The settings.</param>
    /// <param name="saver">The autosave (the "Salvo" stamp of the tab).</param>
    /// <param name="audio">The sound rules: the status of the outputs and "Aplicar agora".</param>
    /// <param name="player">The music player.</param>
    /// <param name="pickFolder">Asks the user for a folder (the playlist's); <c>null</c> when cancelled.</param>
    /// <param name="post">Runs an action on the UI thread; runs it at once when null (tests).</param>
    /// <param name="logger">Receives failures.</param>
    public SomViewModel(
        SettingsService settings,
        Autosaver saver,
        AudioController audio,
        PlayerViewModel player,
        Func<string?> pickFolder,
        Action<Action>? post = null,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(saver);
        ArgumentNullException.ThrowIfNull(audio);
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(pickFolder);

        _settings = settings;
        _saver = saver;
        _audio = audio;
        Player = player;
        _pickFolder = pickFolder;
        _post = post ?? (action => action());
        _logger = logger ?? NullLogger.Instance;

        MasterRow = new VolumeRow(
            () => Audio.MasterVolume,
            () => Audio.MasterMuted,
            volume => QueueAudio("audio.masterVolume", a => a with { MasterVolume = volume }),
            muted => QueueAudio("audio.masterMuted", a => a with { MasterMuted = muted }));
        HolyricsRow = new VolumeRow(
            () => HolyricsApp()?.Volume ?? 100,
            () => HolyricsApp()?.Muted ?? false,
            volume => QueueAudio("audio.holyricsVolume", a => WithHolyrics(a, e => e with { Volume = volume })),
            muted => QueueAudio("audio.holyricsMuted", a => WithHolyrics(a, e => e with { Muted = muted })));
        PlayerRow = new VolumeRow(
            () => _settings.Current.Player.Volume,
            () => _settings.Current.Player.StartMuted,
            volume => _saver.Queue("player.volume", s => s with { Player = s.Player with { Volume = volume } }),
            muted => _saver.Queue("player.startMuted", s => s with { Player = s.Player with { StartMuted = muted } }));
        SystemSoundsRow = new VolumeRow(
            () => Audio.SystemSoundsVolume,
            () => Audio.SystemSoundsMuted,
            volume => QueueAudio("audio.systemSoundsVolume", a => a with { SystemSoundsVolume = volume }),
            muted => QueueAudio("audio.systemSoundsMuted", a => a with { SystemSoundsMuted = muted }));

        DefaultOutput = new OutputRow(
            () => (Audio.OutputDeviceId, Audio.OutputDeviceName),
            () => AutoText(AudioSettings.DefaultOutputWords),
            choice =>
            {
                QueueAudio(
                    "audio.output",
                    a => choice.IsAuto
                        ? a with { OutputDeviceId = null, OutputDeviceName = null, OutputMatch = AudioSettings.DefaultOutputWords }
                        : a with { OutputDeviceId = choice.Id, OutputDeviceName = choice.Name, OutputMatch = [choice.Name ?? choice.Id ?? string.Empty] });
                _saver.FlushNow();
            });
        PlayerOutput = new OutputRow(
            () => (_settings.Current.Player.OutputDeviceId, _settings.Current.Player.OutputDeviceName),
            () => AutoText(PlayerSettings.DefaultOutputMatch),
            choice => Player.ChooseOutput(choice.IsAuto ? null : new AudioDevice(choice.Id ?? string.Empty, choice.Name ?? choice.Id ?? string.Empty)));
        HolyricsOutput = new OutputRow(
            () => (Audio.HolyricsDeviceId, Audio.HolyricsDeviceName),
            () => AutoText(AudioSettings.DefaultHolyricsWords),
            choice =>
            {
                QueueAudio(
                    "audio.holyricsOutput",
                    a => choice.IsAuto
                        ? a with { HolyricsDeviceId = null, HolyricsDeviceName = null, HolyricsMatch = AudioSettings.DefaultHolyricsWords }
                        : a with { HolyricsDeviceId = choice.Id, HolyricsDeviceName = choice.Name, HolyricsMatch = [choice.Name ?? choice.Id ?? string.Empty] });
                _saver.FlushNow();
            });

        StopFadeOptions = FadeOptions(() => _settings.Current.Player.StopFadeSeconds, seconds => Player.StopFadeSeconds = seconds);
        AlarmFadeOptions = FadeOptions(() => _settings.Current.Player.AlarmFadeSeconds, seconds => Player.AlarmFadeSeconds = seconds);

        ApplyNowCommand = new AsyncRelayCommand(ApplyNowAsync, () => !IsApplying);
        AddPlaylistCommand = new RelayCommand(AddPlaylist);
        RemovePlaylistCommand = new RelayCommand(RemovePlaylist);
        ChangeFolderCommand = new RelayCommand(ChangeFolder);
        StartRenameCommand = new RelayCommand(StartRename);
        CommitRenameCommand = new RelayCommand(CommitRename);
        CancelRenameCommand = new RelayCommand(() => IsRenaming = false);
        AddAlarmCommand = new RelayCommand(() => Player.AddAlarm());

        LoadSwitches();
        _settings.Changed += OnSettingsChanged;
        _audio.StatusChanged += OnAudioChanged;
        Player.PropertyChanged += OnPlayerChanged;
        RefreshAll();
    }

    /// <summary>The music player (its state and buttons).</summary>
    public PlayerViewModel Player { get; }

    /// <summary>The autosave stamp of this tab.</summary>
    public Autosaver Saver => _saver;

    /// <summary>"Volume geral do Windows".</summary>
    public VolumeRow MasterRow { get; }

    /// <summary>"Holyrics".</summary>
    public VolumeRow HolyricsRow { get; }

    /// <summary>"Player" (the volume of the music player; it took the place of the Spotify row).</summary>
    public VolumeRow PlayerRow { get; }

    /// <summary>"Sons do Windows (notificacoes e afins)".</summary>
    public VolumeRow SystemSoundsRow { get; }

    /// <summary>"Saida padrao do Windows".</summary>
    public OutputRow DefaultOutput { get; }

    /// <summary>"Player" in the outputs.</summary>
    public OutputRow PlayerOutput { get; }

    /// <summary>"Holyrics" in the outputs.</summary>
    public OutputRow HolyricsOutput { get; }

    /// <summary>"Ao parar": 5 s, 3 s, seco.</summary>
    public IReadOnlyList<FadeOption> StopFadeOptions { get; }

    /// <summary>"Ao comecar pelo despertador": 5 s, 3 s, seco.</summary>
    public IReadOnlyList<FadeOption> AlarmFadeOptions { get; }

    /// <summary>The alarms.</summary>
    public ObservableCollection<AlarmRow> Alarms { get; } = [];

    /// <summary>"Aplicar agora": sets every volume (and the Holyrics output) now.</summary>
    public IAsyncRelayCommand ApplyNowCommand { get; }

    /// <summary>Adds a playlist: asks for a folder and names it after the folder.</summary>
    public IRelayCommand AddPlaylistCommand { get; }

    /// <summary>Removes the selected playlist (the music files stay where they are).</summary>
    public IRelayCommand RemovePlaylistCommand { get; }

    /// <summary>Changes the folder of the selected playlist.</summary>
    public IRelayCommand ChangeFolderCommand { get; }

    /// <summary>Opens the name box of the selected playlist.</summary>
    public IRelayCommand StartRenameCommand { get; }

    /// <summary>Saves the new name.</summary>
    public IRelayCommand CommitRenameCommand { get; }

    /// <summary>Closes the name box.</summary>
    public IRelayCommand CancelRenameCommand { get; }

    /// <summary>Adds an alarm (18:00, not after 18:25, Sunday only).</summary>
    public IRelayCommand AddAlarmCommand { get; }

    /// <summary>Whether the line under "Aplicar agora" has text.</summary>
    public bool HasAppliedLine => AppliedLine.Length > 0;

    /// <summary>Whether the playlist message (a name that already exists) has text.</summary>
    public bool HasPlaylistMessage => PlaylistMessage.Length > 0;

    /// <summary>Whether there is a playlist to remove, rename or change.</summary>
    public bool HasPlaylist => Player.Playlists.Count > 0;

    /// <summary>Whether the name box is closed (the playlist list is showing).</summary>
    public bool IsNotRenaming => !IsRenaming;

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _settings.Changed -= OnSettingsChanged;
        _audio.StatusChanged -= OnAudioChanged;
        Player.PropertyChanged -= OnPlayerChanged;
    }

    // ---- switches ----

    partial void OnAdjustAtStartChanged(bool value)
    {
        if (!_quiet)
        {
            QueueAudio("audio.enabled", a => a with { Enabled = value });
        }
    }

    // The lock and the Holyrics output take effect at once (saved now, the audio controller follows the setting).
    partial void OnLockOutputChanged(bool value)
    {
        if (!_quiet)
        {
            QueueAudio("audio.lockOutput", a => a with { LockOutput = value });
            _saver.FlushNow();
        }
    }

    partial void OnHolyricsRouteChanged(bool value)
    {
        if (!_quiet)
        {
            QueueAudio("audio.holyricsRoute", a => a with { HolyricsRoute = value });
            _saver.FlushNow();
        }
    }

    partial void OnAppliedLineChanged(string value) => OnPropertyChanged(nameof(HasAppliedLine));

    partial void OnPlaylistMessageChanged(string value) => OnPropertyChanged(nameof(HasPlaylistMessage));

    partial void OnIsRenamingChanged(bool value) => OnPropertyChanged(nameof(IsNotRenaming));

    partial void OnIsApplyingChanged(bool value) => ApplyNowCommand.NotifyCanExecuteChanged();

    // ---- commands ----

    private async Task ApplyNowAsync()
    {
        IsApplying = true;
        try
        {
            _saver.FlushNow(); // what was just typed counts
            await _audio.ApplyNowAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha em Aplicar agora");
        }
        finally
        {
            IsApplying = false;
            RefreshAll();
        }
    }

    private void AddPlaylist()
    {
        var folder = _pickFolder();
        if (string.IsNullOrWhiteSpace(folder))
        {
            return;
        }

        var baseName = Path.GetFileName(folder.TrimEnd('\\', '/'));
        if (string.IsNullOrWhiteSpace(baseName))
        {
            baseName = "Lista";
        }

        var name = baseName;
        for (var n = 2; _settings.Current.Player.FindPlaylist(name) is not null; n++)
        {
            name = $"{baseName} {n}";
        }

        if (Player.AddPlaylist(name, folder))
        {
            Player.CurrentPlaylist = name;
            PlaylistMessage = string.Empty;
        }
    }

    private void RemovePlaylist()
    {
        var current = Player.CurrentPlaylist;
        Player.RemovePlaylist(current);
        if (Player.Playlists.FirstOrDefault() is { } first)
        {
            Player.CurrentPlaylist = first.Name;
        }

        IsRenaming = false;
    }

    private void ChangeFolder()
    {
        var folder = _pickFolder();
        if (!string.IsNullOrWhiteSpace(folder) && Player.Playlists.Count > 0)
        {
            Player.SetPlaylistFolder(Player.CurrentPlaylist, folder);
        }
    }

    private void StartRename()
    {
        if (Player.Playlists.Count == 0)
        {
            return;
        }

        RenameText = Player.CurrentPlaylist;
        PlaylistMessage = string.Empty;
        IsRenaming = true;
    }

    private void CommitRename()
    {
        if (!IsRenaming)
        {
            return;
        }

        if (Player.RenamePlaylist(Player.CurrentPlaylist, RenameText))
        {
            PlaylistMessage = string.Empty;
            IsRenaming = false;
        }
        else
        {
            PlaylistMessage = string.IsNullOrWhiteSpace(RenameText)
                ? "Digite um nome."
                : "J\U000000E1 existe uma lista com esse nome.";
        }
    }

    // ---- following the world ----

    private AudioSettings Audio => _settings.Current.Audio;

    private AudioAppEntry? HolyricsApp() =>
        Audio.Apps.FirstOrDefault(app => string.Equals(app.Name, "Holyrics", StringComparison.OrdinalIgnoreCase));

    private static AudioSettings WithHolyrics(AudioSettings audio, Func<AudioAppEntry, AudioAppEntry> edit)
    {
        var found = audio.Apps.Any(app => string.Equals(app.Name, "Holyrics", StringComparison.OrdinalIgnoreCase));
        if (!found)
        {
            return audio with { Apps = [.. audio.Apps, edit(AudioSettings.DefaultApps()[0])] };
        }

        return audio with { Apps = audio.Apps.Select(app => string.Equals(app.Name, "Holyrics", StringComparison.OrdinalIgnoreCase) ? edit(app) : app).ToList() };
    }

    private static string AutoText(IReadOnlyList<string> words) =>
        $"Autom\U000000E1tico ({new DeviceMatchRule(words).Text})";

    private void QueueAudio(string key, Func<AudioSettings, AudioSettings> edit) =>
        _saver.Queue(key, s => s with { Audio = edit(s.Audio) });

    private List<FadeOption> FadeOptions(Func<int> current, Action<int> choose) =>
    [
        new FadeOption("5 s", 5, current, choose),
        new FadeOption("3 s", 3, current, choose),
        new FadeOption("seco", 0, current, choose),
    ];

    private void LoadSwitches()
    {
        _quiet = true;
        try
        {
            AdjustAtStart = Audio.Enabled;
            LockOutput = Audio.LockOutput;
            HolyricsRoute = Audio.HolyricsRoute;
        }
        finally
        {
            _quiet = false;
        }
    }

    private void OnSettingsChanged(AppSettings settings) => _post(RefreshAll);

    private void OnAudioChanged(AudioStatus status) => _post(RefreshAll);

    private void OnPlayerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PlayerViewModel.OutputText) or nameof(PlayerViewModel.OutputDevices) or nameof(PlayerViewModel.Alarms)
            or nameof(PlayerViewModel.Playlists) or nameof(PlayerViewModel.StopFadeSeconds) or nameof(PlayerViewModel.AlarmFadeSeconds)
            or nameof(PlayerViewModel.OutputDeviceName) or nameof(PlayerViewModel.TrackCountText) or nameof(PlayerViewModel.CurrentPlaylist))
        {
            RefreshAll();
        }
    }

    private void RefreshAll()
    {
        if (_disposed)
        {
            return;
        }

        LoadSwitches();
        if (!_saver.HasPending)
        {
            // A slider being dragged must not jump back to the value that is still in the file.
            MasterRow.Load();
            HolyricsRow.Load();
            PlayerRow.Load();
            SystemSoundsRow.Load();
        }

        foreach (var option in StopFadeOptions.Concat(AlarmFadeOptions))
        {
            option.Refresh();
        }

        var status = _audio.Status;
        var devices = status.Known ? status.Devices : Player.OutputDevices;
        DefaultOutput.Refresh(devices, DefaultOutputStatus(status));
        PlayerOutput.Refresh(devices, Player.OutputText);
        HolyricsOutput.Refresh(devices, status.Known ? status.HolyricsText ?? HolyricsOffText : string.Empty);
        AppliedLine = status.Known ? status.AppliedLine : string.Empty;
        PlaylistInfo = PlaylistInfoText();
        OnPropertyChanged(nameof(HasPlaylist));
        RefreshAlarms();
    }

    private static string HolyricsOffText => "Desligado: o Holyrics fica com a sa\U000000EDda que o Windows der a ele.";

    // "WING USB \U00002014 desconectado, aguardando" when the chosen output is not there; otherwise the current output.
    private string DefaultOutputStatus(AudioStatus status)
    {
        if (!status.Known)
        {
            return string.Empty;
        }

        var audio = Audio;
        if (!audio.LockOutput)
        {
            return $"Travamento desligado: o Windows escolhe a sa\U000000EDda. {status.OutputLine}";
        }

        if (!status.SelectedPresent)
        {
            var name = status.SelectedName ?? new DeviceMatchRule(audio.OutputMatch).Text;
            var current = status.CurrentOutputName is { } now ? $" Sa\U000000EDda atual do Windows: {now}." : string.Empty;
            return $"{name} \U00002014 desconectado, aguardando.{current}";
        }

        return status.OutputLine;
    }

    private string PlaylistInfoText()
    {
        var entry = _settings.Current.Player.FindPlaylist(Player.CurrentPlaylist);
        return entry is null ? string.Empty : $"{entry.Folder} \U000000B7 {Player.TrackCountText}";
    }

    private void RefreshAlarms()
    {
        var entries = Player.Alarms;
        for (var i = Alarms.Count - 1; i >= 0; i--)
        {
            if (entries.All(entry => entry.Id != Alarms[i].Id))
            {
                Alarms.RemoveAt(i);
            }
        }

        foreach (var entry in entries)
        {
            if (Alarms.FirstOrDefault(row => row.Id == entry.Id) is { } row)
            {
                row.Update(entry);
                row.PlaylistsChanged();
            }
            else
            {
                Alarms.Add(new AlarmRow(entry, Player));
            }
        }
    }
}
