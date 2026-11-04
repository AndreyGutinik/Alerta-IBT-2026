using System.Runtime.CompilerServices;
using AlertaIBT.Core.Screens;
using AlertaIBT.Core.Settings;

namespace AlertaIBT.Core.Temple;

/// <summary>The tabs of the main window that can disappear (the Configuracoes tab never does).</summary>
[Flags]
public enum TempleTabs
{
    /// <summary>No tab.</summary>
    None = 0,

    /// <summary>Inicio (needs Avisos).</summary>
    Home = 1,

    /// <summary>Som (needs the player or the startup volumes and outputs).</summary>
    Som = 2,

    /// <summary>NDI.</summary>
    Ndi = 4,

    /// <summary>Controle (needs the MA2 or the Holyrics control).</summary>
    Control = 8,

    /// <summary>Configuracoes: always.</summary>
    Settings = 16,
}

/// <summary>
/// The pure rules of "Templo menor": which function is active given the user's own switches, the mode and the list; which tabs and parts of the interface show;
/// which screen size is the target. No state, no WPF: the shell, the pages and the service starters ask here, so nothing repeats an <c>if</c>.
/// </summary>
public static class TempleProfile
{
    private static readonly ConditionalWeakTable<AppSettings, AppSettings> EffectiveCache = new();

    /// <summary>Whether <paramref name="feature"/> runs: the mode is off, or the function is not on the list. Independent of the user's own switches.</summary>
    public static bool IsActive(this AppSettings settings, TempleFeature feature) => settings.Temple.IsActive(feature);

    /// <summary>"Alertas ligados" as the app must obey it: the user's switch AND the function is not turned off by Templo menor. The user's switch itself is never changed.</summary>
    public static bool AlertsRun(this AppSettings settings) => settings.System.AlertsEnabled && settings.Temple.IsActive(TempleFeature.Alerts);

    /// <summary>The tabs that show.</summary>
    public static TempleTabs VisibleTabs(TempleSettings temple)
    {
        var tabs = TempleTabs.Settings;
        if (temple.IsActive(TempleFeature.Alerts))
        {
            tabs |= TempleTabs.Home;
        }

        if (temple.IsActive(TempleFeature.Player) || temple.IsActive(TempleFeature.StartupAudio))
        {
            tabs |= TempleTabs.Som;
        }

        if (temple.IsActive(TempleFeature.Ndi))
        {
            tabs |= TempleTabs.Ndi;
        }

        if (temple.IsActive(TempleFeature.Ma2) || temple.IsActive(TempleFeature.Holyrics))
        {
            tabs |= TempleTabs.Control;
        }

        return tabs;
    }

    /// <summary>The tab the window opens on, and the one that takes over when the selected one disappears: Inicio, Som, NDI, Controle, Configuracoes, the first that shows.</summary>
    public static TempleTabs FirstVisible(TempleSettings temple)
    {
        var tabs = VisibleTabs(temple);
        foreach (var tab in new[] { TempleTabs.Home, TempleTabs.Som, TempleTabs.Ndi, TempleTabs.Control })
        {
            if (tabs.HasFlag(tab))
            {
                return tab;
            }
        }

        return TempleTabs.Settings;
    }

    /// <summary>
    /// The settings as the services must see them: the same file, with the functions that Templo menor turned off switched off in the fields the services already read
    /// (the player and its alarms, the startup sound rules, the Art-Net socket, the Holyrics switches, the NDI layer and shortcuts, the Gemini mode, the site).
    /// The user's own values are never written: this is a view for reading, and the settings file keeps what the owner chose. Same instance when nothing is off.
    /// </summary>
    public static AppSettings Effective(this AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var temple = settings.Temple;
        if (!temple.Enabled || temple.Disabled.Count == 0)
        {
            return settings;
        }

        return EffectiveCache.GetValue(settings, Project);
    }

    /// <summary>The target screen of the settings: the temple size while the mode is on, else the normal LED size.</summary>
    public static TargetSize TargetOf(AppSettings settings) => settings.Temple.Target;

    /// <summary>What a change of the mode or of the list will do, for the confirmation: what stops, what starts, and the screen size before and after.</summary>
    public static TemplePlan Plan(TempleSettings before, TempleSettings after)
    {
        var stops = new List<TempleFeature>();
        var starts = new List<TempleFeature>();
        foreach (var feature in TempleFeatures.All)
        {
            var was = before.IsActive(feature);
            var will = after.IsActive(feature);
            if (was && !will)
            {
                stops.Add(feature);
            }
            else if (!was && will)
            {
                starts.Add(feature);
            }
        }

        return new TemplePlan(stops, starts, before.Target, after.Target, before.Enabled, after.Enabled);
    }

    private static AppSettings Project(AppSettings s)
    {
        var t = s.Temple;
        var result = s;
        if (!t.IsActive(TempleFeature.Alerts))
        {
            result = result with { System = result.System with { AlertsEnabled = false }, Site = result.Site with { Enabled = false } };
        }

        if (!t.IsActive(TempleFeature.Player))
        {
            result = result with { Player = result.Player with { Enabled = false, AlarmsEnabled = false, MediaKeys = false } };
        }

        if (!t.IsActive(TempleFeature.StartupAudio))
        {
            result = result with { Audio = result.Audio with { Enabled = false, LockOutput = false, HolyricsRoute = false } };
        }

        if (!t.IsActive(TempleFeature.Ndi))
        {
            var blank = result.Ndi.Cameras.Select(camera => camera with { Source = string.Empty }).ToList();
            result = result with { Ndi = result.Ndi with { ShortcutsEnabled = false, LayerOn = false, TestPattern = false, Cameras = blank, FixedIps = [] } };
        }

        if (!t.IsActive(TempleFeature.Ma2))
        {
            result = result with { Control = result.Control with { Enabled = false } };
        }

        if (!t.IsActive(TempleFeature.Holyrics))
        {
            result = result with { Holyrics = result.Holyrics with { ArrowsEnabled = false, PresentationMode = false, CountdownStopEnabled = false } };
        }

        if (!t.IsActive(TempleFeature.Gemini))
        {
            result = result with { Correction = result.Correction with { Mode = "offline" } };
        }

        return result;
    }
}

/// <summary>What a change of Templo menor does (the confirmation dialog lists it).</summary>
/// <param name="Stops">The functions that stop.</param>
/// <param name="Starts">The functions that start again.</param>
/// <param name="From">The target screen size now.</param>
/// <param name="To">The target screen size after the change.</param>
/// <param name="WasOn">Whether the mode is on now.</param>
/// <param name="WillBeOn">Whether the mode will be on.</param>
public sealed record TemplePlan(IReadOnlyList<TempleFeature> Stops, IReadOnlyList<TempleFeature> Starts, TargetSize From, TargetSize To, bool WasOn, bool WillBeOn)
{
    /// <summary>Whether the change does nothing visible (no function changes and the same size).</summary>
    public bool IsEmpty => Stops.Count == 0 && Starts.Count == 0 && From == To && WasOn == WillBeOn;
}
