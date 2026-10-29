using System.Windows;
using System.Windows.Media;

namespace AlertaIBT.App.Theme;

/// <summary>
/// The two drawings of an icon-only toggle (the lock, the pin, the speaker): <see cref="OffGeometryProperty"/> while it is off and
/// <see cref="OnGeometryProperty"/> while it is on. Set them in a style (<c>IbtLockToggle</c>, <c>IbtPinToggle</c>, <c>IbtMuteToggle</c>); the shared
/// template <c>IbtIconToggle</c> shows the right one.
/// </summary>
public static class IconToggle
{
    /// <summary>The icon drawn while the toggle is on.</summary>
    public static readonly DependencyProperty OnGeometryProperty =
        DependencyProperty.RegisterAttached("OnGeometry", typeof(Geometry), typeof(IconToggle), new PropertyMetadata(null));

    /// <summary>The icon drawn while the toggle is off.</summary>
    public static readonly DependencyProperty OffGeometryProperty =
        DependencyProperty.RegisterAttached("OffGeometry", typeof(Geometry), typeof(IconToggle), new PropertyMetadata(null));

    /// <summary>Gets <see cref="OnGeometryProperty"/>.</summary>
    public static Geometry? GetOnGeometry(DependencyObject element) => (Geometry?)element.GetValue(OnGeometryProperty);

    /// <summary>Sets <see cref="OnGeometryProperty"/>.</summary>
    public static void SetOnGeometry(DependencyObject element, Geometry? value) => element.SetValue(OnGeometryProperty, value);

    /// <summary>Gets <see cref="OffGeometryProperty"/>.</summary>
    public static Geometry? GetOffGeometry(DependencyObject element) => (Geometry?)element.GetValue(OffGeometryProperty);

    /// <summary>Sets <see cref="OffGeometryProperty"/>.</summary>
    public static void SetOffGeometry(DependencyObject element, Geometry? value) => element.SetValue(OffGeometryProperty, value);
}
