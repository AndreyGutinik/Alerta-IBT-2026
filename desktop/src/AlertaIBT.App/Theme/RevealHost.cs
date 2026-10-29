using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace AlertaIBT.App.Theme;

/// <summary>
/// A folding area that opens and closes smoothly: the height grows from 0 to the height of its content and the opacity follows, in 180 ms.
/// Animations off in Windows, or a host that is not on screen, change state at once. Used by the Expander style.
/// </summary>
public sealed class RevealHost : Decorator
{
    /// <summary>Whether the content is open.</summary>
    public static readonly DependencyProperty IsOpenProperty =
        DependencyProperty.Register(nameof(IsOpen), typeof(bool), typeof(RevealHost), new PropertyMetadata(false, OnIsOpenChanged));

    private static readonly DependencyProperty ProgressProperty =
        DependencyProperty.Register(
            nameof(Progress),
            typeof(double),
            typeof(RevealHost),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsMeasure, OnProgressChanged));

    /// <summary>Builds the host (closed).</summary>
    public RevealHost()
    {
        ClipToBounds = true;
        Opacity = 0;
        Loaded += (_, _) => SyncChild(); // a host that starts closed must not leave its content in the tab order
    }

    /// <summary>Whether the content is open.</summary>
    public bool IsOpen
    {
        get => (bool)GetValue(IsOpenProperty);
        set => SetValue(IsOpenProperty, value);
    }

    /// <summary>How open the host is now, 0 to 1 (for tests).</summary>
    public double Progress => (double)GetValue(ProgressProperty);

    /// <inheritdoc />
    protected override Size MeasureOverride(Size constraint)
    {
        if (Child is null)
        {
            return default;
        }

        Child.Measure(new Size(constraint.Width, double.PositiveInfinity));
        return new Size(Child.DesiredSize.Width, Child.DesiredSize.Height * Progress);
    }

    /// <inheritdoc />
    protected override Size ArrangeOverride(Size arrangeSize)
    {
        Child?.Arrange(new Rect(0, 0, arrangeSize.Width, Child.DesiredSize.Height));
        return arrangeSize;
    }

    private static void OnIsOpenChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var host = (RevealHost)d;
        var target = (bool)e.NewValue ? 1.0 : 0.0;
        if (host.Child is { } child)
        {
            child.Visibility = Visibility.Visible; // measured for its height even while it closes
        }

        if (!Motion.ShouldAnimate(host))
        {
            host.BeginAnimation(ProgressProperty, null);
            host.SetValue(ProgressProperty, target);
            return;
        }

        var animation = new DoubleAnimation(target, Motion.Medium)
        {
            EasingFunction = Motion.EaseOut,
            FillBehavior = FillBehavior.HoldEnd,
        };
        host.BeginAnimation(ProgressProperty, animation);
    }

    private static void OnProgressChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var host = (RevealHost)d;
        var progress = (double)e.NewValue;
        host.Opacity = progress;
        host.SyncChild();
    }

    /// <inheritdoc />
    protected override void OnVisualChildrenChanged(DependencyObject visualAdded, DependencyObject visualRemoved)
    {
        base.OnVisualChildrenChanged(visualAdded, visualRemoved);
        SyncChild(); // closed from the start: the content is not on screen and not in the tab order
    }

    private void SyncChild()
    {
        if (Child is { } child)
        {
            child.Visibility = Progress <= 0 && !IsOpen ? Visibility.Collapsed : Visibility.Visible;
        }
    }
}
