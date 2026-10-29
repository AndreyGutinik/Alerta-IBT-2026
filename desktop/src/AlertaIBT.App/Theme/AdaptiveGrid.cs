using System.Windows;
using System.Windows.Controls;

namespace AlertaIBT.App.Theme;

/// <summary>
/// A grid of equal cells that chooses its own number of columns from the width it gets: as many as fit with each at least
/// <see cref="MinColumnWidth"/> wide (never more than <see cref="MaxColumns"/>, never fewer than one). The children keep their order, row by
/// row; every cell of a row is as tall as the tallest and every child fills its cell, so cards side by side end at the same line. Used for the alarm
/// blocks, the bottom row of the Som tab, and anywhere a row of blocks must stack when the window is narrow.
/// </summary>
public sealed class AdaptiveGrid : Panel
{
    /// <summary>The narrowest a cell may be before a column is dropped.</summary>
    public static readonly DependencyProperty MinColumnWidthProperty =
        DependencyProperty.Register(nameof(MinColumnWidth), typeof(double), typeof(AdaptiveGrid), new FrameworkPropertyMetadata(320.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>The most columns there may be.</summary>
    public static readonly DependencyProperty MaxColumnsProperty =
        DependencyProperty.Register(nameof(MaxColumns), typeof(int), typeof(AdaptiveGrid), new FrameworkPropertyMetadata(2, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>The space between cells, across and down.</summary>
    public static readonly DependencyProperty GapProperty =
        DependencyProperty.Register(nameof(Gap), typeof(double), typeof(AdaptiveGrid), new FrameworkPropertyMetadata(16.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>The narrowest a cell may be before a column is dropped.</summary>
    public double MinColumnWidth
    {
        get => (double)GetValue(MinColumnWidthProperty);
        set => SetValue(MinColumnWidthProperty, value);
    }

    /// <summary>The most columns there may be.</summary>
    public int MaxColumns
    {
        get => (int)GetValue(MaxColumnsProperty);
        set => SetValue(MaxColumnsProperty, value);
    }

    /// <summary>The space between cells, across and down.</summary>
    public double Gap
    {
        get => (double)GetValue(GapProperty);
        set => SetValue(GapProperty, value);
    }

    /// <summary>The number of columns in the last layout.</summary>
    public int Columns { get; private set; } = 1;

    /// <summary>How many columns fit in <paramref name="width"/>.</summary>
    public int ColumnsFor(double width)
    {
        if (double.IsInfinity(width))
        {
            return Math.Max(1, MaxColumns);
        }

        var fit = (int)Math.Floor((width + Gap) / (Math.Max(1, MinColumnWidth) + Gap));
        return Math.Clamp(fit, 1, Math.Max(1, MaxColumns));
    }

    /// <inheritdoc />
    protected override Size MeasureOverride(Size available)
    {
        var visible = InternalChildren.Cast<UIElement>().Where(c => c.Visibility != Visibility.Collapsed).ToList();
        Columns = ColumnsFor(available.Width);
        var width = double.IsInfinity(available.Width) ? MinColumnWidth * Columns : available.Width;
        var cell = Math.Max(0, (width - (Gap * (Columns - 1))) / Columns);
        var total = 0.0;
        var rows = 0;
        for (var start = 0; start < visible.Count; start += Columns)
        {
            var height = 0.0;
            for (var i = start; i < Math.Min(start + Columns, visible.Count); i++)
            {
                visible[i].Measure(new Size(cell, double.PositiveInfinity));
                height = Math.Max(height, visible[i].DesiredSize.Height);
            }

            total += height;
            rows++;
        }

        return new Size(width, total + (Gap * Math.Max(0, rows - 1)));
    }

    /// <inheritdoc />
    protected override Size ArrangeOverride(Size finalSize)
    {
        var visible = InternalChildren.Cast<UIElement>().Where(c => c.Visibility != Visibility.Collapsed).ToList();
        var cell = Math.Max(0, (finalSize.Width - (Gap * (Columns - 1))) / Columns);
        var y = 0.0;
        for (var start = 0; start < visible.Count; start += Columns)
        {
            var height = 0.0;
            for (var i = start; i < Math.Min(start + Columns, visible.Count); i++)
            {
                height = Math.Max(height, visible[i].DesiredSize.Height);
            }

            for (var i = start; i < Math.Min(start + Columns, visible.Count); i++)
            {
                var column = i - start;
                visible[i].Arrange(new Rect(column * (cell + Gap), y, cell, height));
            }

            y += height + Gap;
        }

        return finalSize;
    }
}
