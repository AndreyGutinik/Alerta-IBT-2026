using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AlertaIBT.App.Views;

/// <summary>
/// The card of the redesigned tabs: a lighter surface with a thin border, the title with its icon in the header (<see cref="HeaderedContentControl.Header"/>,
/// <see cref="Icon"/>), an optional (i) with the long explanation as a tooltip (<see cref="Info"/>), the controls of the card at the right of the
/// header (<see cref="HeaderActions"/>) and the content below. Style: <c>IbtCardSectionStyle</c>.
/// </summary>
public sealed class CardSection : HeaderedContentControl
{
    /// <summary>The icon in front of the title (a Lucide geometry); null shows none.</summary>
    public static readonly DependencyProperty IconProperty =
        DependencyProperty.Register(nameof(Icon), typeof(Geometry), typeof(CardSection), new PropertyMetadata(null));

    /// <summary>The text of the (i) after the title; null shows no (i).</summary>
    public static readonly DependencyProperty InfoProperty =
        DependencyProperty.Register(nameof(Info), typeof(string), typeof(CardSection), new PropertyMetadata(null));

    /// <summary>What sits at the right end of the header (icon buttons, a toggle, a dot).</summary>
    public static readonly DependencyProperty HeaderActionsProperty =
        DependencyProperty.Register(nameof(HeaderActions), typeof(object), typeof(CardSection), new PropertyMetadata(null));

    /// <summary>Builds the card; it never takes the keyboard focus (only the controls in it do).</summary>
    public CardSection()
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

    /// <summary>The text of the (i) after the title; null shows none.</summary>
    public string? Info
    {
        get => (string?)GetValue(InfoProperty);
        set => SetValue(InfoProperty, value);
    }

    /// <summary>What sits at the right end of the header.</summary>
    public object? HeaderActions
    {
        get => GetValue(HeaderActionsProperty);
        set => SetValue(HeaderActionsProperty, value);
    }
}
