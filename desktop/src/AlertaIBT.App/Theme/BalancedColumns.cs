using System.Windows;
using System.Windows.Controls;

namespace AlertaIBT.App.Theme;

/// <summary>
/// Two balanced columns of stacked blocks that fall back to ONE column, in child order, when the width is short. Each child says which column it
/// belongs to (<see cref="SideProperty"/>: 0 left, 1 right); inside a column the blocks keep their order and the panel's gap. When one column is
/// shorter, its LAST block grows so both bottoms end on the same line, so there is never an empty hole beside a short block.
/// </summary>
public sealed class BalancedColumns : Panel
{
    /// <summary>The column a child belongs to when there are two: 0 left, 1 right.</summary>
    public static readonly DependencyProperty SideProperty =
        DependencyProperty.RegisterAttached("Side", typeof(int), typeof(BalancedColumns), new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsParentMeasure));

    /// <summary>The narrowest a column may be before the panel drops to one column.</summary>
    public static readonly DependencyProperty MinColumnWidthProperty =
        DependencyProperty.Register(nameof(MinColumnWidth), typeof(double), typeof(BalancedColumns), new FrameworkPropertyMetadata(320.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>The space between blocks, across and down.</summary>
    public static readonly DependencyProperty GapProperty =
        DependencyProperty.Register(nameof(Gap), typeof(double), typeof(BalancedColumns), new FrameworkPropertyMetadata(16.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>Reads the column of a child.</summary>
    public static int GetSide(DependencyObject element) => (int)element.GetValue(SideProperty);

    /// <summary>Sets the column of a child.</summary>
    public static void SetSide(DependencyObject element, int value) => element.SetValue(SideProperty, value);

    /// <summary>The narrowest a column may be before the panel drops to one column.</summary>
    public double MinColumnWidth
    {
        get => (double)GetValue(MinColumnWidthProperty);
        set => SetValue(MinColumnWidthProperty, value);
    }

    /// <summary>The space between blocks, across and down.</summary>
    public double Gap
    {
        get => (double)GetValue(GapProperty);
        set => SetValue(GapProperty, value);
    }

    /// <summary>The number of columns in the last layout (1 or 2).</summary>
    public int Columns { get; private set; } = 1;

    private List<UIElement> Visible() => InternalChildren.Cast<UIElement>().Where(c => c.Visibility != Visibility.Collapsed).ToList();

    private int ColumnsFor(double width) => !double.IsInfinity(width) && width >= (2 * MinColumnWidth) + Gap ? 2 : 1;

    private double ColumnHeight(List<UIElement> blocks) => blocks.Sum(b => b.DesiredSize.Height) + (Gap * Math.Max(0, blocks.Count - 1));

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
            return new Size(width, ColumnHeight(visible));
        }

        var left = visible.Where(c => GetSide(c) == 0).ToList();
        var right = visible.Where(c => GetSide(c) != 0).ToList();
        return new Size(width, Math.Max(ColumnHeight(left), ColumnHeight(right)));
    }

    /// <inheritdoc />
    protected override Size ArrangeOverride(Size finalSize)
    {
        var visible = Visible();
        if (Columns == 1)
        {
            Stack(visible, 0, finalSize.Width, 0);
            return finalSize;
        }

        var cell = Math.Max(0, (finalSize.Width - Gap) / 2);
        var left = visible.Where(c => GetSide(c) == 0).ToList();
        var right = visible.Where(c => GetSide(c) != 0).ToList();
        var tallest = Math.Max(ColumnHeight(left), ColumnHeight(right));
        Stack(left, 0, cell, tallest - ColumnHeight(left));
        Stack(right, cell + Gap, cell, tallest - ColumnHeight(right));
        return finalSize;
    }

    private void Stack(List<UIElement> blocks, double x, double width, double extraForLast)
    {
        var y = 0.0;
        for (var i = 0; i < blocks.Count; i++)
        {
            var height = blocks[i].DesiredSize.Height + (i == blocks.Count - 1 ? extraForLast : 0);
            blocks[i].Arrange(new Rect(x, y, width, height));
            y += height + Gap;
        }
    }
}
