using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using AlertaIBT.App.ViewModels;
using AlertaIBT.Core.Diagnostics;
using AlertaIBT.Core.Services;

namespace AlertaIBT.App.Theme;

/// <summary>The colors of the status dots (painel-v7.html: <c>--gl</c>, <c>--wn</c>, <c>--bad</c>, <c>--blue</c>).</summary>
internal static class Palette
{
    public static readonly Brush Gray = Frozen(0x6F, 0x6A, 0x63);
    public static readonly Brush Green = Frozen(0x22, 0xE0, 0x7E);
    public static readonly Brush Yellow = Frozen(0xFF, 0xD8, 0x7A);
    public static readonly Brush Red = Frozen(0xFF, 0x5C, 0x5C);
    public static readonly Brush Blue = Frozen(0x5A, 0xA9, 0xFF);
    public static readonly Brush Accent = Frozen(0x1D, 0xA3, 0x5F);
    public static readonly Brush Cream = Frozen(0xFF, 0xF3, 0xDC);
    public static readonly Brush Ok = Frozen(0x86, 0xE4, 0xAD);
    public static readonly Brush ErrorText = Frozen(0xFF, 0x9D, 0x9D);

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}

/// <summary><see cref="StatusLight"/> to the brush of its dot.</summary>
[ValueConversion(typeof(StatusLight), typeof(Brush))]
public sealed class StatusLightBrushConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        StatusLight.Green => Palette.Green,
        StatusLight.Yellow => Palette.Yellow,
        StatusLight.Red => Palette.Red,
        StatusLight.Blue => Palette.Blue,
        _ => Palette.Gray,
    };

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>
/// <see cref="PreviewTone"/> to a brush. The parameter <c>icon</c> gives the color of the icon (green, blue, red), anything else
/// the color of the text (cream, cream, light red).
/// </summary>
[ValueConversion(typeof(PreviewTone), typeof(Brush))]
public sealed class PreviewToneBrushConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var icon = string.Equals(parameter as string, "icon", StringComparison.Ordinal);
        return value switch
        {
            PreviewTone.Ok => icon ? Palette.Accent : Palette.Cream,
            PreviewTone.Info => icon ? Palette.Blue : Palette.Cream,
            PreviewTone.Error => icon ? Palette.Red : Palette.ErrorText,
            _ => Palette.Cream,
        };
    }

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary><see cref="FeedbackTone"/> to the color of the message under the field.</summary>
[ValueConversion(typeof(FeedbackTone), typeof(Brush))]
public sealed class FeedbackToneBrushConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        FeedbackTone.Ok => Palette.Ok,
        FeedbackTone.Warn => Palette.Yellow,
        FeedbackTone.Error => Palette.ErrorText,
        _ => Palette.Cream,
    };

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>A resource key such as <c>LucidePause</c> to the geometry it names; nothing when the key is empty or unknown.</summary>
[ValueConversion(typeof(string), typeof(Geometry))]
public sealed class GeometryKeyConverter : IValueConverter
{
    /// <inheritdoc />
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string { Length: > 0 } key ? Application.Current?.TryFindResource(key) as Geometry : null;

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary><see cref="HistoryTone"/> to the color of a history line's icon and reason (green shown, red refused, grey the rest).</summary>
[ValueConversion(typeof(HistoryTone), typeof(Brush))]
public sealed class HistoryToneBrushConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        HistoryTone.Ok => Palette.Ok,
        HistoryTone.Bad => Palette.Red,
        _ => Palette.Gray,
    };

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary><see cref="LogSeverity"/> to the color of a log entry (warning yellow, error light red, the rest the log's grey).</summary>
[ValueConversion(typeof(LogSeverity), typeof(Brush))]
public sealed class LogSeverityBrushConverter : IValueConverter
{
    private static readonly Brush Plain = Frozen(0xCF, 0xCA, 0xC0);

    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        LogSeverity.Warning => Palette.Yellow,
        LogSeverity.Error => Palette.ErrorText,
        _ => Plain,
    };

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}

/// <summary>A <c>#RRGGBB</c> text to a brush (for the color swatch next to a color box); transparent when it is not a color.</summary>
[ValueConversion(typeof(string), typeof(Brush))]
public sealed class HexColorBrushConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string text && text.Length == 7 && text[0] == '#')
        {
            try
            {
                if (ColorConverter.ConvertFromString(text) is Color color)
                {
                    var brush = new SolidColorBrush(color);
                    brush.Freeze();
                    return brush;
                }
            }
            catch (FormatException)
            {
                // Not a color yet (the user is typing): no swatch.
            }
        }

        return Brushes.Transparent;
    }

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Shows an element only when the number bound to it is zero (an empty list, an empty box): the placeholder of a field, the "nothing here" line.</summary>
public sealed class ZeroToVisibleConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
        value is int number && number == 0 ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

    /// <inheritdoc />
    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => throw new NotSupportedException();
}
