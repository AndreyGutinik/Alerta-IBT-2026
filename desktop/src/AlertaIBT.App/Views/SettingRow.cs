using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AlertaIBT.App.Views;

/// <summary>
/// One setting, drawn as a block of the design system (a lighter surface with a thin border): the icon and the title (with an optional (i)
/// tooltip) on the left, the control (<see cref="ContentControl.Content"/>) on the right and, under both, an optional line of hints
/// (<see cref="Footer"/>). Every row of every card uses this one template, so they line up.
/// </summary>
public sealed class SettingRow : ContentControl
{
    /// <summary>The text on the left.</summary>
    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(SettingRow), new PropertyMetadata(string.Empty));

    /// <summary>The icon in front of the title (a Lucide geometry); null shows none.</summary>
    public static readonly DependencyProperty IconProperty =
        DependencyProperty.Register(nameof(Icon), typeof(Geometry), typeof(SettingRow), new PropertyMetadata(null));

    /// <summary>The text of the (i) icon after the title; null shows no icon.</summary>
    public static readonly DependencyProperty InfoProperty =
        DependencyProperty.Register(nameof(Info), typeof(string), typeof(SettingRow), new PropertyMetadata(null));

    /// <summary>What goes under the title and the control, across the whole row (hints and problems).</summary>
    public static readonly DependencyProperty FooterProperty =
        DependencyProperty.Register(nameof(Footer), typeof(object), typeof(SettingRow), new PropertyMetadata(null));

    /// <summary>Builds the row; it never takes the keyboard focus (only its control does).</summary>
    public SettingRow()
    {
        Focusable = false;
        IsTabStop = false;
    }

    /// <summary>The icon in front of the title.</summary>
    public Geometry? Icon
    {
        get => (Geometry?)GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    /// <summary>The text on the left.</summary>
    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>The text of the (i) icon after the title; null shows no icon.</summary>
    public string? Info
    {
        get => (string?)GetValue(InfoProperty);
        set => SetValue(InfoProperty, value);
    }

    /// <summary>What goes under the title and the control, across the whole row.</summary>
    public object? Footer
    {
        get => GetValue(FooterProperty);
        set => SetValue(FooterProperty, value);
    }
}

/// <summary>
/// The two children of a <see cref="SettingRow"/>: the title on the left and the control on the right, aligned to the right edge. When
/// they do not fit on one line the title wraps; if it would be left with less than <see cref="MinTitleWidth"/> the control moves to a
/// line of its own under it. Nothing is ever clipped and nothing needs a horizontal scroll bar.
/// </summary>
public sealed class SplitRowPanel : Panel
{
    /// <summary>The space between the title and the control.</summary>
    public const double Gap = 8;

    /// <summary>The narrowest the title may be made by wrapping before the control drops under it.</summary>
    public const double MinTitleWidth = 130;

    private const double StackedGap = 8;

    private bool _stacked;

    /// <inheritdoc />
    protected override Size MeasureOverride(Size available)
    {
        var (title, control) = Pair();
        if (title is null || control is null)
        {
            return default;
        }

        var infinite = new Size(double.PositiveInfinity, double.PositiveInfinity);
        control.Measure(new Size(available.Width, double.PositiveInfinity));
        title.Measure(infinite);
        _stacked = false;

        var controlWidth = control.DesiredSize.Width;
        if (controlWidth <= 0 || double.IsInfinity(available.Width) || title.DesiredSize.Width + Gap + controlWidth <= available.Width)
        {
            return new Size(
                double.IsInfinity(available.Width) ? title.DesiredSize.Width + Gap + controlWidth : available.Width,
                Math.Max(title.DesiredSize.Height, control.DesiredSize.Height));
        }

        var remaining = available.Width - controlWidth - Gap;
        if (remaining >= MinTitleWidth)
        {
            title.Measure(new Size(remaining, double.PositiveInfinity));
            return new Size(available.Width, Math.Max(title.DesiredSize.Height, control.DesiredSize.Height));
        }

        _stacked = true;
        title.Measure(new Size(available.Width, double.PositiveInfinity));
        return new Size(available.Width, title.DesiredSize.Height + StackedGap + control.DesiredSize.Height);
    }

    /// <inheritdoc />
    protected override Size ArrangeOverride(Size finalSize)
    {
        var (title, control) = Pair();
        if (title is null || control is null)
        {
            return finalSize;
        }

        var controlSize = control.DesiredSize;
        var titleSize = title.DesiredSize;
        if (_stacked)
        {
            title.Arrange(new Rect(0, 0, finalSize.Width, titleSize.Height));
            control.Arrange(new Rect(Math.Max(0, finalSize.Width - controlSize.Width), titleSize.Height + StackedGap, Math.Min(controlSize.Width, finalSize.Width), controlSize.Height));
            return finalSize;
        }

        var height = Math.Max(titleSize.Height, controlSize.Height);
        var titleWidth = Math.Max(0, Math.Min(titleSize.Width, finalSize.Width - controlSize.Width - (controlSize.Width > 0 ? Gap : 0)));
        title.Arrange(new Rect(0, (height - titleSize.Height) / 2, titleWidth, titleSize.Height));
        control.Arrange(new Rect(Math.Max(0, finalSize.Width - controlSize.Width), (height - controlSize.Height) / 2, Math.Min(controlSize.Width, finalSize.Width), controlSize.Height));
        return finalSize;
    }

    private (UIElement? Title, UIElement? Control) Pair() =>
        InternalChildren.Count >= 2 ? (InternalChildren[0], InternalChildren[1]) : (null, null);
}
