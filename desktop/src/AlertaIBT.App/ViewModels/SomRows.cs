using System.Globalization;
using AlertaIBT.Core.Audio;
using AlertaIBT.Core.Services;
using AlertaIBT.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AlertaIBT.App.ViewModels;

/// <summary>
/// One line of a device list: an output of Windows, the remembered one that is unplugged, or "Automatico" (the name rule).
/// </summary>
/// <param name="Id">The endpoint id; <c>null</c> for the automatic choice.</param>
/// <param name="Name">The friendly name Windows shows; <c>null</c> for the automatic choice.</param>
/// <param name="Display">The text of the list.</param>
/// <param name="Present">Whether the device is active now.</param>
/// <param name="IsAuto">Whether this is the automatic choice (no device remembered: the name rule finds it).</param>
public sealed record DeviceChoice(string? Id, string? Name, string Display, bool Present, bool IsAuto = false)
{
    /// <summary>
    /// The list for one output row: the automatic choice, every active output, and (when it is not among them) the remembered one marked
    /// <c>desconectado</c>.
    /// </summary>
    /// <param name="active">The active outputs.</param>
    /// <param name="savedId">The remembered endpoint id, or <c>null</c>.</param>
    /// <param name="savedName">The remembered name, or <c>null</c>.</param>
    /// <param name="autoText">The text of the automatic choice, such as <c>Automatico (WING + 3-4)</c>.</param>
    public static IReadOnlyList<DeviceChoice> Build(IReadOnlyList<AudioDevice> active, string? savedId, string? savedName, string autoText)
    {
        ArgumentNullException.ThrowIfNull(active);

        var list = new List<DeviceChoice> { new(null, null, autoText, true, IsAuto: true) };
        list.AddRange(active.Select(d => new DeviceChoice(d.Id, d.Name, d.Name, true)));
        if (Remembered(active, savedId, savedName) is { } gone)
        {
            list.Add(gone);
        }

        return list;
    }

    /// <summary>The choice that matches what is remembered: the automatic one when nothing is, the device when it is active (by id, then by name), else the unplugged one.</summary>
    public static DeviceChoice Selected(IReadOnlyList<DeviceChoice> choices, IReadOnlyList<AudioDevice> active, string? savedId, string? savedName)
    {
        ArgumentNullException.ThrowIfNull(choices);
        ArgumentNullException.ThrowIfNull(active);

        if (string.IsNullOrWhiteSpace(savedId) && string.IsNullOrWhiteSpace(savedName))
        {
            return choices[0];
        }

        var device = AudioNames.Resolve(active, savedId, savedName);
        return choices.FirstOrDefault(c => device is not null && string.Equals(c.Id, device.Id, StringComparison.OrdinalIgnoreCase))
            ?? choices.FirstOrDefault(c => !c.Present && !c.IsAuto)
            ?? choices[0];
    }

    private static DeviceChoice? Remembered(IReadOnlyList<AudioDevice> active, string? savedId, string? savedName)
    {
        if (string.IsNullOrWhiteSpace(savedId) && string.IsNullOrWhiteSpace(savedName))
        {
            return null;
        }

        if (AudioNames.Resolve(active, savedId, savedName) is not null)
        {
            return null;
        }

        var name = string.IsNullOrWhiteSpace(savedName) ? savedId! : savedName;
        return new DeviceChoice(savedId, savedName, name + " (desconectado)", false);
    }
}

/// <summary>
/// One row of "Saidas": a list of outputs, the choice made and the line of status under it. The list and the choice follow the settings
/// and the devices of Windows; choosing one writes what the owner picked (id, name and the name rule) through <paramref name="apply"/>.
/// </summary>
public sealed partial class OutputRow : ObservableObject
{
    private readonly Func<(string? Id, string? Name)> _saved;
    private readonly Action<DeviceChoice> _apply;
    private readonly Func<string> _autoText;
    private bool _quiet;

    [ObservableProperty]
    private IReadOnlyList<DeviceChoice> _choices = [];

    [ObservableProperty]
    private DeviceChoice? _selected;

    [ObservableProperty]
    private string _status = string.Empty;

    [ObservableProperty]
    private StatusLight _light = StatusLight.Gray;

    /// <param name="saved">The remembered output (id and name) in the settings.</param>
    /// <param name="autoText">The text of the automatic choice.</param>
    /// <param name="apply">Writes a choice into the settings.</param>
    public OutputRow(Func<(string? Id, string? Name)> saved, Func<string> autoText, Action<DeviceChoice> apply)
    {
        _saved = saved;
        _autoText = autoText;
        _apply = apply;
    }

    /// <summary>Whether the status line has text.</summary>
    public bool HasStatus => Status.Length > 0;

    /// <summary>
    /// Rebuilds the list and the choice from the settings and the active outputs. <paramref name="light"/> is the dot beside the list: green when the
    /// output is active, yellow while the app waits for the device (the WING is off), gray when the rule is off.
    /// </summary>
    public void Refresh(IReadOnlyList<AudioDevice> active, string status, StatusLight light = StatusLight.Gray)
    {
        Light = light;
        var (id, name) = _saved();
        var choices = DeviceChoice.Build(active, id, name, _autoText());
        _quiet = true;
        try
        {
            if (!choices.SequenceEqual(Choices))
            {
                Choices = choices;
            }

            var selected = DeviceChoice.Selected(choices, active, id, name);
            if (!Equals(Selected, selected))
            {
                Selected = selected;
            }

            Status = status;
        }
        finally
        {
            _quiet = false;
        }
    }

    partial void OnSelectedChanged(DeviceChoice? value)
    {
        if (!_quiet && value is not null)
        {
            _apply(value);
        }
    }

    partial void OnStatusChanged(string value) => OnPropertyChanged(nameof(HasStatus));
}

/// <summary>One row of "Volumes": a slider from 0 to 100, the number box and "Com som | Mudo", each writing through its own delegate.</summary>
public sealed partial class VolumeRow : ObservableObject
{
    private readonly Func<int> _readVolume;
    private readonly Func<bool> _readMuted;
    private readonly Action<int> _writeVolume;
    private readonly Action<bool> _writeMuted;
    private bool _quiet;

    [ObservableProperty]
    private double _volume;

    [ObservableProperty]
    private bool _muted;

    /// <param name="readVolume">The volume in the settings.</param>
    /// <param name="readMuted">The mute in the settings.</param>
    /// <param name="writeVolume">Saves a volume (0 to 100).</param>
    /// <param name="writeMuted">Saves the mute.</param>
    public VolumeRow(Func<int> readVolume, Func<bool> readMuted, Action<int> writeVolume, Action<bool> writeMuted)
    {
        _readVolume = readVolume;
        _readMuted = readMuted;
        _writeVolume = writeVolume;
        _writeMuted = writeMuted;
        Box = new IntegerField(
            AudioSettings.MinVolume,
            AudioSettings.MaxVolume,
            allowBlank: false,
            value =>
            {
                var number = value ?? 0;
                _quiet = true;
                try
                {
                    Volume = number;
                }
                finally
                {
                    _quiet = false;
                }

                _writeVolume(number);
            },
            () => { });
        Load();
    }

    /// <summary>The short name of the row (<c>Windows</c>, <c>Holyrics</c>, <c>Player</c>, <c>Sons do sistema</c>).</summary>
    public string Label { get; init; } = string.Empty;

    /// <summary>The resource key of the Lucide icon of the row.</summary>
    public string IconKey { get; init; } = "LucideVolume2";

    /// <summary>What the row is, for the tooltip (the long explanation that used to be written under the title).</summary>
    public string Tip { get; init; } = string.Empty;

    /// <summary>The number box (the same value as the slider).</summary>
    public IntegerField Box { get; }

    /// <summary>"Com som": the opposite of <see cref="Muted"/>, for the left half of the two-way switch.</summary>
    public bool WithSound
    {
        get => !Muted;
        set
        {
            if (value)
            {
                Muted = false;
            }
        }
    }

    /// <summary>"Mudo": the right half of the switch.</summary>
    public bool IsMuted
    {
        get => Muted;
        set
        {
            if (value)
            {
                Muted = true;
            }
        }
    }

    /// <summary>The settings changed somewhere else: the row follows without writing anything back.</summary>
    public void Load()
    {
        var volume = Math.Clamp(_readVolume(), AudioSettings.MinVolume, AudioSettings.MaxVolume);
        var muted = _readMuted();
        _quiet = true;
        try
        {
            if ((int)Math.Round(Volume) != volume || Box.Accepted != volume)
            {
                Volume = volume;
                Box.Set(volume);
            }

            if (Muted != muted)
            {
                Muted = muted;
            }
        }
        finally
        {
            _quiet = false;
        }
    }

    partial void OnVolumeChanged(double value)
    {
        if (_quiet)
        {
            return;
        }

        var number = (int)Math.Round(Math.Clamp(value, AudioSettings.MinVolume, AudioSettings.MaxVolume));
        _quiet = true;
        try
        {
            Box.Set(number);
        }
        finally
        {
            _quiet = false;
        }

        _writeVolume(number);
    }

    partial void OnMutedChanged(bool value)
    {
        OnPropertyChanged(nameof(WithSound));
        OnPropertyChanged(nameof(IsMuted));
        if (!_quiet)
        {
            _writeMuted(value);
        }
    }
}

/// <summary>One of the three choices of a fade ("5 s", "3 s", "seco"): selected when it is the length in the settings.</summary>
public sealed class FadeOption : ObservableObject
{
    private readonly Func<int> _current;
    private readonly Action<int> _choose;

    /// <param name="label">The text on the button.</param>
    /// <param name="seconds">The length of the fade (0 is dry).</param>
    /// <param name="current">The length in the settings.</param>
    /// <param name="choose">Saves a length.</param>
    public FadeOption(string label, int seconds, Func<int> current, Action<int> choose)
    {
        Label = label;
        Seconds = seconds;
        _current = current;
        _choose = choose;
    }

    /// <summary>The text on the button.</summary>
    public string Label { get; }

    /// <summary>The length of the fade in seconds.</summary>
    public int Seconds { get; }

    /// <summary>Whether this is the chosen length. Setting false does nothing: choosing another one does it.</summary>
    public bool IsSelected
    {
        get => _current() == Seconds;
        set
        {
            if (value && _current() != Seconds)
            {
                _choose(Seconds);
            }
        }
    }

    /// <summary>The settings changed: the button looks again.</summary>
    public void Refresh() => OnPropertyChanged(nameof(IsSelected));
}

/// <summary>One weekday chip of an alarm (<c>D S T Q Q S S</c>).</summary>
public sealed class DayChip : ObservableObject
{
    private readonly Func<AlarmEntry> _entry;
    private readonly Action<AlarmEntry> _update;

    /// <param name="day">The weekday.</param>
    /// <param name="letter">The letter on the chip.</param>
    /// <param name="name">The full name, for the tooltip.</param>
    /// <param name="entry">The alarm as it is now.</param>
    /// <param name="update">Saves a changed alarm.</param>
    public DayChip(DayOfWeek day, string letter, string name, Func<AlarmEntry> entry, Action<AlarmEntry> update)
    {
        Day = day;
        Letter = letter;
        Name = name;
        _entry = entry;
        _update = update;
    }

    /// <summary>The weekday.</summary>
    public DayOfWeek Day { get; }

    /// <summary>The letter on the chip.</summary>
    public string Letter { get; }

    /// <summary>The full name of the day.</summary>
    public string Name { get; }

    /// <summary>Whether the alarm fires on this day.</summary>
    public bool IsOn
    {
        get => _entry().HasDay(Day);
        set
        {
            var entry = _entry();
            if (entry.HasDay(Day) != value)
            {
                _update(entry.WithDay(Day, value));
            }
        }
    }

    /// <summary>The alarm changed: the chip looks again.</summary>
    public void Refresh() => OnPropertyChanged(nameof(IsOn));
}

/// <summary>
/// One alarm of "Despertadores": the switch, the start time, the weekdays, "nao iniciar depois de" and the playlist. A time is saved
/// only when it is a time of day (<c>HH:mm</c>); what is half typed stays in the box until it is complete, and goes back to the saved
/// time when the box loses the focus.
/// </summary>
public sealed partial class AlarmRow : ObservableObject
{
    private readonly PlayerViewModel _player;
    private readonly Func<string, string, bool>? _confirm;
    private AlarmEntry _entry;
    private string _startText;
    private string _latestText;

    /// <param name="entry">The alarm.</param>
    /// <param name="player">Where changes go.</param>
    /// <param name="confirm">Asks "title, message" and answers whether the owner said yes; null removes without asking (tests).</param>
    public AlarmRow(AlarmEntry entry, PlayerViewModel player, Func<string, string, bool>? confirm = null)
    {
        _entry = entry;
        _player = player;
        _confirm = confirm;
        _startText = entry.Start;
        _latestText = entry.Latest;
        string[] letters = ["D", "S", "T", "Q", "Q", "S", "S"];
        string[] names = ["Domingo", "Segunda", "Ter\U000000E7a", "Quarta", "Quinta", "Sexta", "S\U000000E1bado"];
        Days = Enumerable.Range(0, 7)
            .Select(i => new DayChip((DayOfWeek)i, letters[i], names[i], () => _entry, Save))
            .ToList();
        RemoveCommand = new RelayCommand(Remove);
    }

    /// <summary>Identifies the alarm.</summary>
    public string Id => _entry.Id;

    /// <summary>The weekday chips, Sunday first.</summary>
    public IReadOnlyList<DayChip> Days { get; }

    /// <summary>Removes the alarm.</summary>
    public IRelayCommand RemoveCommand { get; }

    /// <summary>The names of the playlists the alarm can play.</summary>
    public IReadOnlyList<string> PlaylistNames => _player.Playlists.Select(p => p.Name).ToList();

    /// <summary>Whether the alarm can fire.</summary>
    public bool Enabled
    {
        get => _entry.Enabled;
        set
        {
            if (_entry.Enabled != value)
            {
                Save(_entry with { Enabled = value });
                OnPropertyChanged(); // the block dims at once (the alarm that is off is quieter)
            }
        }
    }

    /// <summary>The start time as typed.</summary>
    public string StartText
    {
        get => _startText;
        set
        {
            if (_startText == value)
            {
                return;
            }

            _startText = value;
            OnPropertyChanged();
            if (PlayerSettings.ParseTime(value) is { } time && Format(time) != _entry.Start)
            {
                Save(_entry with { Start = Format(time) });
            }
        }
    }

    /// <summary>"Nao iniciar depois de", as typed.</summary>
    public string LatestText
    {
        get => _latestText;
        set
        {
            if (_latestText == value)
            {
                return;
            }

            _latestText = value;
            OnPropertyChanged();
            if (PlayerSettings.ParseTime(value) is { } time && Format(time) != _entry.Latest)
            {
                Save(_entry with { Latest = Format(time) });
            }
        }
    }

    /// <summary>The playlist the alarm plays.</summary>
    public string Playlist
    {
        get => _entry.Playlist;
        set
        {
            if (!string.IsNullOrEmpty(value) && _entry.Playlist != value)
            {
                Save(_entry with { Playlist = value });
            }
        }
    }

    /// <summary>The start box lost the focus: it shows the time that was kept, never what could not be used.</summary>
    public void NormalizeStart() => StartText = _entry.Start;

    /// <summary>The "nao iniciar depois de" box lost the focus.</summary>
    public void NormalizeLatest() => LatestText = _entry.Latest;

    /// <summary>The alarm in the settings changed (or the playlists did): the row follows.</summary>
    public void Update(AlarmEntry entry)
    {
        var before = _entry;
        _entry = entry;
        if (before.Enabled != entry.Enabled)
        {
            OnPropertyChanged(nameof(Enabled));
        }

        if (before.Start != entry.Start)
        {
            _startText = entry.Start;
            OnPropertyChanged(nameof(StartText));
        }

        if (before.Latest != entry.Latest)
        {
            _latestText = entry.Latest;
            OnPropertyChanged(nameof(LatestText));
        }

        if (before.Playlist != entry.Playlist)
        {
            OnPropertyChanged(nameof(Playlist));
        }

        foreach (var day in Days)
        {
            day.Refresh();
        }
    }

    /// <summary>The list of playlists changed.</summary>
    public void PlaylistsChanged()
    {
        OnPropertyChanged(nameof(PlaylistNames));
        OnPropertyChanged(nameof(Playlist));
    }

    private static string Format(TimeOnly time) => time.ToString("HH:mm", CultureInfo.InvariantCulture);

    // The trash asks first: a removed alarm cannot be brought back.
    private void Remove()
    {
        if (_confirm is null
            || _confirm("Remover este despertador?", $"O despertador das {_entry.Start} deixa de existir. Nenhuma m\U000000FAsica \U000000E9 apagada."))
        {
            _player.RemoveAlarm(_entry.Id);
        }
    }

    private void Save(AlarmEntry entry)
    {
        _entry = entry;
        _player.UpdateAlarm(entry);
        foreach (var day in Days)
        {
            day.Refresh();
        }
    }
}

/// <summary>
/// The dashed "+" block at the end of the alarm grid. It is an item of <see cref="SomViewModel.AlarmTiles"/> next to the alarms, so the grid lays
/// it out like one; it shows a word only while there is no alarm (an empty grid still says what the block is for).
/// </summary>
public sealed partial class AddAlarmTile : ObservableObject
{
    [ObservableProperty]
    private bool _showLabel;

    /// <param name="add">Adds an alarm.</param>
    public AddAlarmTile(IRelayCommand add) => AddCommand = add;

    /// <summary>Adds an alarm (18:00, not after 18:25, Sunday only).</summary>
    public IRelayCommand AddCommand { get; }
}
