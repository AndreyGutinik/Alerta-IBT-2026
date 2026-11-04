using System.Windows;
using System.Windows.Controls;

namespace AlertaIBT.App.Theme;

/// <summary>
/// Cards in two columns that never leave a column empty or half a screen blank. Each child names the column it prefers (<see cref="SideProperty"/>: 0 left, 1 right) and the
/// panel keeps that layout while the two columns are of a similar height. When cards go away (a function turned off) and one column ends up much shorter than the other
/// (under <see cref="Balance"/> of its height), the visible cards are poured again in reading order, each into the shorter column, so both columns fill. Narrow windows get
/// ONE column in reading order (left cards first, then the right ones). The gap is the panel's <see cref="Gap"/>; the cards bring no margin of their own.
/// </summary>
public sealed class FlowColumns : Panel
{
    /// <summary>The column a child prefers when there are two: 0 left, 1 right.</summary>
    public static readonly DependencyProperty SideProperty =
        DependencyProperty.RegisterAttached("Side", typeof(int), typeof(FlowColumns), new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsParentMeasure));

    /// <summary>The narrowest a column may be before the panel drops to one column.</summary>
    public static readonly DependencyProperty MinColumnWidthProperty =
        DependencyProperty.Register(nameof(MinColumnWidth), typeof(double), typeof(FlowColumns), new FrameworkPropertyMetadata(400.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>The space between cards, across and down.</summary>
    public static readonly DependencyProperty GapProperty =
        DependencyProperty.Register(nameof(Gap), typeof(double), typeof(FlowColumns), new FrameworkPropertyMetadata(24.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>The shortest a column may be, as a share of the other, before the cards are poured again.</summary>
    public const double Balance = 0.4;

    /// <summary>Reads the preferred column of a child.</summary>
    public static int GetSide(DependencyObject element) => (int)element.GetValue(SideProperty);

    /// <summary>Sets the preferred column of a child.</summary>
    public static void SetSide(DependencyObject element, int value) => element.SetValue(SideProperty, value);

    /// <summary>The narrowest a column may be before the panel drops to one column.</summary>
    public double MinColumnWidth
    {
        get => (double)GetValue(MinColumnWidthProperty);
        set => SetValue(MinColumnWidthProperty, value);
    }

    /// <summary>The space between cards, across and down.</summary>
    public double Gap
    {
        get => (double)GetValue(GapProperty);
        set => SetValue(GapProperty, value);
    }

    /// <summary>The number of columns in the last layout (1 or 2).</summary>
    public int Columns { get; private set; } = 1;

    /// <summary>The column each visible child got in the last layout (0 or 1).</summary>
    public IReadOnlyDictionary<UIElement, int> Placement { get; private set; } = new Dictionary<UIElement, int>();

    private List<UIElement> Visible() => InternalChildren.Cast<UIElement>().Where(c => c.Visibility != Visibility.Collapsed).ToList();

    private int ColumnsFor(double width) => !double.IsInfinity(width) && width >= (2 * MinColumnWidth) + Gap ? 2 : 1;

    private double StackHeight(IEnumerable<UIElement> cards)
    {
        var list = cards.ToList();
        return list.Sum(c => c.DesiredSize.Height) + (Gap * Math.Max(0, list.Count - 1));
    }

    private Dictionary<UIElement, int> Place(List<UIElement> visible)
    {
        var placement = visible.ToDictionary(c => c, c => GetSide(c) == 0 ? 0 : 1);
        var left = visible.Where(c => placement[c] == 0).ToList();
        var right = visible.Where(c => placement[c] == 1).ToList();
        var a = StackHeight(left);
        var b = StackHeight(right);
        var tall = Math.Max(a, b);
        if (visible.Count < 2 || Math.Min(a, b) >= tall * Balance)
        {
            return placement;
        }

        // One column is (nearly) empty: pour the cards again in reading order, each into the shorter column.
        var heights = new double[2];
        var counts = new int[2];
        foreach (var card in visible)
        {
            var column = heights[0] <= heights[1] ? 0 : 1;
            placement[card] = column;
            heights[column] += card.DesiredSize.Height + (counts[column] > 0 ? Gap : 0);
            counts[column]++;
        }

        return placement;
    }

    /// <inheritdoc />
    protected override Size MeasureOverride(Size available)
    {
        var visible = Visible();
        Columns = ColumnsFor(available.Width);
        var width = double.IsInfinity(available.Width) ? MinColumnWidth : available.Width;
        var cell = Columns == 2 ? Math.Max(0, (width - Gap) / 2) : width;
        foreach (var child in visible)
        {
            child.Measure(new Size(cell, double.PositiveInfinity));
        }

        if (Columns == 1)
        {
            Placement = visible.ToDictionary(c => c, _ => 0);
            return new Size(width, StackHeight(visible));
        }

        var placement = Place(visible);
        Placement = placement;
        return new Size(width, Math.Max(StackHeight(visible.Where(c => placement[c] == 0)), StackHeight(visible.Where(c => placement[c] == 1))));
    }

    /// <inheritdoc />
    protected override Size ArrangeOverride(Size finalSize)
    {
        var visible = Visible();
        if (Columns == 1)
        {
            // Reading order: the cards the design puts on the left first, then the ones of the right.
            var y = 0.0;
            foreach (var card in visible.Where(c => GetSide(c) == 0).Concat(visible.Where(c => GetSide(c) != 0)))
            {
                card.Arrange(new Rect(0, y, finalSize.Width, card.DesiredSize.Height));
                y += card.DesiredSize.Height + Gap;
            }

            return finalSize;
        }

        var cell = Math.Max(0, (finalSize.Width - Gap) / 2);
        var placement = Place(visible);
        Placement = placement;
        foreach (var column in new[] { 0, 1 })
        {
            var y = 0.0;
            foreach (var card in visible.Where(c => placement[c] == column))
            {
                card.Arrange(new Rect(column * (cell + Gap), y, cell, card.DesiredSize.Height));
                y += card.DesiredSize.Height + Gap;
            }
        }

        return finalSize;
    }
}
