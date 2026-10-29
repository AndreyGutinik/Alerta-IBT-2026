using System.Collections.ObjectModel;
using AlertaIBT.Core.Ndi;
using AlertaIBT.Core.Screens;
using AlertaIBT.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AlertaIBT.App.ViewModels;

/// <summary>What the camera window asks: the title, the current name and source, the sources the finder knows and the check that tells what is wrong.</summary>
/// <param name="Title">The window title ("Nova c\U000000E2mera", "Editar c\U000000E2mera").</param>
/// <param name="Name">The name now.</param>
/// <param name="Source">The source now (a finder name or an address), or empty.</param>
/// <param name="Sources">The sources the finder found.</param>
/// <param name="Check">Gives the problem with (name, source), or null when they can be used.</param>
public sealed record CameraEditInput(string Title, string Name, string Source, IReadOnlyList<NdiSourceInfo> Sources, Func<string, string, string?> Check);

/// <summary>What the camera window answers.</summary>
/// <param name="Name">The name for the button.</param>
/// <param name="Source">The NDI source or the address.</param>
public sealed record CameraEditResult(string Name, string Source);

/// <summary>A monitor in the list of the telao.</summary>
/// <param name="DeviceName">The Windows display name (<c>\\.\DISPLAY3</c>).</param>
/// <param name="Display">What the list shows ("Tela 3 - 2560 x 768").</param>
public sealed record MonitorChoice(string DeviceName, string Display);

/// <summary>What the telao shows now.</summary>
public enum NdiSignal
{
    /// <summary>The camera layer is off.</summary>
    Off,

    /// <summary>On, but no camera is on air.</summary>
    NoCamera,

    /// <summary>Pictures are arriving.</summary>
    Live,

    /// <summary>The source stopped: the last picture is held (5 s), then it hides.</summary>
    Frozen,

    /// <summary>On, but nothing to show (the camera window is hidden: Holyrics shows normally).</summary>
    NoSignal,
}

/// <summary>Copies the latest picture of a lane (BGRA) into <paramref name="destination"/>; false when there is none or it did not change.</summary>
public delegate bool ThumbnailCopy(Span<byte> destination, out int width, out int height, out long version);

/// <summary>
/// One of the two big pictures at the top of the tab, Pr\U000000E9via or No ar: which camera is in it, how far down the picture the telao shows (0..100),
/// its state and where its latest thumbnail comes from. The vertical position is the camera's stored position: moving it moves the picture on
/// the telao at once and saves.
/// </summary>
public sealed partial class NdiLaneViewModel : ObservableObject
{
    private readonly NdiViewModel _owner;
    private readonly ThumbnailCopy _copy;
    private bool _syncing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCamera))]
    private int? _slot;

    [ObservableProperty]
    private string _cameraName = string.Empty;

    [ObservableProperty]
    private int _position = NdiSettings.DefaultVerticalPosition;

    [ObservableProperty]
    private NdiConnectionState _state = NdiConnectionState.Idle;

    [ObservableProperty]
    private bool _noSignal;

    [ObservableProperty]
    private bool _tallyRed;

    [ObservableProperty]
    private string _tallyTip = string.Empty;

    [ObservableProperty]
    private int _cropWidth = NdiViewModel.DefaultTargetWidth;

    [ObservableProperty]
    private int _cropHeight = NdiViewModel.DefaultTargetHeight;

    internal NdiLaneViewModel(NdiViewModel owner, bool isProgram, ThumbnailCopy copy)
    {
        _owner = owner;
        IsProgram = isProgram;
        _copy = copy;
    }

    /// <summary>True for "No ar", false for "Pr\U000000E9via".</summary>
    public bool IsProgram { get; }

    /// <summary>The name of the lane ("Pr\U000000E9via" or "No ar").</summary>
    public string Title => IsProgram ? "No ar" : "Pr\U000000E9via";

    /// <summary>The word on the label over the picture.</summary>
    public string PillText => IsProgram ? "NO AR" : "PR\U000000C9VIA";

    /// <summary>Whether a camera is in this lane.</summary>
    public bool HasCamera => Slot is not null;

    /// <summary>Whether to show the red/green tally (only the lane that is on air, with a camera).</summary>
    public bool ShowTally => IsProgram && HasCamera;

    /// <summary>The tooltip of the slider (what it does).</summary>
    public string SliderTip => "Posi\U000000E7\U000000E3o vertical: qual faixa da imagem aparece no tel\U000000E3o. 0% mostra o topo da imagem, 100% mostra a parte de baixo. Tamb\U000000E9m d\U000000E1 para girar o mouse sobre a imagem (Shift = 5%).";

    /// <summary>The picture of the lane as a short text for the tooltip and the screen reader.</summary>
    public string Description => HasCamera ? $"{Title}: {CameraName}" : $"{Title}: sem c\U000000E2mera";

    /// <summary>Copies the newest picture of this lane into <paramref name="destination"/>.</summary>
    public bool TryCopyThumbnail(Span<byte> destination, out int width, out int height, out long version) =>
        _copy(destination, out width, out height, out version);

    /// <summary>Moves the crop by <paramref name="delta"/> percent (negative = up); clamped to 0..100 and sent to the engine.</summary>
    public void Nudge(int delta) => Position = Math.Clamp(Position + delta, 0, 100);

    internal void Sync(int? slot, string name, int position, NdiConnectionState state, bool noSignal)
    {
        _syncing = true;
        try
        {
            Slot = slot;
            CameraName = name;
            Position = position;
            State = state;
            NoSignal = noSignal;
        }
        finally
        {
            _syncing = false;
        }

        OnPropertyChanged(nameof(ShowTally));
        OnPropertyChanged(nameof(Description));
    }

    partial void OnPositionChanged(int value)
    {
        if (_syncing)
        {
            return;
        }

        var clamped = Math.Clamp(value, 0, 100);
        if (clamped != value)
        {
            Position = clamped;
            return;
        }

        if (Slot is int slot)
        {
            _owner.MovePosition(slot, clamped);
        }
    }

    partial void OnCameraNameChanged(string value) => OnPropertyChanged(nameof(Description));
}

/// <summary>A camera button: a name the owner gave, the source behind it and whether it is in the Pr\U000000E9via or No ar.</summary>
public sealed partial class NdiCameraItem : ObservableObject
{
    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _source = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText))]
    private bool _isPreview;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText))]
    private bool _isProgram;

    internal NdiCameraItem(int slot, NdiViewModel owner)
    {
        Slot = slot;
        SelectCommand = new RelayCommand(() => owner.SelectCamera(this));
        EditCommand = new RelayCommand(() => owner.EditCamera(this));
        DeleteCommand = new RelayCommand(() => owner.DeleteCamera(this));
    }

    /// <summary>The slot, 0-based (what the engine uses).</summary>
    public int Slot { get; }

    /// <summary>The number shown (1-based, F1..F4 for the first four).</summary>
    public int Number => Slot + 1;

    /// <summary>Click: sends this camera to the Pr\U000000E9via (never on air).</summary>
    public IRelayCommand SelectCommand { get; }

    /// <summary>The pencil: name and source.</summary>
    public IRelayCommand EditCommand { get; }

    /// <summary>The trash (asks first).</summary>
    public IRelayCommand DeleteCommand { get; }

    /// <summary>Where this camera is right now, in words (the tooltip).</summary>
    public string StateText => IsProgram ? "No ar" : IsPreview ? "Na pr\U000000E9via" : string.Empty;

    /// <summary>The tooltip of the button.</summary>
    public string Tip
    {
        get
        {
            var shortcut = Number <= 4 ? $" (F{Number})" : string.Empty;
            var where = StateText.Length > 0 ? $" - {StateText}" : string.Empty;
            return $"{Name}{where}\nFonte: {(Source.Length > 0 ? Source : "nenhuma")}\nClique para mandar para a pr\U000000E9via{shortcut}. N\U000000E3o vai ao ar sem o bot\U000000E3o Cortar.";
        }
    }

    partial void OnNameChanged(string value) => OnPropertyChanged(nameof(Tip));

    partial void OnSourceChanged(string value) => OnPropertyChanged(nameof(Tip));

    partial void OnIsPreviewChanged(bool value) => OnPropertyChanged(nameof(Tip));

    partial void OnIsProgramChanged(bool value) => OnPropertyChanged(nameof(Tip));
}

/// <summary>The dashed "+" tile at the end of the camera buttons.</summary>
public sealed class NdiAddTile
{
    internal NdiAddTile(IRelayCommand command) => Command = command;

    /// <summary>Adds a camera.</summary>
    public IRelayCommand Command { get; }
}

/// <summary>
/// The "NDI" tab: the Pr\U000000E9via and No ar pictures with the Cortar button, the named cameras (click = Pr\U000000E9via), and the telao card (the on/off
/// switch, the status, the test bars, the monitor, hide from the live, the shortcuts). All the work is the engine's (<see cref="NdiService"/>): the
/// commands call it and the tab listens to its events, so the shortcuts and the local API show here at once. What the owner configures is saved
/// through the settings' own autosave. The engine is only asked to look for sources while the tab is on screen (<see cref="SetActive"/>).
/// </summary>
public sealed partial class NdiViewModel : ObservableObject, IDisposable
{
    /// <summary>The width of the LED monitor (the telao), used for the crop picture until a monitor is known.</summary>
    public const int DefaultTargetWidth = 2560;

    /// <summary>The height of the LED monitor.</summary>
    public const int DefaultTargetHeight = 768;

    private readonly NdiService _service;
    private readonly SettingsService _settings;
    private readonly Func<IReadOnlyList<ScreenInfo>> _screens;
    private readonly Func<ScreenInfo?> _led;
    private readonly Func<NdiHotkeyController?> _hotkeys;
    private readonly Func<bool>? _recheck;
    private readonly Action<Action> _post;
    private readonly Func<string, string, bool>? _confirm;
    private readonly Func<CameraEditInput, CameraEditResult?>? _editCamera;
    private readonly Func<IReadOnlyList<string>, IReadOnlyList<string>?>? _editIps;
    private readonly Action<string>? _openUrl;
    private readonly ILogger _logger;
    private readonly NdiCameraItem[] _items;
    private readonly NdiAddTile _addTile;
    private NdiHotkeyController? _hooked;
    private int _refreshQueued;
    private bool _refreshing;
    private bool _active;
    private bool _disposed;

    [ObservableProperty]
    private bool _runtimeOk = true;

    [ObservableProperty]
    private string _runtimeText = string.Empty;

    [ObservableProperty]
    private bool _layerOn;

    [ObservableProperty]
    private bool _testPattern;

    [ObservableProperty]
    private bool _hideFromCapture;

    [ObservableProperty]
    private bool _shortcutsEnabled = true;

    [ObservableProperty]
    private bool _monitorLocked = true;

    [ObservableProperty]
    private string? _selectedMonitor;

    [ObservableProperty]
    private string _notice = string.Empty;

    [ObservableProperty]
    private bool _takePending;

    [ObservableProperty]
    private NdiSignal _signal;

    [ObservableProperty]
    private string _fpsText = "-";

    [ObservableProperty]
    private string _resolutionText = "-";

    [ObservableProperty]
    private string _droppedText = "-";

    [ObservableProperty]
    private int _sourceCount;

    [ObservableProperty]
    private string _shortcutWarning = string.Empty;

    [ObservableProperty]
    private string _monitorText = string.Empty;

    /// <param name="service">The NDI engine.</param>
    /// <param name="settings">The settings (autosaved by their own service).</param>
    /// <param name="screens">Lists the monitors.</param>
    /// <param name="led">The monitor of the alert bar (the 2560x768 LED), or null.</param>
    /// <param name="hotkeys">The shortcut controller (null until the shell started).</param>
    /// <param name="recheck">Looks for the NDI runtime again; true when it works now. Null cannot look again.</param>
    /// <param name="post">Runs an action on the UI thread; runs it at once when null (tests).</param>
    /// <param name="confirm">Asks "title, message" in a window and answers whether the owner said yes; null never asks, it acts (tests).</param>
    /// <param name="editCamera">The camera window: answers the name and source, or null when cancelled.</param>
    /// <param name="editIps">The fixed IP window: answers the new list, or null when cancelled.</param>
    /// <param name="openUrl">Opens a link in the browser.</param>
    /// <param name="logger">Receives failures.</param>
    public NdiViewModel(
        NdiService service,
        SettingsService settings,
        Func<IReadOnlyList<ScreenInfo>>? screens = null,
        Func<ScreenInfo?>? led = null,
        Func<NdiHotkeyController?>? hotkeys = null,
        Func<bool>? recheck = null,
        Action<Action>? post = null,
        Func<string, string, bool>? confirm = null,
        Func<CameraEditInput, CameraEditResult?>? editCamera = null,
        Func<IReadOnlyList<string>, IReadOnlyList<string>?>? editIps = null,
        Action<string>? openUrl = null,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(settings);

        _service = service;
        _settings = settings;
        _screens = screens ?? (() => []);
        _led = led ?? (() => null);
        _hotkeys = hotkeys ?? (() => null);
        _recheck = recheck;
        _post = post ?? (action => action());
        _confirm = confirm;
        _editCamera = editCamera;
        _editIps = editIps;
        _openUrl = openUrl;
        _logger = logger ?? NullLogger.Instance;

        _items = Enumerable.Range(0, NdiSettings.SlotCount).Select(slot => new NdiCameraItem(slot, this)).ToArray();
        Preview = new NdiLaneViewModel(this, false, _service.Switcher.TryCopyPreviewThumbnail);
        OnAir = new NdiLaneViewModel(this, true, _service.Switcher.TryCopyProgramThumbnail);

        TakeCommand = new RelayCommand(Take);
        AddCameraCommand = new RelayCommand(AddCamera);
        _addTile = new NdiAddTile(AddCameraCommand);
        ManageIpsCommand = new RelayCommand(ManageIps);
        RecheckCommand = new RelayCommand(Recheck);
        DismissNoticeCommand = new RelayCommand(() => Notice = string.Empty);
        OpenDownloadCommand = new RelayCommand(() => _openUrl?.Invoke(NdiConstants.DownloadUrl));
        OpenSiteCommand = new RelayCommand(() => _openUrl?.Invoke(NdiConstants.SiteUrl));

        _service.Changed += OnEngineChanged;
        _service.SourcesChanged += OnEngineChanged;
        _service.Error += OnEngineError;
        _settings.Changed += OnSettingsChanged;
        Refresh();
    }

    /// <summary>The Pr\U000000E9via picture (left).</summary>
    public NdiLaneViewModel Preview { get; }

    /// <summary>The No ar picture (right).</summary>
    public NdiLaneViewModel OnAir { get; }

    /// <summary>The camera buttons, in slot order (only the cameras that were added).</summary>
    public ObservableCollection<NdiCameraItem> Cameras { get; } = [];

    /// <summary>What the camera grid shows: the camera buttons (<see cref="NdiCameraItem"/>) and, while there is room, the "+" tile (<see cref="NdiAddTile"/>).</summary>
    public ObservableCollection<object> Tiles { get; } = [];

    /// <summary>The monitors of the PC.</summary>
    public ObservableCollection<MonitorChoice> Monitors { get; } = [];

    /// <summary>The fixed IPs the finder also asks (for the tooltip of the manager).</summary>
    public ObservableCollection<string> FixedIps { get; } = [];

    /// <summary>The Cortar button.</summary>
    public IRelayCommand TakeCommand { get; }

    /// <summary>The "+" tile.</summary>
    public IRelayCommand AddCameraCommand { get; }

    /// <summary>The fixed IP list manager.</summary>
    public IRelayCommand ManageIpsCommand { get; }

    /// <summary>"Verificar de novo" of the missing-runtime card.</summary>
    public IRelayCommand RecheckCommand { get; }

    /// <summary>Hides the message line.</summary>
    public IRelayCommand DismissNoticeCommand { get; }

    /// <summary>Opens the NDI Tools download page.</summary>
    public IRelayCommand OpenDownloadCommand { get; }

    /// <summary>Opens ndi.video.</summary>
    public IRelayCommand OpenSiteCommand { get; }

    /// <summary>The trademark line NDI asks for.</summary>
    public string TrademarkText => NdiConstants.TrademarkNotice;

    /// <summary>The NDI site.</summary>
    public string SiteUrl => NdiConstants.SiteUrl;

    /// <summary>The NDI Tools download page.</summary>
    public string DownloadUrl => NdiConstants.DownloadUrl;

    /// <summary>Whether Cortar can work: the runtime is there and a camera is in the Pr\U000000E9via.</summary>
    public bool CanTake => RuntimeOk && Preview.HasCamera;

    /// <summary>Whether at least one camera was added.</summary>
    public bool HasCameras => Cameras.Count > 0;

    /// <summary>Whether no camera was added yet.</summary>
    public bool HasNoCameras => Cameras.Count == 0;

    /// <summary>Whether another camera can be added (up to six).</summary>
    public bool CanAddCamera => Cameras.Count < NdiSettings.SlotCount;

    /// <summary>Whether a message is shown.</summary>
    public bool HasNotice => Notice.Length > 0;

    /// <summary>Whether a shortcut could not be registered.</summary>
    public bool HasShortcutWarning => ShortcutWarning.Length > 0;

    /// <summary>Whether the monitor list can be used (the monitor is unlocked).</summary>
    public bool MonitorUnlocked => !MonitorLocked;

    /// <summary>Whether the NDI runtime is missing (the banner shows and the rest is off).</summary>
    public bool RuntimeMissing => !RuntimeOk;

    /// <summary>The words for <see cref="Signal"/>.</summary>
    public string SignalText => Signal switch
    {
        NdiSignal.Live => "ao vivo",
        NdiSignal.Frozen => "congelado",
        NdiSignal.NoSignal => "sem sinal",
        NdiSignal.NoCamera => "sem c\U000000E2mera no ar",
        _ => "desligado",
    };

    /// <summary>The tooltip of the signal chip.</summary>
    public string SignalTip => Signal switch
    {
        NdiSignal.Live => "A c\U000000E2mera est\U000000E1 chegando e aparece no tel\U000000E3o.",
        NdiSignal.Frozen => "A fonte parou de enviar: a \U000000FAltima imagem fica parada por 5 segundos e depois some (o Holyrics aparece normal). Volta sozinha quando a imagem voltar.",
        NdiSignal.NoSignal => "C\U000000E2mera no tel\U000000E3o ligada, mas sem imagem para mostrar: a janela da c\U000000E2mera est\U000000E1 escondida e o Holyrics aparece normal.",
        NdiSignal.NoCamera => "C\U000000E2mera no tel\U000000E3o ligada, mas n\U000000E3o h\U000000E1 c\U000000E2mera no ar. Escolha uma na pr\U000000E9via e corte.",
        _ => "A c\U000000E2mera no tel\U000000E3o est\U000000E1 desligada.",
    };

    /// <summary>Tells the tab it is on screen (or not): the finder and the clock of the numbers only run while it is.</summary>
    public void SetActive(bool active)
    {
        if (_disposed || _active == active)
        {
            return;
        }

        _active = active;
        if (active)
        {
            _service.StartDiscovery();
            HookHotkeys();
            Refresh();
        }
        else
        {
            _service.StopDiscovery();
        }
    }

    /// <summary>Looks at the engine again (the page calls it about twice a second while visible, for the numbers).</summary>
    public void Tick()
    {
        if (!_disposed && _active)
        {
            Refresh();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (_active)
        {
            _service.StopDiscovery();
            _active = false;
        }

        _disposed = true;
        _service.Changed -= OnEngineChanged;
        _service.SourcesChanged -= OnEngineChanged;
        _service.Error -= OnEngineError;
        _settings.Changed -= OnSettingsChanged;
        if (_hooked is not null)
        {
            _hooked.Changed -= OnEngineChanged;
        }
    }

    // ---- commands ----

    internal void SelectCamera(NdiCameraItem item)
    {
        Report(_service.SelectPreview(item.Slot));
        Refresh();
    }

    /// <summary>Cuts: the Pr\U000000E9via goes on air and the camera that was on air goes to the Pr\U000000E9via.</summary>
    public void Take()
    {
        Report(_service.Take());
        Refresh();
    }

    internal void MovePosition(int slot, int position)
    {
        Report(_service.SetVerticalPosition(slot, position));
    }

    internal void AddCamera()
    {
        var free = _items.FirstOrDefault(item => !IsConfigured(_settings.Current.Ndi, item.Slot));
        if (free is null)
        {
            return;
        }

        var answer = Ask("Nova c\U000000E2mera", DefaultName(free.Slot), string.Empty, free.Slot);
        if (answer is null)
        {
            return;
        }

        Report(_service.SetCamera(free.Slot, answer.Name, answer.Source));
        Refresh();
    }

    internal void EditCamera(NdiCameraItem item)
    {
        var answer = Ask("Editar c\U000000E2mera", item.Name, item.Source, item.Slot);
        if (answer is null)
        {
            return;
        }

        Report(_service.SetCamera(item.Slot, answer.Name, answer.Source));
        Refresh();
    }

    internal void DeleteCamera(NdiCameraItem item)
    {
        if (_confirm is not null
            && !_confirm($"Remover a c\U000000E2mera \U0000201C{item.Name}\U0000201D?", "Ela sai da lista de bot\U000000F5es. A c\U000000E2mera em si n\U000000E3o \U000000E9 mexida."))
        {
            return;
        }

        var slot = item.Slot;
        _settings.Update(settings =>
        {
            var cameras = settings.Ndi.Cameras.ToList();
            cameras[slot] = new NdiCameraSlot(DefaultName(slot), string.Empty);
            return settings with { Ndi = settings.Ndi with { Cameras = cameras } };
        });
        Refresh();
    }

    internal void ManageIps()
    {
        var current = _settings.Current.Ndi.FixedIps;
        var answer = _editIps?.Invoke(current);
        if (answer is null)
        {
            return;
        }

        var clean = answer.Select(ip => ip.Trim()).Where(ip => ip.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        UpdateNdi(ndi => ndi with { FixedIps = clean });
    }

    internal void Recheck()
    {
        var found = _recheck?.Invoke() ?? _service.RuntimeAvailable;
        if (found && _active)
        {
            // The finder could not start before: ask again now that the runtime works.
            _service.StopDiscovery();
            _service.StartDiscovery();
        }

        Notice = found ? string.Empty : "O NDI Tools ainda n\U000000E3o foi encontrado neste PC.";
        Refresh();
    }

    // ---- toggles ----

    partial void OnLayerOnChanged(bool value)
    {
        if (!_refreshing)
        {
            Report(_service.SetLayer(value));
            Refresh();
        }
    }

    partial void OnTestPatternChanged(bool value)
    {
        if (!_refreshing)
        {
            Report(_service.SetTestPattern(value));
            Refresh();
        }
    }

    partial void OnHideFromCaptureChanged(bool value)
    {
        if (!_refreshing)
        {
            UpdateNdi(ndi => ndi with { HideFromCapture = value });
        }
    }

    partial void OnShortcutsEnabledChanged(bool value)
    {
        if (!_refreshing)
        {
            UpdateNdi(ndi => ndi with { ShortcutsEnabled = value });
        }
    }

    partial void OnMonitorLockedChanged(bool value)
    {
        OnPropertyChanged(nameof(MonitorUnlocked));
        if (_refreshing)
        {
            return;
        }

        // Unlocking always starts from the 2560x768 monitor again; locking goes back to it.
        var home = DefaultMonitor()?.DeviceName ?? string.Empty;
        UpdateNdi(ndi => ndi with { MonitorLocked = value, MonitorDeviceName = value ? ndi.MonitorDeviceName : home });
    }

    partial void OnSelectedMonitorChanged(string? value)
    {
        if (_refreshing || MonitorLocked || string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        UpdateNdi(ndi => ndi with { MonitorDeviceName = value });
    }

    partial void OnNoticeChanged(string value) => OnPropertyChanged(nameof(HasNotice));

    partial void OnShortcutWarningChanged(string value) => OnPropertyChanged(nameof(HasShortcutWarning));

    partial void OnRuntimeOkChanged(bool value) => OnPropertyChanged(nameof(RuntimeMissing));

    partial void OnSignalChanged(NdiSignal value)
    {
        OnPropertyChanged(nameof(SignalText));
        OnPropertyChanged(nameof(SignalTip));
    }

    // ---- reading the engine ----

    private static bool IsConfigured(NdiSettings ndi, int slot)
    {
        var camera = ndi.Cameras[slot];
        return camera.Source.Length > 0 || !string.Equals(camera.Name, DefaultName(slot), StringComparison.Ordinal);
    }

    private static string DefaultName(int slot) => $"C\U000000E2mera {slot + 1}";

    private CameraEditResult? Ask(string title, string name, string source, int slot)
    {
        if (_editCamera is null)
        {
            return null;
        }

        string? Check(string typedName, string typedSource)
        {
            if (string.IsNullOrWhiteSpace(typedName))
            {
                return "Escreva um nome para o bot\U000000E3o.";
            }

            if (typedName.Trim().Length > 32)
            {
                return "O nome pode ter at\U000000E9 32 letras.";
            }

            return NdiSourceParser.Parse(typedSource) is null ? "Escolha uma fonte da lista ou digite o IP da c\U000000E2mera." : null;
        }

        return _editCamera(new CameraEditInput(title, name, source, _service.Sources, Check));
    }

    private void UpdateNdi(Func<NdiSettings, NdiSettings> change) =>
        _settings.Update(settings => settings with { Ndi = change(settings.Ndi) });

    private void Report(NdiResult result)
    {
        Notice = result.Message ?? string.Empty;
    }

    private void OnEngineChanged() => QueueRefresh();

    private void OnSettingsChanged(AppSettings _) => QueueRefresh();

    private void OnEngineError(string message) => _post(() =>
    {
        if (!_disposed)
        {
            Notice = message;
        }
    });

    private void QueueRefresh()
    {
        if (Interlocked.Exchange(ref _refreshQueued, 1) == 1)
        {
            return;
        }

        _post(() =>
        {
            Interlocked.Exchange(ref _refreshQueued, 0);
            Refresh();
        });
    }

    private void HookHotkeys()
    {
        var controller = _hotkeys();
        if (controller is not null && !ReferenceEquals(controller, _hooked))
        {
            if (_hooked is not null)
            {
                _hooked.Changed -= OnEngineChanged;
            }

            _hooked = controller;
            _hooked.Changed += OnEngineChanged;
        }
    }

    private ScreenInfo? DefaultMonitor()
    {
        var screens = _screens();
        return screens.FirstOrDefault(s => s.Width == DefaultTargetWidth && s.Height == DefaultTargetHeight) ?? _led();
    }

    private static string MonitorDisplay(ScreenInfo screen)
    {
        var digits = new string(screen.DeviceName.Where(char.IsDigit).ToArray());
        var name = digits.Length > 0 ? $"Tela {digits}" : screen.DeviceName;
        return $"{name} - {screen.Width} x {screen.Height}{(screen.IsPrimary ? " (principal)" : string.Empty)}";
    }

    private void Refresh()
    {
        if (_disposed)
        {
            return;
        }

        _refreshing = true;
        try
        {
            HookHotkeys();
            var status = _service.GetStatus();
            var sw = status.Switcher;
            var ndi = _settings.Current.Ndi;
            RuntimeOk = status.RuntimeOk;
            RuntimeText = status.RuntimeText;
            LayerOn = sw.LayerOn;
            TestPattern = sw.TestPattern;
            TakePending = sw.TakePending;
            HideFromCapture = ndi.HideFromCapture;
            ShortcutsEnabled = ndi.ShortcutsEnabled;
            MonitorLocked = ndi.MonitorLocked;
            SourceCount = _service.Sources.Count;

            SyncCameras(ndi, sw);
            SyncMonitors(ndi);
            SyncLanes(ndi, sw);
            SyncSignal(sw);
            SyncIps(ndi);
            OnPropertyChanged(nameof(CanTake));

            var warnings = _hooked?.Warnings ?? [];
            ShortcutWarning = ndi.ShortcutsEnabled ? string.Join("\n", warnings) : string.Empty;
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void SyncCameras(NdiSettings ndi, NdiSwitcherStatus sw)
    {
        var wanted = new List<NdiCameraItem>();
        foreach (var item in _items)
        {
            var camera = ndi.Cameras[item.Slot];
            item.Name = camera.Name;
            item.Source = camera.Source;
            item.IsPreview = sw.PreviewSlot == item.Slot;
            item.IsProgram = sw.ProgramSlot == item.Slot;
            if (IsConfigured(ndi, item.Slot))
            {
                wanted.Add(item);
            }
        }

        for (var i = Cameras.Count - 1; i >= 0; i--)
        {
            if (!wanted.Contains(Cameras[i]))
            {
                Cameras.RemoveAt(i);
            }
        }

        for (var i = 0; i < wanted.Count; i++)
        {
            if (i >= Cameras.Count || !ReferenceEquals(Cameras[i], wanted[i]))
            {
                Cameras.Insert(i, wanted[i]);
            }
        }

        // The grid: the buttons, then the "+" tile while there is room.
        var tiles = new List<object>(wanted);
        if (wanted.Count < NdiSettings.SlotCount)
        {
            tiles.Add(_addTile);
        }

        for (var i = Tiles.Count - 1; i >= 0; i--)
        {
            if (!tiles.Contains(Tiles[i]))
            {
                Tiles.RemoveAt(i);
            }
        }

        for (var i = 0; i < tiles.Count; i++)
        {
            if (i >= Tiles.Count || !ReferenceEquals(Tiles[i], tiles[i]))
            {
                Tiles.Insert(i, tiles[i]);
            }
        }

        OnPropertyChanged(nameof(HasCameras));
        OnPropertyChanged(nameof(HasNoCameras));
        OnPropertyChanged(nameof(CanAddCamera));
    }

    private void SyncMonitors(NdiSettings ndi)
    {
        var screens = _screens();
        var wanted = screens.Select(s => new MonitorChoice(s.DeviceName, MonitorDisplay(s))).ToList();
        if (!Monitors.SequenceEqual(wanted))
        {
            Monitors.Clear();
            foreach (var monitor in wanted)
            {
                Monitors.Add(monitor);
            }
        }

        var home = DefaultMonitor();
        var chosenName = ndi.MonitorLocked || string.IsNullOrWhiteSpace(ndi.MonitorDeviceName) ? home?.DeviceName : ndi.MonitorDeviceName;
        SelectedMonitor = chosenName;
        var current = _service.ChooseMonitor(screens, _led() ?? home);
        MonitorText = current is null ? "nenhum monitor" : MonitorDisplay(current);
    }

    private void SyncLanes(NdiSettings ndi, NdiSwitcherStatus sw)
    {
        var screens = _screens();
        var target = _service.ChooseMonitor(screens, _led() ?? DefaultMonitor());
        var width = target?.Width ?? DefaultTargetWidth;
        var height = target?.Height ?? DefaultTargetHeight;

        SyncLane(Preview, sw.PreviewSlot, sw.PreviewState, ndi, width, height, false);
        SyncLane(OnAir, sw.ProgramSlot, sw.ProgramState, ndi, width, height, sw.ProgramLastFrameAgeMs is > 1500);

        var onTelao = sw.LayerOn && sw.WindowVisible;
        OnAir.TallyRed = onTelao;
        OnAir.TallyTip = onTelao
            ? "NO AR: esta c\U000000E2mera est\U000000E1 aparecendo no tel\U000000E3o agora."
            : sw.LayerOn
                ? "NO AR: esta c\U000000E2mera est\U000000E1 escolhida, mas n\U000000E3o aparece no tel\U000000E3o (sem imagem)."
                : "NO AR: esta c\U000000E2mera est\U000000E1 escolhida; a c\U000000E2mera no tel\U000000E3o est\U000000E1 desligada, ent\U000000E3o nada aparece no LED.";
    }

    private static void SyncLane(NdiLaneViewModel lane, int? slot, NdiConnectionState state, NdiSettings ndi, int width, int height, bool stale)
    {
        // A camera that was removed while it sat in a lane shows as an empty lane (the engine let go of its source).
        if (slot is int removed && !IsConfigured(ndi, removed))
        {
            slot = null;
        }

        var camera = slot is int s ? ndi.Cameras[s] : null;
        var noSignal = slot is not null && (state is NdiConnectionState.Lost or NdiConnectionState.Failed || stale);
        lane.Sync(slot, camera?.Name ?? string.Empty, camera?.VerticalPosition ?? NdiSettings.DefaultVerticalPosition, state, noSignal);
        lane.CropWidth = width;
        lane.CropHeight = height;
    }

    private void SyncSignal(NdiSwitcherStatus sw)
    {
        FpsText = sw.ProgramSlot is null ? "-" : $"{Math.Round(sw.ProgramFps)} fps";
        ResolutionText = sw.ProgramWidth > 0 ? $"{sw.ProgramWidth} x {sw.ProgramHeight}" : "-";
        DroppedText = sw.ProgramSlot is null ? "-" : sw.ProgramFramesDropped.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (!sw.LayerOn)
        {
            Signal = NdiSignal.Off;
        }
        else if (sw.ProgramSlot is null)
        {
            Signal = NdiSignal.NoCamera;
        }
        else if (!sw.WindowVisible)
        {
            Signal = NdiSignal.NoSignal;
        }
        else
        {
            Signal = sw.ProgramLastFrameAgeMs is > 1500 || sw.ProgramState is NdiConnectionState.Lost or NdiConnectionState.Failed ? NdiSignal.Frozen : NdiSignal.Live;
        }
    }

    private void SyncIps(NdiSettings ndi)
    {
        if (!FixedIps.SequenceEqual(ndi.FixedIps))
        {
            FixedIps.Clear();
            foreach (var ip in ndi.FixedIps)
            {
                FixedIps.Add(ip);
            }
        }
    }
}
