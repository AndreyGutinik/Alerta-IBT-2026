using AlertaIBT.Core.Screens;
using AlertaIBT.Core.Settings;
using AlertaIBT.Core.Temple;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AlertaIBT.App.ViewModels;

/// <summary>
/// The "Templo menor" card of Configuracoes > Sistema and the "Resolucao do telao" card: the master switch, the gear and the four size boxes. The switch and the gear
/// never change anything by themselves: each one asks (a confirmation that lists what stops, what starts and the new size) and only <c>Confirmar</c> saves. The first
/// time the mode is turned on the gear opens first, with nothing marked, so the owner picks. The services follow the saved settings; this class only writes them.
/// </summary>
public sealed partial class TempleViewModel : ObservableObject, IDisposable
{
    private readonly TempleModeService _service;
    private readonly SettingsService _settings;
    private readonly Func<IReadOnlyList<TempleFeature>, IReadOnlyList<TempleFeature>?> _pickFeatures;
    private readonly Func<TemplePlan, bool> _confirm;
    private readonly Action<Action> _post;
    private bool _disposed;

    /// <param name="service">The mode.</param>
    /// <param name="settings">The settings (for the sizes).</param>
    /// <param name="pickFeatures">The gear dialog: gets the list now, answers the new list or null when cancelled.</param>
    /// <param name="confirm">The confirmation dialog: true only for Confirmar. Null always confirms (tests).</param>
    /// <param name="post">Runs an action on the UI thread; at once when null (tests).</param>
    public TempleViewModel(
        TempleModeService service,
        SettingsService settings,
        Func<IReadOnlyList<TempleFeature>, IReadOnlyList<TempleFeature>?>? pickFeatures = null,
        Func<TemplePlan, bool>? confirm = null,
        Action<Action>? post = null)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(settings);

        _service = service;
        _settings = settings;
        _pickFeatures = pickFeatures ?? (current => current);
        _confirm = confirm ?? (_ => true);
        _post = post ?? (action => action());
        ToggleCommand = new RelayCommand(Toggle);
        GearCommand = new RelayCommand(OpenGear);
        NormalWidth = Box(width: true, normal: true);
        NormalHeight = Box(width: false, normal: true);
        TempleWidth = Box(width: true, normal: false);
        TempleHeight = Box(width: false, normal: false);
        Reload();
        _service.Changed += OnChanged;
    }

    /// <summary>Whether the mode is on (what is saved: it moves only after Confirmar).</summary>
    public bool IsOn => _service.IsOn;

    /// <summary>Click on the switch: asks, and turns the mode on or off only on Confirmar.</summary>
    public IRelayCommand ToggleCommand { get; }

    /// <summary>The gear: the list of what to turn off.</summary>
    public IRelayCommand GearCommand { get; }

    /// <summary>The normal LED width box.</summary>
    public IntegerField NormalWidth { get; }

    /// <summary>The normal LED height box.</summary>
    public IntegerField NormalHeight { get; }

    /// <summary>The Templo menor screen width box.</summary>
    public IntegerField TempleWidth { get; }

    /// <summary>The Templo menor screen height box.</summary>
    public IntegerField TempleHeight { get; }

    /// <summary>The normal size as <c>L x A</c>.</summary>
    public string NormalText => _settings.Current.Temple.NormalSize.ToString();

    /// <summary>The Templo menor size as <c>L x A</c>.</summary>
    public string TempleText => _settings.Current.Temple.TempleSize.ToString();

    /// <summary>The normal size is the one in force (highlighted).</summary>
    public bool NormalActive => !IsOn;

    /// <summary>The Templo menor size is the one in force (highlighted).</summary>
    public bool TempleActive => IsOn;

    /// <summary>How many functions are on the list (the number on the gear).</summary>
    public int DisabledCount => _service.Disabled.Count;

    /// <summary>The tooltip of the gear.</summary>
    public string GearTip => DisabledCount == 0
        ? "Escolher o que o templo menor n\U000000E3o usa"
        : "Desligadas no templo menor: " + string.Join(", ", _service.Disabled.Select(TempleFeatures.Title));

    /// <summary>The switch was clicked.</summary>
    internal void Toggle()
    {
        var turnOn = !_service.IsOn;
        var list = _service.Disabled;
        if (turnOn && list.Count == 0)
        {
            // The first time: nothing is pre-marked, the owner picks (cancelling the gear cancels the switch).
            if (_pickFeatures(list) is not { } picked)
            {
                Refresh();
                return;
            }

            list = picked;
        }

        var plan = _service.Plan(turnOn, list);
        if (_confirm(plan))
        {
            _service.Apply(turnOn, list);
        }

        Refresh();
    }

    /// <summary>The gear was clicked.</summary>
    internal void OpenGear()
    {
        if (_pickFeatures(_service.Disabled) is not { } picked)
        {
            return;
        }

        if (!_service.IsOn)
        {
            _service.Apply(false, picked); // the mode is off: the list only waits (nothing changes, so nothing to confirm)
            Refresh();
            return;
        }

        if (_service.Plan(true, picked).IsEmpty)
        {
            return;
        }

        if (_confirm(_service.Plan(true, picked)))
        {
            _service.Apply(true, picked);
        }

        Refresh();
    }

    /// <summary>The settings were loaded again (a restored backup): the boxes read them.</summary>
    public void Reload()
    {
        var temple = _settings.Current.Temple;
        NormalWidth.Set(temple.NormalWidth);
        NormalHeight.Set(temple.NormalHeight);
        TempleWidth.Set(temple.TempleWidth);
        TempleHeight.Set(temple.TempleHeight);
        Refresh();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _disposed = true;
        _service.Changed -= OnChanged;
    }

    private IntegerField Box(bool width, bool normal) =>
        new(
            TempleSettings.MinSide,
            TempleSettings.MaxSide,
            allowBlank: false,
            value =>
            {
                if (value is not { } number)
                {
                    return;
                }

                var temple = _settings.Current.Temple;
                var size = normal ? temple.NormalSize : temple.TempleSize;
                size = width ? size with { Width = number } : size with { Height = number };
                _service.SetSizes(normal ? size : temple.NormalSize, normal ? temple.TempleSize : size);
            },
            () => { });

    private void OnChanged() => _post(() =>
    {
        if (!_disposed)
        {
            Refresh();
        }
    });

    private void Refresh()
    {
        OnPropertyChanged(string.Empty);
    }
}
