using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using AlertaIBT.App.ViewModels;

namespace AlertaIBT.App.Views;

/// <summary>
/// Connects the named parts of a page to <see cref="TempleViewState"/>: a part whose function "Templo menor" turned off is collapsed (it leaves the layout, it is not
/// just hidden), and the cards that remain close the gap it left (the first visible card of a column loses its top margin). One place for every page, so a page only lists
/// <c>(name, rule)</c> pairs.
/// </summary>
internal static class TempleVisual
{
    private static readonly ConditionalWeakTable<FrameworkElement, object> OriginalMargins = new();

    /// <summary>Applies the rules now and again each time the state changes.</summary>
    /// <param name="root">The page that owns the named parts.</param>
    /// <param name="state">The state to follow.</param>
    /// <param name="rules">The part name (<c>x:Name</c>) and whether it shows.</param>
    public static void Bind(FrameworkElement root, TempleViewState state, params (string Name, Func<TempleViewState, bool> Show)[] rules)
    {
        void Apply()
        {
            var parents = new HashSet<Panel>();
            var managed = new HashSet<FrameworkElement>();
            foreach (var (name, show) in rules)
            {
                if (root.FindName(name) is not FrameworkElement part)
                {
                    continue;
                }

                managed.Add(part);
                part.Visibility = show(state) ? Visibility.Visible : Visibility.Collapsed;
                if (part.Parent is Panel panel)
                {
                    parents.Add(panel);
                }
            }

            foreach (var panel in parents)
            {
                CloseGap(panel, managed);
            }
        }

        Apply();
        state.PropertyChanged += (_, _) => Apply();
    }

    // Among the parts a page manages inside one panel, the first one that shows takes the top margin the first one of them has when everything shows
    // (the page title above it already brings the space); the others keep their own, so no gap or double gap is left behind.
    private static void CloseGap(Panel panel, HashSet<FrameworkElement> managed)
    {
        var parts = panel.Children.OfType<FrameworkElement>().Where(managed.Contains).ToList();
        if (parts.Count == 0)
        {
            return;
        }

        foreach (var part in parts)
        {
            if (!OriginalMargins.TryGetValue(part, out _))
            {
                OriginalMargins.Add(part, part.Margin);
            }
        }

        var baseline = Original(parts[0]).Top;
        var placed = false;
        foreach (var part in parts.Where(p => p.Visibility == Visibility.Visible))
        {
            var original = Original(part);
            part.Margin = placed ? original : new Thickness(original.Left, baseline, original.Right, original.Bottom);
            placed = true;
        }
    }

    private static Thickness Original(FrameworkElement part) => OriginalMargins.TryGetValue(part, out var boxed) ? (Thickness)boxed : part.Margin;
}
