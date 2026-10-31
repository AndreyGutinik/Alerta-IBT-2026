using System.Collections.ObjectModel;
using System.Globalization;
using System.Net;
using AlertaIBT.Core.Control;
using AlertaIBT.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AlertaIBT.App.ViewModels;

/// <summary>A mode in the pick list of a row.</summary>
/// <param name="Mode">The mode.</param>
/// <param name="Label">The short word shown.</param>
/// <param name="Tip">What it does.</param>
public sealed record ModeChoice(ControlMode Mode, string Label, string Tip)
{
    /// <summary>The word and the tooltip of a mode.</summary>
    public static ModeChoice For(ControlMode mode) => mode switch
    {
        ControlMode.Button => new(mode, "Bot\U000000E3o", "Bot\U000000E3o: o canal liga e desliga como um bot\U000000E3o e a a\U000000E7\U000000E3o roda uma vez. Valor 128 ou mais \U000000E9 ligado."),
        ControlMode.Select => new(mode, "Sele\U000000E7\U000000E3o", "Sele\U000000E7\U000000E3o: ao ligar o canal, escolhe a c\U000000E2mera na pr\U000000E9via."),
        ControlMode.State => new(mode, "Estado", "Estado: o app acompanha o canal. Ligado liga, desligado desliga."),
        _ => new(mode, "Fader", "Fader: 0 a 255 vira 0 a 100 %."),
    };
}

/// <summary>A trigger in the pick list of a button row.</summary>
/// <param name="Trigger">The trigger.</param>
/// <param name="Label">The words shown.</param>
public sealed record TriggerChoice(ControlTrigger Trigger, string Label)
{
    /// <summary>Both choices.</summary>
    public static IReadOnlyList<TriggerChoice> All { get; } =
    [
        new(ControlTrigger.AnyChange, "Qualquer mudan\U000000E7a"),
        new(ControlTrigger.RisingOnly, "S\U000000F3 na subida"),
    ];
}

/// <summary>One line of the map as the screen shows it: the editable parts, and the live level that comes from the console.</summary>
public sealed partial class ControlRowViewModel : ObservableObject
{
    private readonly ControlViewModel _owner;
    private bool _syncing;

    [ObservableProperty]
    private byte _level;

    [ObservableProperty]
    private bool _isOn;

    [ObservableProperty]
    private FaderPickup _pickupState;

    [ObservableProperty]
    private bool _isDuplicate;

    /// <param name="owner">The tab, which saves the changes.</param>
    /// <param name="row">The row as saved.</param>
    public ControlRowViewModel(ControlViewModel owner, ControlRow row)
    {
        _owner = owner;
        Row = row;
        ChannelBox = new IntegerField(1, ControlSettings.ChannelCount, allowBlank: false, value => Write(r => r with { Channel = value ?? r.Channel }), () => { });
        ChannelBox.Set(row.Channel);
        TestCommand = new RelayCommand(() => _owner.Test(Row), () => CanTest);
        RemoveCommand = new RelayCommand(() => _owner.Remove(this));
    }

    /// <summary>The row as saved (a new copy replaces it on every edit).</summary>
    public ControlRow Row { get; private set; }

    /// <summary>The channel box (1 to 512).</summary>
    public IntegerField ChannelBox { get; }

    /// <summary>"Testar": runs the action once.</summary>
    public IRelayCommand TestCommand { get; }

    /// <summary>Takes the row out of the map.</summary>
    public IRelayCommand RemoveCommand { get; }

    /// <summary>Every action, for the pick list.</summary>
    public IReadOnlyList<ControlActionInfo> Actions => ControlCatalog.All;

    /// <summary>The triggers, for the pick list.</summary>
    public IReadOnlyList<TriggerChoice> Triggers => TriggerChoice.All;

    /// <summary>The cameras 1 to 6.</summary>
    public IReadOnlyList<int> Cameras { get; } = [1, 2, 3, 4, 5, 6];

    /// <summary>What the catalog knows about the action of this row.</summary>
    public ControlActionInfo Info => ControlCatalog.Get(Row.Action);

    /// <summary>The modes the action accepts.</summary>
    public IReadOnlyList<ModeChoice> Modes => Info.Modes.Select(ModeChoice.For).ToList();

    /// <summary>The icon of the action (a resource key).</summary>
    public string IconKey => Info.IconKey;

    /// <summary>The action (changing it also fixes the mode).</summary>
    public ControlActionInfo SelectedAction
    {
        get => Info;
        set
        {
            if (value is null || _syncing || value.Action == Row.Action)
            {
                return;
            }

            Write(r => r with
            {
                Action = value.Action,
                Mode = value.Modes.Contains(r.Mode) ? r.Mode : value.DefaultMode,
                Parameter = value.Action == ControlAction.SelectCamera ? Math.Max(1, r.Parameter) : 0,
                Enabled = value.Available && r.Enabled,
            });
        }
    }

    /// <summary>The mode.</summary>
    public ModeChoice SelectedMode
    {
        get => ModeChoice.For(Row.Mode);
        set
        {
            if (value is not null && !_syncing && value.Mode != Row.Mode)
            {
                Write(r => r with { Mode = value.Mode });
            }
        }
    }

    /// <summary>The trigger of a button.</summary>
    public TriggerChoice SelectedTrigger
    {
        get => TriggerChoice.All.First(t => t.Trigger == Row.Trigger);
        set
        {
            if (value is not null && !_syncing && value.Trigger != Row.Trigger)
            {
                Write(r => r with { Trigger = value.Trigger });
            }
        }
    }

    /// <summary>The camera of a Select row.</summary>
    public int Camera
    {
        get => Math.Max(1, Row.Parameter);
        set
        {
            if (!_syncing && value != Row.Parameter)
            {
                Write(r => r with { Parameter = value });
            }
        }
    }

    /// <summary>Whether the pickup of a fader is on.</summary>
    public bool PickupOn
    {
        get => Row.Pickup;
        set
        {
            if (!_syncing && value != Row.Pickup)
            {
                Write(r => r with { Pickup = value });
            }
        }
    }

    /// <summary>Whether the row is active.</summary>
    public bool Enabled
    {
        get => Row.Enabled;
        set
        {
            if (!_syncing && value != Row.Enabled && (Info.Available || !value))
            {
                Write(r => r with { Enabled = value });
            }
        }
    }

    /// <summary>The trigger pick list is shown for a button.</summary>
    public bool ShowTrigger => Row.Mode == ControlMode.Button && Info.Available;

    /// <summary>The camera pick list is shown for a Select row.</summary>
    public bool ShowCamera => Row.Action == ControlAction.SelectCamera && Info.Available;

    /// <summary>The pickup toggle is shown for a fader.</summary>
    public bool ShowPickup => Row.Mode == ControlMode.Fader;

    /// <summary>The action has no engine yet (Holyrics): "em breve".</summary>
    public bool IsSoon => !Info.Available;

    /// <summary>The level bar is for a fader (a continuous value) and not for a button.</summary>
    public bool IsFader => Row.Mode == ControlMode.Fader;

    /// <summary>Whether "Testar" can run (not for a fader, nor for a row that is not available).</summary>
    public bool CanTest => Info.Available && Row.Mode != ControlMode.Fader;

    /// <summary>The level as a number (0 to 255).</summary>
    public string LevelText => Level.ToString(CultureInfo.InvariantCulture);

    /// <summary>The words of the pickup state, for the tooltip.</summary>
    public string PickupTip => PickupState switch
    {
        FaderPickup.Following => "O fader assumiu o controle: o app acompanha.",
        FaderPickup.Waiting => "Esperando o fader chegar no valor atual do app (assim o valor nunca d\U000000E1 um salto).",
        _ => "Pickup desligado: o fader controla assim que mexe.",
    };

    /// <summary>The row of the map was changed from outside (the settings): show it without writing it back.</summary>
    public void Sync(ControlRow row)
    {
        _syncing = true;
        try
        {
            Row = row;
            ChannelBox.Set(row.Channel);
            OnPropertyChanged(string.Empty);
            TestCommand.NotifyCanExecuteChanged();
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>Takes the live reading of the channel (about 15 times a second while the tab is visible).</summary>
    public void Apply(ControlRowState state)
    {
        Level = state.Level;
        IsOn = state.On;
        PickupState = state.Pickup;
    }

    partial void OnLevelChanged(byte value) => OnPropertyChanged(nameof(LevelText));

    partial void OnPickupStateChanged(FaderPickup value) => OnPropertyChanged(nameof(PickupTip));

    private void Write(Func<ControlRow, ControlRow> change) => _owner.Edit(this, change);
}

/// <summary>
/// The "Controle" tab: the master switch of the control by the grandMA2, the signal, the universe and the source filter, and the map of channels to
/// actions with the live level of each channel. Everything is saved through <see cref="SettingsService"/>; the live numbers are read from the
/// engine about 15 times a second, only while the tab is on screen.
/// </summary>
public sealed partial class ControlViewModel : ObservableObject, IDisposable
{
    private readonly SettingsService _settings;
    private readonly ControlService _service;
    private readonly Func<string, string, bool> _confirm;
    private readonly Action<Action> _post;
    private readonly ILogger _logger;
    private bool _active;
    private bool _disposed;
    private bool _syncing;
    private bool _compact;

    [ObservableProperty]
    private ControlSignal _signal = ControlSignal.Off;

    [ObservableProperty]
    private string _sinceText = string.Empty;

    [ObservableProperty]
    private string _lastSource = string.Empty;

    [ObservableProperty]
    private bool _sourceIpInvalid;

    [ObservableProperty]
    private string _sourceIpText = string.Empty;

    /// <param name="settings">Where the settings live.</param>
    /// <param name="service">The control service (status, test).</param>
    /// <param name="confirm">Asks a yes/no question (title, text); true is yes.</param>
    /// <param name="post">Runs an action on the UI thread; runs it at once when null (tests).</param>
    /// <param name="logger">Receives the log lines.</param>
    public ControlViewModel(SettingsService settings, ControlService service, Func<string, string, bool> confirm, Action<Action>? post = null, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(confirm);

        _settings = settings;
        _service = service;
        _confirm = confirm;
        _post = post ?? (action => action());
        _logger = logger ?? NullLogger.Instance;

        UniverseBox = new IntegerField(0, 32768, allowBlank: false, value => Update(c => c with { Universe = value ?? c.Universe }), () => { });
        AddRowCommand = new RelayCommand(AddRow);
        RestoreCommand = new RelayCommand(Restore);
        Load();
        _settings.Changed += OnSettingsChanged;
        _service.Changed += OnServiceChanged;
        Refresh();
    }

    /// <summary>The universe box (as the console shows it).</summary>
    public IntegerField UniverseBox { get; }

    /// <summary>
    /// The window is narrow: a line of the map takes two lines (the action, then the mode, the option and the level). The page sets it from its width
    /// (under 760 px of list), so nothing is ever cut or scrolled sideways.
    /// </summary>
    public bool Compact
    {
        get => _compact;
        set
        {
            if (SetProperty(ref _compact, value))
            {
                OnPropertyChanged(nameof(NotCompact));
            }
        }
    }

    /// <summary>The opposite of <see cref="Compact"/>, for the column titles.</summary>
    public bool NotCompact => !_compact;

    /// <summary>The 21 channels of the default map as short lines, for the help card (in pairs of columns).</summary>
    public IReadOnlyList<string> HelpMap { get; } = BuildHelpMap();

    /// <summary>The map.</summary>
    public ObservableCollection<ControlRowViewModel> Rows { get; } = [];

    /// <summary>Adds a row.</summary>
    public IRelayCommand AddRowCommand { get; }

    /// <summary>"Restaurar mapa padrao" (asks first).</summary>
    public IRelayCommand RestoreCommand { get; }

    /// <summary>The master switch "Controle pela MA2".</summary>
    public bool Enabled
    {
        get => _settings.Current.Control.Enabled;
        set
        {
            if (!_syncing && value != Enabled)
            {
                Update(c => c with { Enabled = value });
            }
        }
    }

    /// <summary>"Universo mostrado na MA2 = Art-Net + 1".</summary>
    public bool UniverseOffset
    {
        get => _settings.Current.Control.UniverseOffset;
        set
        {
            if (!_syncing && value != UniverseOffset)
            {
                Update(c => c with { UniverseOffset = value });
            }
        }
    }

    /// <summary>The Art-Net universe the app listens to (what goes on the wire), for the tooltip.</summary>
    public int ArtNetUniverse => _settings.Current.Control.ArtNetUniverse;

    /// <summary>The words of the signal chip.</summary>
    public string SignalText => Signal switch
    {
        ControlSignal.Ok => "Sinal ok",
        ControlSignal.NoSignal => "Sem sinal",
        ControlSignal.PortInUse => "Porta 6454 em uso",
        ControlSignal.Error => "Erro de rede",
        _ => "Desligado",
    };

    /// <summary>The tooltip of the signal chip.</summary>
    public string SignalTip => Signal switch
    {
        ControlSignal.Ok => $"Recebendo o universo {ArtNetUniverse} do Art-Net ({_settings.Current.Control.Universe} na MA2). {SinceTip()}",
        ControlSignal.NoSignal => $"Nenhum pacote do universo {ArtNetUniverse} do Art-Net h\U000000E1 mais de 3 s (ou ainda nenhum). O app mant\U000000E9m tudo como est\U000000E1 e ignora a MA2 at\U000000E9 o sinal voltar; ao voltar, os valores que a MA2 j\U000000E1 segura n\U000000E3o disparam nada. {SinceTip()}",
        ControlSignal.PortInUse => "A porta UDP 6454 do Art-Net est\U000000E1 em uso por outro programa. O app tenta de novo a cada 3 segundos.",
        ControlSignal.Error => "N\U000000E3o foi poss\U000000EDvel abrir a porta do Art-Net. O app tenta de novo a cada 3 segundos.",
        _ => "O controle pela MA2 est\U000000E1 desligado: o app n\U000000E3o escuta a rede.",
    };

    /// <summary>The state as a word the style reads (<c>ok</c>, <c>warn</c>, <c>bad</c>, <c>off</c>).</summary>
    public string SignalTone => Signal switch
    {
        ControlSignal.Ok => "ok",
        ControlSignal.NoSignal => "warn",
        ControlSignal.PortInUse or ControlSignal.Error => "bad",
        _ => "off",
    };

    /// <summary>Whether the line "ultimo pacote" is shown.</summary>
    public bool HasPacket => SinceText.Length > 0;

    /// <summary>The map has two rows on the same channel.</summary>
    public bool HasDuplicates => Rows.Any(r => r.IsDuplicate);

    /// <summary>The warning about a repeated channel.</summary>
    public string DuplicateTip => "H\U000000E1 mais de uma a\U000000E7\U000000E3o no mesmo canal: as duas rodam juntas.";

    /// <summary>The page is on screen (true) or hidden (false): the live numbers are read only while it is.</summary>
    public void SetActive(bool active)
    {
        if (_disposed || _active == active)
        {
            return;
        }

        _active = active;
        if (active)
        {
            Tick();
        }
    }

    /// <summary>Reads the live numbers (the page calls it about 15 times a second while visible).</summary>
    public void Tick()
    {
        if (_disposed || !_active)
        {
            return;
        }

        Refresh();
        var states = _service.Engine.GetRowStates();
        if (states.Count == Rows.Count)
        {
            for (var i = 0; i < states.Count; i++)
            {
                Rows[i].Apply(states[i]);
            }
        }
    }

    /// <summary>Runs a row once ("Testar").</summary>
    public void Test(ControlRow row) => _service.Test(row);

    /// <summary>Applies an edit of one row and saves it.</summary>
    public void Edit(ControlRowViewModel row, Func<ControlRow, ControlRow> change)
    {
        var index = Rows.IndexOf(row);
        if (index < 0)
        {
            return;
        }

        Update(c =>
        {
            var rows = c.Rows.ToList();
            if (index < rows.Count)
            {
                rows[index] = change(rows[index]);
            }

            return c with { Rows = rows };
        });
    }

    /// <summary>Takes a row out of the map.</summary>
    public void Remove(ControlRowViewModel row)
    {
        var index = Rows.IndexOf(row);
        if (index >= 0)
        {
            Update(c => c with { Rows = c.Rows.Where((_, i) => i != index).ToList() });
        }
    }

    /// <summary>Whether <paramref name="text"/> is empty or an IPv4 address.</summary>
    public static bool IsValidSource(string? text) =>
        string.IsNullOrWhiteSpace(text) || (IPAddress.TryParse(text.Trim(), out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _settings.Changed -= OnSettingsChanged;
        _service.Changed -= OnServiceChanged;
    }

    partial void OnSourceIpTextChanged(string value)
    {
        if (_syncing)
        {
            return;
        }

        SourceIpInvalid = !IsValidSource(value);
        if (!SourceIpInvalid)
        {
            Update(c => c with { SourceIp = value.Trim() });
        }
    }

    partial void OnSignalChanged(ControlSignal value)
    {
        OnPropertyChanged(nameof(SignalText));
        OnPropertyChanged(nameof(SignalTip));
        OnPropertyChanged(nameof(SignalTone));
    }

    partial void OnSinceTextChanged(string value)
    {
        OnPropertyChanged(nameof(HasPacket));
        OnPropertyChanged(nameof(SignalTip));
    }

    private static List<string> BuildHelpMap()
    {
        var lines = new List<string>();
        var rows = ControlSettings.DefaultRows();
        lines.Add("1 a 6  C\U000000E2mera 1 a 6 na pr\U000000E9via");
        foreach (var row in rows.Where(r => r.Action != ControlAction.SelectCamera))
        {
            var info = ControlCatalog.Get(row.Action);
            var how = row.Mode switch
            {
                ControlMode.State => " (estado)",
                ControlMode.Fader => " (fader)",
                _ => string.Empty,
            };
            var soon = info.Available ? string.Empty : " \U00002014 em breve";
            lines.Add($"{row.Channel}  {info.Name}{how}{soon}");
        }

        return lines;
    }

    private string SinceTip() => SinceText.Length > 0 ? $"\U000000DAltimo pacote {SinceText}" + (LastSource.Length > 0 ? $", de {LastSource}." : ".") : string.Empty;

    private void AddRow()
    {
        Update(c =>
        {
            var used = c.Rows.Select(r => r.Channel).ToHashSet();
            var channel = Enumerable.Range(1, ControlSettings.ChannelCount).FirstOrDefault(n => !used.Contains(n), 1);
            var info = ControlCatalog.Get(ControlAction.Take);
            return c with { Rows = [.. c.Rows, new ControlRow(channel, info.Action, info.DefaultMode)] };
        });
    }

    private void Restore()
    {
        if (!_confirm("Restaurar o mapa padr\U000000E3o?", "As 21 linhas voltam ao que veio de f\U000000E1brica. O que voc\U000000EA mudou no mapa se perde."))
        {
            return;
        }

        Update(c => c with { Rows = ControlSettings.DefaultRows() });
    }

    private void Update(Func<ControlSettings, ControlSettings> change) => _settings.Update(s => s with { Control = change(s.Control) });

    private void OnSettingsChanged(AppSettings settings) => _post(() =>
    {
        if (!_disposed)
        {
            Load();
            Refresh();
        }
    });

    private void OnServiceChanged() => _post(() =>
    {
        if (!_disposed)
        {
            Refresh();
        }
    });

    private void Refresh()
    {
        var status = _service.GetStatus();
        Signal = status.Signal;
        SinceText = status.SinceLastPacket is { } since ? "h\U000000E1 " + FormatSince(since) : string.Empty;
        LastSource = status.LastSource ?? string.Empty;
    }

    private static string FormatSince(TimeSpan since) =>
        since.TotalSeconds < 10
            ? since.TotalSeconds.ToString("0.0", CultureInfo.GetCultureInfo("pt-BR")) + " s"
            : Math.Floor(since.TotalSeconds).ToString(CultureInfo.InvariantCulture) + " s";

    // Reads the saved settings into the screen without writing anything back.
    private void Load()
    {
        var control = _settings.Current.Control;
        _syncing = true;
        try
        {
            UniverseBox.Set(control.Universe);
            if (control.SourceIp != SourceIpText.Trim() && IsValidSource(SourceIpText))
            {
                SourceIpText = control.SourceIp;
            }

            OnPropertyChanged(nameof(Enabled));
            OnPropertyChanged(nameof(UniverseOffset));
            OnPropertyChanged(nameof(ArtNetUniverse));
            OnPropertyChanged(nameof(SignalTip));

            if (Rows.Count == control.Rows.Count)
            {
                for (var i = 0; i < Rows.Count; i++)
                {
                    if (Rows[i].Row != control.Rows[i])
                    {
                        Rows[i].Sync(control.Rows[i]);
                    }
                }
            }
            else
            {
                Rows.Clear();
                foreach (var row in control.Rows)
                {
                    Rows.Add(new ControlRowViewModel(this, row));
                }
            }

            var counts = control.Rows.GroupBy(r => r.Channel).ToDictionary(g => g.Key, g => g.Count());
            for (var i = 0; i < Rows.Count; i++)
            {
                Rows[i].IsDuplicate = counts[control.Rows[i].Channel] > 1;
            }

            OnPropertyChanged(nameof(HasDuplicates));
        }
        finally
        {
            _syncing = false;
        }
    }
}
