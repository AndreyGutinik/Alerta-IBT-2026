using System.Windows;
using System.Windows.Media.Animation;

namespace AlertaIBT.App.Theme;

/// <summary>
/// The motion of the app, in one place: short, subtle, never a loop. Everything here honours the Windows setting that turns animations
/// off (<see cref="SystemParameters.ClientAreaAnimation"/>) and never animates an element that is not on screen (a window that was never shown
/// has no frames, so the tests and a hidden tab see the final state at once).
/// </summary>
public static class Motion
{
    /// <summary>A tab change or a cover change: 140 ms.</summary>
    public static readonly Duration Fast = new(TimeSpan.FromMilliseconds(140));

    /// <summary>A folding area opening or closing: 180 ms.</summary>
    public static readonly Duration Medium = new(TimeSpan.FromMilliseconds(180));

    /// <summary>Turns the fade of a page when it is shown on; set it on a page (a UserControl) that is shown and hidden by its tab.</summary>
    public static readonly DependencyProperty FadeOnShowProperty =
        DependencyProperty.RegisterAttached("FadeOnShow", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnFadeOnShowChanged));

    /// <summary>Whether animations may play now (false when the owner turned them off in Windows).</summary>
    public static bool Enabled => SystemParameters.ClientAreaAnimation;

    /// <summary>Gets <see cref="FadeOnShowProperty"/>.</summary>
    public static bool GetFadeOnShow(DependencyObject element) => (bool)element.GetValue(FadeOnShowProperty);

    /// <summary>Sets <see cref="FadeOnShowProperty"/>.</summary>
    public static void SetFadeOnShow(DependencyObject element, bool value) => element.SetValue(FadeOnShowProperty, value);

    /// <summary>The ease of every motion: fast at the start, soft at the end (an exponential ease-out).</summary>
    public static IEasingFunction EaseOut { get; } = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 5 };

    /// <summary>Whether <paramref name="element"/> should animate now (animations on, and it is really on screen).</summary>
    public static bool ShouldAnimate(UIElement element) => Enabled && element.IsVisible && element.IsArrangeValid;

    /// <summary>Fades the element from transparent to its own opacity, once.</summary>
    public static void FadeIn(UIElement element, Duration? duration = null)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (!Enabled || !element.IsVisible)
        {
            return;
        }

        var animation = new DoubleAnimation(0, 1, duration ?? Fast)
        {
            EasingFunction = EaseOut,
            FillBehavior = FillBehavior.Stop,
        };
        element.BeginAnimation(UIElement.OpacityProperty, animation);
    }

    private static void OnFadeOnShowChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element)
        {
            return;
        }

        element.IsVisibleChanged -= OnVisibleChanged;
        if ((bool)e.NewValue)
        {
            element.IsVisibleChanged += OnVisibleChanged;
        }
    }

    private static void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is UIElement element && e.NewValue is true)
        {
            FadeIn(element);
        }
    }
}
