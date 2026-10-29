using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using AlertaIBT.Core.Audio;
using AlertaIBT.Core.Services;
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
    private readonly Func<string, string, bool>? _confirm;
    private readonly Func<string, string, Func<string, string?>, string?>? _askName;
    private bool _quiet;
    private bool _disposed;
    private readonly AddAlarmTile _addTile;

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
    private string _playlistInfo = string.Empty;

    [ObservableProperty]
    private string _playlistFolder = string.Empty;

    /// <param name="settings">The settings.</param>
    /// <param name="saver">The autosave (the "Salvo" stamp of the tab).</param>
    /// <param name="audio">The sound rules: the status of the outputs and "Aplicar agora".</param>
    /// <param name="player">The music player.</param>
    /// <param name="pickFolder">Asks the user for a folder (the playlist's); <c>null</c> when cancelled.</param>
    /// <param name="post">Runs an action on the UI thread; runs it at once when null (tests).</param>
    /// <param name="logger">Receives failures.</param>
    /// <param name="confirm">Asks "title, message" in a window and answers whether the owner said yes (Sim); null never asks, it acts (tests).</param>
    /// <param name="askName">Asks for a name in a small window: "title, current name, check" where the check gives the problem with a name or null; answers the name, or null when cancelled.</param>
    public SomViewModel(
        SettingsService settings,
        Autosaver saver,
        AudioController audio,
        PlayerViewModel player,
        Func<string?> pickFolder,
        Action<Action>? post = null,
        ILogger? logger = null,
        Func<string, string, bool>? confirm = null,
        Func<string, string, Func<string, string?>, string?>? askName = null)
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
        _confirm = confirm;
        _askName = askName;

        MasterRow = new VolumeRow(
            () => Audio.MasterVolume,
            () => Audio.MasterMuted,
            volume => QueueAudio("audio.masterVolume", a => a with { MasterVolume = volume }),
            muted => QueueAudio("audio.masterMuted", a => a with { MasterMuted = muted }))
        {
            Label = "Windows",
            IconKey = "LucideMonitor",
            Tip = "Volume geral do Windows: o volume principal da sa\U000000EDda do Windows (o controle do alto-falante na barra de tarefas).",
        };
        HolyricsRow = new VolumeRow(
            () => HolyricsApp()?.Volume ?? 100,
            () => HolyricsApp()?.Muted ?? false,
            volume => QueueAudio("audio.holyricsVolume", a => WithHolyrics(a, e => e with { Volume = volume })),
            muted => QueueAudio("audio.holyricsMuted", a => WithHolyrics(a, e => e with { Muted = muted })))
        {
            Label = "Holyrics",
            IconKey = "LucidePresentation",
            Tip = "Holyrics: o volume do Holyrics (o som dele vem do javaw). Vale a cada vez que o Holyrics abre.",
        };
        PlayerRow = new VolumeRow(
            () => _settings.Current.Player.Volume,
            () => _settings.Current.Player.StartMuted,
            volume => _saver.Queue("player.volume", s => s with { Player = s.Player with { Volume = volume } }),
            muted => _saver.Queue("player.startMuted", s => s with { Player = s.Player with { StartMuted = muted } }))
        {
            Label = "Player",
            IconKey = "LucideMusic",
            Tip = "Player: o volume do player de m\U000000FAsica do app. Mudo deixa o player calado. \U000000C9 o mesmo controle do volume ao lado do play.",
        };
        SystemSoundsRow = new VolumeRow(
            () => Audio.SystemSoundsVolume,
            () => Audio.SystemSoundsMuted,
            volume => QueueAudio("audio.systemSoundsVolume", a => a with { SystemSoundsVolume = volume }),
            muted => QueueAudio("audio.systemSoundsMuted", a => a with { SystemSoundsMuted = muted }))
        {
            Label = "Sons do sistema",
            IconKey = "LucideBell",
            Tip = "Sons do sistema: os avisos sonoros do Windows (o item Sons do sistema do misturador de volume). Em 0, eles n\U000000E3o tocam no tel\U000000E3o.",
        };
        VolumeRows = [MasterRow, HolyricsRow, PlayerRow, SystemSoundsRow];

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
        RenamePlaylistCommand = new RelayCommand(RenamePlaylist);
        AddAlarmCommand = new RelayCommand(() => Player.AddAlarm());
        _addTile = new AddAlarmTile(AddAlarmCommand);
        AlarmTiles = [_addTile];

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

    /// <summary>The four volume rows, in the order of the card: Windows, Holyrics, Player, Sons do sistema.</summary>
    public IReadOnlyList<VolumeRow> VolumeRows { get; }

    /// <summary>The alarms.</summary>
    public ObservableCollection<AlarmRow> Alarms { get; } = [];

    /// <summary>What the alarm grid shows: the alarms, then the dashed plus block.</summary>
    public ObservableCollection<object> AlarmTiles { get; }

    /// <summary>The next-alarm line of the card header, short (proximo: quarta 19:30).</summary>
    public string NextAlarmShort
    {
        get
        {
            const string Prefix = "Pr\U000000F3ximo despertador: ";
            var text = Player.NextAlarmText;
            return text.StartsWith(Prefix, StringComparison.Ordinal) ? "pr\U000000F3ximo: " + text[Prefix.Length..] : text;
        }
    }

    /// <summary>The dot of the "ajustar o som ao iniciar" master: green when on, gray when off.</summary>
    public StatusLight AdjustLight => AdjustAtStart ? StatusLight.Green : StatusLight.Gray;

    /// <summary>The tooltip of the dot: whether the adjusting is on, and what was applied last.</summary>
    public string AdjustStatusTip =>
        (AdjustAtStart ? "Ajuste autom\U000000E1tico ligado." : "Ajuste autom\U000000E1tico desligado: o app n\U000000E3o mexe em nenhum volume.")
        + (HasAppliedLine ? "\n" + AppliedLine : string.Empty);

    /// <summary>The tooltip of the master switch.</summary>
    public string AdjustSwitchTip => AdjustAtStart
        ? "Ajustar o som ao iniciar: ligado.\nQuando o PC liga e a cada vez que um programa abre o som dele, o app p\U000000F5e o volume que est\U000000E1 nesta lista."
        : "Ajustar o som ao iniciar: desligado.\nO app n\U000000E3o mexe em nenhum volume (s\U000000F3 em Aplicar agora).";

    /// <summary>The tooltip of "Aplicar agora": what it does, then when it was last done and what waits.</summary>
    public string ApplyTip =>
        "Aplicar agora: p\U000000F5e todos os volumes da lista agora, nos programas que j\U000000E1 est\U000000E3o tocando, e a sa\U000000EDda do Holyrics."
        + (HasAppliedLine ? "\n" + AppliedLine : string.Empty);

    /// <summary>"Aplicar agora": sets every volume (and the Holyrics output) now.</summary>
    public IAsyncRelayCommand ApplyNowCommand { get; }

    /// <summary>Adds a playlist: asks for a folder and names it after the folder.</summary>
    public IRelayCommand AddPlaylistCommand { get; }

    /// <summary>Removes the selected playlist (the music files stay where they are).</summary>
    public IRelayCommand RemovePlaylistCommand { get; }

    /// <summary>Changes the folder of the selected playlist.</summary>
    public IRelayCommand ChangeFolderCommand { get; }

    /// <summary>Asks for a new name for the selected playlist in a small window and renames it (alarms that play it follow).</summary>
    public IRelayCommand RenamePlaylistCommand { get; }

    /// <summary>Adds an alarm (18:00, not after 18:25, Sunday only).</summary>
    public IRelayCommand AddAlarmCommand { get; }

    /// <summary>Whether the line under "Aplicar agora" has text.</summary>
    public bool HasAppliedLine => AppliedLine.Length > 0;

    /// <summary>Whether there is a playlist to remove, rename or change.</summary>
    public bool HasPlaylist => Player.Playlists.Count > 0;

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
        OnPropertyChanged(nameof(AdjustLight));
        OnPropertyChanged(nameof(AdjustStatusTip));
        OnPropertyChanged(nameof(AdjustSwitchTip));
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
            RefreshAll(); // the dot beside the output follows at once
        }
    }

    partial void OnHolyricsRouteChanged(bool value)
    {
        if (!_quiet)
        {
            QueueAudio("audio.holyricsRoute", a => a with { HolyricsRoute = value });
            _saver.FlushNow();
            RefreshAll();
        }
    }

    partial void OnAppliedLineChanged(string value)
    {
        OnPropertyChanged(nameof(HasAppliedLine));
        OnPropertyChanged(nameof(AdjustStatusTip));
        OnPropertyChanged(nameof(ApplyTip));
    }

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
        }
    }

    // The trash asks first (a window, Sim / Nao): only the list leaves the app, the music files are never touched.
    private void RemovePlaylist()
    {
        var current = Player.CurrentPlaylist;
        if (Player.Playlists.Count == 0
            || (_confirm is not null && !_confirm(
                $"Excluir a lista \U0000201C{current}\U0000201D?",
                "S\U000000F3 a lista sai do app. As m\U000000FAsicas continuam na pasta: nada \U000000E9 apagado do PC.")))
        {
            return;
        }

        Player.RemovePlaylist(current);
        if (Player.Playlists.FirstOrDefault() is { } first)
        {
            Player.CurrentPlaylist = first.Name;
        }
    }

    private void ChangeFolder()
    {
        var folder = _pickFolder();
        if (!string.IsNullOrWhiteSpace(folder) && Player.Playlists.Count > 0)
        {
            Player.SetPlaylistFolder(Player.CurrentPlaylist, folder);
        }
    }

    // The pencil: a small window asks for the name (and shows the problem with it, so a taken name never gets through).
    private void RenamePlaylist()
    {
        if (Player.Playlists.Count == 0 || _askName is null)
        {
            return;
        }

        var current = Player.CurrentPlaylist;
        var name = _askName("Renomear a lista", current, text => PlaylistNameProblem(text, current));
        if (name is not null)
        {
            Player.RenamePlaylist(current, name);
        }
    }

    /// <summary>What is wrong with <paramref name="text"/> as the new name of <paramref name="current"/>, or null when it can be used.</summary>
    public string? PlaylistNameProblem(string text, string current)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "Digite um nome.";
        }

        var other = _settings.Current.Player.FindPlaylist(text.Trim());
        return other is not null && !string.Equals(other.Name, current, StringComparison.OrdinalIgnoreCase)
            ? "J\U000000E1 existe uma lista com esse nome."
            : null;
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
        if (e.PropertyName is nameof(PlayerViewModel.NextAlarmText))
        {
            OnPropertyChanged(nameof(NextAlarmShort));
        }

        if (e.PropertyName is nameof(PlayerViewModel.OutputText) or nameof(PlayerViewModel.OutputKind) or nameof(PlayerViewModel.IsEnabled) or nameof(PlayerViewModel.OutputDevices) or nameof(PlayerViewModel.Alarms)
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
        DefaultOutput.Refresh(devices, DefaultOutputStatus(status), DefaultLight(status));
        PlayerOutput.Refresh(devices, Player.OutputText, PlayerLight());
        HolyricsOutput.Refresh(devices, status.Known ? status.HolyricsText ?? HolyricsOffText : string.Empty, Audio.HolyricsRoute ? HolyricsLight(status) : StatusLight.Gray);
        AppliedLine = status.Known ? status.AppliedLine : string.Empty;
        PlaylistInfo = PlaylistInfoText();
        PlaylistFolder = _settings.Current.Player.FindPlaylist(Player.CurrentPlaylist)?.Folder ?? string.Empty;
        OnPropertyChanged(nameof(NextAlarmShort));
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

    // The dot of the default output: gray when the lock is off, green when the chosen output is there, yellow while the app waits for it.
    private StatusLight DefaultLight(AudioStatus status)
    {
        if (!status.Known || !Audio.LockOutput)
        {
            return StatusLight.Gray;
        }

        return status.SelectedPresent ? StatusLight.Green : StatusLight.Yellow;
    }

    // The player: green when its own output is in use, yellow when the Windows default stands in for a device that is not there, gray when it is off.
    private StatusLight PlayerLight()
    {
        if (!Player.IsEnabled)
        {
            return StatusLight.Gray;
        }

        return Player.OutputKind switch
        {
            OutputChoiceKind.ById or OutputChoiceKind.ByRule => StatusLight.Green,
            OutputChoiceKind.Default => StatusLight.Yellow,
            _ => StatusLight.Gray,
        };
    }

    // Holyrics: gray when the Holyrics output switch is off, yellow while the device is absent, red when Windows refused, green otherwise.
    internal static StatusLight HolyricsLight(AudioStatus status)
    {
        if (!status.Known || status.HolyricsText is not { } text)
        {
            return StatusLight.Gray;
        }

        if (text.Contains("desconectado", StringComparison.OrdinalIgnoreCase))
        {
            return StatusLight.Yellow;
        }

        return text.Contains("sem acesso", StringComparison.OrdinalIgnoreCase) || text.Contains("n\U000000E3o foi poss", StringComparison.OrdinalIgnoreCase)
            ? StatusLight.Red
            : StatusLight.Green;
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
                AlarmTiles.Remove(Alarms[i]);
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
                var created = new AlarmRow(entry, Player, _confirm);
                Alarms.Add(created);
                AlarmTiles.Insert(AlarmTiles.Count - 1, created); // before the dashed plus block
            }
        }

        _addTile.ShowLabel = Alarms.Count == 0;
    }
}
