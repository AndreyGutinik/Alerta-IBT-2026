using AlertaIBT.Core.Screens;
using AlertaIBT.Core.Settings;

namespace AlertaIBT.Core.Temple;

/// <summary>What the API and the windows ask of the "Templo menor" mode.</summary>
public interface ITempleControl
{
    /// <summary>Whether the mode is on.</summary>
    bool IsOn { get; }

    /// <summary>Whether a function runs (the mode is off, or the function is not on the list).</summary>
    bool IsFeatureActive(TempleFeature feature);

    /// <summary>The target screen size in force.</summary>
    TargetSize Target { get; }

    /// <summary>The functions on the list of what to turn off (whatever the mode says).</summary>
    IReadOnlyList<TempleFeature> Disabled { get; }

    /// <summary>Turns the mode on or off (the list is kept). The services follow the settings.</summary>
    void SetOn(bool on);
}

/// <summary>
/// The observable state of "Templo menor" on top of the settings: it only reads and writes <see cref="TempleSettings"/>, so the mode, the list and the sizes are
/// persisted like every other setting and applied from the first line of the start (the services read the settings when they start). The shell, the nav, the pages,
/// the quick panel and the service starters ask <see cref="IsFeatureActive"/>; <see cref="Changed"/> tells them when something that matters moved.
/// </summary>
public sealed class TempleModeService : ITempleControl, IDisposable
{
    private readonly SettingsService _settings;
    private readonly object _gate = new();
    private TempleSettings _last;

    /// <param name="settings">The settings, which hold the mode.</param>
    public TempleModeService(SettingsService settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        _settings = settings;
        _last = settings.Current.Temple;
        _settings.Changed += OnSettingsChanged;
    }

    /// <summary>The mode, the list or a screen size changed. Any thread; the settings already hold the new values.</summary>
    public event Action? Changed;

    /// <summary>The temple part of the settings now.</summary>
    public TempleSettings Current => _settings.Current.Temple;

    /// <inheritdoc />
    public bool IsOn => Current.Enabled;

    /// <inheritdoc />
    public TargetSize Target => Current.Target;

    /// <inheritdoc />
    public IReadOnlyList<TempleFeature> Disabled => Current.DisabledFeatures;

    /// <inheritdoc />
    public bool IsFeatureActive(TempleFeature feature) => Current.IsActive(feature);

    /// <summary>The tabs that show now.</summary>
    public TempleTabs VisibleTabs => TempleProfile.VisibleTabs(Current);

    /// <summary>What turning the mode on or off and choosing this list would do.</summary>
    public TemplePlan Plan(bool enabled, IEnumerable<TempleFeature> disabled) =>
        TempleProfile.Plan(Current, Current with { Enabled = enabled, Disabled = Keys(disabled) });

    /// <summary>Saves the mode and the list together (one change, so the services see one step).</summary>
    public void Apply(bool enabled, IEnumerable<TempleFeature> disabled)
    {
        ArgumentNullException.ThrowIfNull(disabled);

        var keys = Keys(disabled);
        _settings.Update(s => s with { Temple = s.Temple with { Enabled = enabled, Disabled = keys } });
    }

    /// <inheritdoc />
    public void SetOn(bool on) => _settings.Update(s => s.Temple.Enabled == on ? s : s with { Temple = s.Temple with { Enabled = on } });

    /// <summary>Saves the two screen sizes (held in range by the settings).</summary>
    public void SetSizes(TargetSize normal, TargetSize temple) =>
        _settings.Update(s => s with
        {
            Temple = s.Temple with { NormalWidth = normal.Width, NormalHeight = normal.Height, TempleWidth = temple.Width, TempleHeight = temple.Height },
        });

    /// <inheritdoc />
    public void Dispose() => _settings.Changed -= OnSettingsChanged;

    /// <summary>The JSON body of <c>/templo/status</c>.</summary>
    public Dictionary<string, object?> StatusBody()
    {
        var temple = Current;
        return new Dictionary<string, object?>
        {
            ["success"] = true,
            ["templeMode"] = temple.Enabled,
            ["disabled"] = temple.DisabledFeatures.Select(TempleFeatures.Key).ToArray(),
            ["resolution"] = new Dictionary<string, object?>
            {
                ["width"] = temple.Target.Width,
                ["height"] = temple.Target.Height,
                ["text"] = temple.Target.ToString(),
            },
        };
    }

    private static string[] Keys(IEnumerable<TempleFeature> features) =>
        TempleFeatures.All.Where(features.Contains).Select(TempleFeatures.Key).ToArray();

    private void OnSettingsChanged(AppSettings settings)
    {
        var next = settings.Temple;
        bool changed;
        lock (_gate)
        {
            changed = !next.Equals(_last);
            _last = next;
        }

        if (changed)
        {
            Changed?.Invoke();
        }
    }
}
