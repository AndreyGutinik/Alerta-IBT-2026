using System.ComponentModel;
using AlertaIBT.Core.Screens;
using AlertaIBT.Core.Temple;

namespace AlertaIBT.App.ViewModels;

/// <summary>
/// What "Templo menor" lets the interface show: the tabs, the parts of the tabs, the cards of Configuracoes, the rows of the quick panel. One object for the whole
/// app (the window, the pages and the quick panel ask it), built on <see cref="TempleModeService"/>, so no page repeats a rule. Without a service everything shows
/// (<see cref="AllVisible"/>), which is what a page built alone (a test, a preview) gets. It tells <see cref="PropertyChanged"/> (empty name = everything) on the UI
/// thread when the mode or the list changes.
/// </summary>
public sealed class TempleViewState : INotifyPropertyChanged, IDisposable
{
    private readonly TempleModeService? _service;
    private readonly Action<Action> _post;
    private bool _disposed;

    /// <summary>A state with no mode: everything shows.</summary>
    public TempleViewState()
        : this(null, null)
    {
    }

    /// <param name="service">The mode; null shows everything.</param>
    /// <param name="post">Runs an action on the UI thread; at once when null (tests).</param>
    public TempleViewState(TempleModeService? service, Action<Action>? post = null)
    {
        _service = service;
        _post = post ?? (action => action());
        if (_service is not null)
        {
            _service.Changed += OnChanged;
        }
    }

    /// <summary>The state of an app without the mode (every function shows). Shared and read-only in practice: it has no service, so it never changes.</summary>
    public static TempleViewState AllVisible { get; } = new();

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Whether the mode is on (the green border shows).</summary>
    public bool IsOn => _service?.IsOn ?? false;

    /// <summary>The target screen size in force.</summary>
    public TargetSize Target => _service?.Target ?? TargetSize.DefaultNormal;

    /// <summary>The tabs that show.</summary>
    public TempleTabs Tabs => _service?.VisibleTabs ?? (TempleTabs.Home | TempleTabs.Som | TempleTabs.Ndi | TempleTabs.Control | TempleTabs.Settings);

    /// <summary>Whether the Inicio tab shows (needs Avisos).</summary>
    public bool HomeVisible => Tabs.HasFlag(TempleTabs.Home);

    /// <summary>Whether the Som tab shows.</summary>
    public bool SomVisible => Tabs.HasFlag(TempleTabs.Som);

    /// <summary>Whether the NDI tab shows.</summary>
    public bool NdiVisible => Tabs.HasFlag(TempleTabs.Ndi);

    /// <summary>Whether the Controle tab shows.</summary>
    public bool ControlVisible => Tabs.HasFlag(TempleTabs.Control);

    /// <summary>The first tab that shows: where the window opens and where it goes when the selected tab disappears.</summary>
    public TempleTabs FirstTab => _service is null ? TempleTabs.Home : TempleProfile.FirstVisible(_service.Current);

    /// <summary>Avisos: the bar, Telegram, the site, the queue and the history (their cards, the Novo aviso row).</summary>
    public bool AlertsVisible => Active(TempleFeature.Alerts);

    /// <summary>The player hero and the alarms of the Som tab, and the player row of the quick panel.</summary>
    public bool PlayerVisible => Active(TempleFeature.Player);

    /// <summary>The volumes at start and the audio outputs (the bottom row of the Som tab).</summary>
    public bool StartupAudioVisible => Active(TempleFeature.StartupAudio);

    /// <summary>The MA2 / Art-Net part of the Controle tab.</summary>
    public bool Ma2Visible => Active(TempleFeature.Ma2);

    /// <summary>The Holyrics card of the Controle tab.</summary>
    public bool HolyricsVisible => Active(TempleFeature.Holyrics);

    /// <summary>The Correcao section of Inicio and the Gemini model card (also needs Avisos for the section of Inicio, which is part of that tab).</summary>
    public bool GeminiVisible => Active(TempleFeature.Gemini);

    /// <summary>Whether a function runs.</summary>
    public bool Active(TempleFeature feature) => _service?.IsFeatureActive(feature) ?? true;

    /// <inheritdoc />
    public void Dispose()
    {
        _disposed = true;
        if (_service is not null)
        {
            _service.Changed -= OnChanged;
        }
    }

    private void OnChanged() => _post(() =>
    {
        if (!_disposed)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
        }
    });
}
