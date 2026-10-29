using System.Collections.ObjectModel;
using AlertaIBT.App.Platform;
using AlertaIBT.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AlertaIBT.App.ViewModels;

/// <summary>The three tabs of the main window (the correction lives inside Início).</summary>
public enum MainTab
{
    /// <summary>Início: what is on the bar, the message field, the correction (folded), the queue and the history.</summary>
    Home,

    /// <summary>Som: the volumes, the routing of the outputs, the music player and the alarms.</summary>
    Som,

    /// <summary>Configurações: the bar, Telegram, the system, the Gemini model and the diagnostics.</summary>
    Settings,
}

/// <summary>One line of the small status list at the bottom of the side bar.</summary>
/// <param name="Label">The name (<c>Telegram</c>, <c>Site</c>, <c>LED</c>, <c>Barra</c>, <c>Gemini</c>; <c>Offline</c> for a blue Gemini).</param>
/// <param name="Light">The color of the dot.</param>
/// <param name="Tip">The tooltip: the state, then the detail.</param>
public sealed record StatusRow(string Label, StatusLight Light, string Tip);

/// <summary>
/// The main window (spec 7.2): the three tabs and the status list of the side bar (Telegram, Site, LED, Barra, Gemini with their
/// dots; the Gemini line turns blue "Offline" when the offline corrector is working). The tabs' own logic is in
/// <see cref="Home"/> (with <see cref="Correction"/>), <see cref="Som"/> and <see cref="Settings"/>.
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly StatusHub _hub;
    private readonly Action<Action> _post;
    private readonly IUpdateStatus? _updates;
    private bool _disposed;

    [ObservableProperty]
    private MainTab _selectedTab = MainTab.Home;

    /// <param name="hub">The status of the parts.</param>
    /// <param name="home">The Início tab.</param>
    /// <param name="correction">The correction (shown in the Início tab; its model in Configurações).</param>
    /// <param name="settings">The Configurações tab.</param>
    /// <param name="saver">The autosave the two settings tabs share; saved one last time when this is disposed.</param>
    /// <param name="post">Runs an action on the UI thread; runs it at once when null (tests).</param>
    /// <param name="updates">The automatic update, for the one-line notice while it downloads or installs; null shows none.</param>
    /// <param name="som">The Som tab; null leaves the tab empty (tests of the other tabs).</param>
    public MainViewModel(
        StatusHub hub,
        HomeViewModel home,
        CorrectionViewModel correction,
        SettingsViewModel settings,
        Autosaver saver,
        Action<Action>? post = null,
        IUpdateStatus? updates = null,
        SomViewModel? som = null)
    {
        ArgumentNullException.ThrowIfNull(hub);
        ArgumentNullException.ThrowIfNull(home);
        ArgumentNullException.ThrowIfNull(correction);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(saver);

        _hub = hub;
        Home = home;
        Correction = correction;
        Settings = settings;
        Som = som;
        Saver = saver;
        _post = post ?? (action => action());
        SelectTabCommand = new RelayCommand<MainTab>(tab => SelectedTab = tab);

        _hub.PropertyChanged += OnHubChanged;
        RefreshStatus();
        _updates = updates;
        if (_updates is not null)
        {
            _updates.StateChanged += OnUpdateStateChanged;
            UpdateNotice = _updates.State.Notice ?? string.Empty;
        }
    }

    /// <summary>The one line shown at the top of the window while an update downloads or installs; empty otherwise.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdateNotice))]
    private string _updateNotice = string.Empty;

    /// <summary>Whether the update line is shown.</summary>
    public bool HasUpdateNotice => UpdateNotice.Length > 0;

    /// <summary>The Início tab.</summary>
    public HomeViewModel Home { get; }

    /// <summary>The correction: the key, the mode and the rules (Início), the model (Configurações).</summary>
    public CorrectionViewModel Correction { get; }

    /// <summary>The Configurações tab.</summary>
    public SettingsViewModel Settings { get; }

    /// <summary>The Som tab (null when the window was built without it).</summary>
    public SomViewModel? Som { get; }

    /// <summary>The autosave of the settings tabs (their "Salvo" stamp).</summary>
    public Autosaver Saver { get; }

    /// <summary>The five lines of the status list.</summary>
    public ObservableCollection<StatusRow> Status { get; } = [];

    /// <summary>Switches tab; the parameter is a <see cref="MainTab"/>.</summary>
    public IRelayCommand<MainTab> SelectTabCommand { get; }

    /// <summary>Whether the Início tab is selected.</summary>
    public bool IsHomeSelected => SelectedTab == MainTab.Home;

    /// <summary>Whether the Configurações tab is selected.</summary>
    public bool IsSettingsSelected => SelectedTab == MainTab.Settings;

    /// <summary>Whether the Som tab is selected.</summary>
    public bool IsSomSelected => SelectedTab == MainTab.Som;

    /// <summary>The window was shown: the Início tab takes a fresh look at the queue and the field takes the focus.</summary>
    public void OnShown()
    {
        RefreshStatus();
        Home.OnShown();
    }

    /// <summary>Saves what is waiting and lets go of the parts.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _hub.PropertyChanged -= OnHubChanged;
        if (_updates is not null)
        {
            _updates.StateChanged -= OnUpdateStateChanged;
        }

        Saver.Dispose();
        Home.Dispose();
        Home.Composer.Dispose();
        Correction.Dispose();
        Settings.Dispose();
        Som?.Dispose();
    }

    partial void OnSelectedTabChanged(MainTab value)
    {
        OnPropertyChanged(nameof(IsHomeSelected));
        OnPropertyChanged(nameof(IsSettingsSelected));
        OnPropertyChanged(nameof(IsSomSelected));
    }

    private void OnHubChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => _post(RefreshStatus);

    private void OnUpdateStateChanged(UpdateState state) =>
        _post(() =>
        {
            if (!_disposed)
            {
                UpdateNotice = state.Notice ?? string.Empty;
            }
        });

    private void RefreshStatus()
    {
        if (_disposed)
        {
            return;
        }

        StatusRow[] rows =
        [
            Row("Telegram", _hub.Telegram),
            Row("Site", _hub.Site),
            Row("LED", _hub.Led),
            Row("Barra", _hub.Bar),
            Row(_hub.Gemini.Light == StatusLight.Blue ? "Offline" : "Gemini", _hub.Gemini, "Gemini"),
        ];

        // Update in place when the number of lines is the same, so the tooltips under the mouse are not torn down.
        if (Status.Count != rows.Length)
        {
            Status.Clear();
            foreach (var row in rows)
            {
                Status.Add(row);
            }

            return;
        }

        for (var i = 0; i < rows.Length; i++)
        {
            if (Status[i] != rows[i])
            {
                Status[i] = rows[i];
            }
        }
    }

    private static StatusRow Row(string label, StatusIndicator indicator, string? name = null)
    {
        var title = name ?? label;
        return new StatusRow(
            label,
            indicator.Light,
            indicator.Detail.Length > 0 ? $"{title}: {indicator.Text}\n{indicator.Detail}" : $"{title}: {indicator.Text}");
    }
}
