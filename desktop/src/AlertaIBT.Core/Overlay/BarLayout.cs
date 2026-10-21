using AlertaIBT.Core.Screens;
using AlertaIBT.Core.Settings;

namespace AlertaIBT.Core.Overlay;

/// <summary>
/// Where the bar window sits on the screen and how big its parts are. The window is exactly as tall as the bar and as wide
/// as the screen, placed in physical pixels (so a monitor at 125% scaling still gets the exact 2560 pixels); the sizes inside
/// it are in device-independent units (1/96 inch) because that is what WPF draws with.
/// </summary>
/// <param name="WindowX">The window's left edge in virtual-desktop physical pixels.</param>
/// <param name="WindowY">The window's top edge in virtual-desktop physical pixels.</param>
/// <param name="WindowWidth">The window width in physical pixels (the screen width).</param>
/// <param name="WindowHeight">The window height in physical pixels (the bar height).</param>
/// <param name="BarHeight">The bar height in device-independent units.</param>
/// <param name="FontSize">The font size in device-independent units.</param>
/// <param name="PaddingX">The blank margin on each side of the text, in device-independent units.</param>
/// <param name="AtTop">Whether the bar is at the top of the screen (otherwise at the bottom).</param>
/// <param name="DpiScale">Physical pixels per device-independent unit of the monitor the layout was made for.</param>
public sealed record BarLayout(
    int WindowX,
    int WindowY,
    int WindowWidth,
    int WindowHeight,
    double BarHeight,
    double FontSize,
    double PaddingX,
    bool AtTop,
    double DpiScale)
{
    /// <summary>Space between letters, in em (v7 used <c>letter-spacing: .08em</c>).</summary>
    public const double LetterSpacingEm = 0.08;

    /// <summary>The font size as a share of the bar height when the user sets none (64%).</summary>
    public const double AutoFontShare = 0.64;

    /// <summary>
    /// Works the layout out. The bar height is held to the screen height. The font is <see cref="BarSettings.FontSize"/> when
    /// set, otherwise 64% of the height rounded to a whole pixel. Pixel sizes become device-independent units by dividing
    /// by <paramref name="dpiScale"/> (a missing or silly scale counts as 1).
    /// </summary>
    public static BarLayout Compute(ScreenInfo screen, BarSettings bar, double dpiScale)
    {
        ArgumentNullException.ThrowIfNull(screen);
        ArgumentNullException.ThrowIfNull(bar);

        var scale = double.IsFinite(dpiScale) && dpiScale >= 0.5 ? dpiScale : 1.0;
        var heightPx = Math.Clamp(bar.Height, 1, Math.Max(1, screen.Height));
        var fontPx = bar.FontSize ?? (int)Math.Round(heightPx * AutoFontShare, MidpointRounding.AwayFromZero);
        var atTop = !string.Equals(bar.Position, "bottom", StringComparison.OrdinalIgnoreCase);

        return new BarLayout(
            screen.X,
            atTop ? screen.Y : screen.Y + screen.Height - heightPx,
            screen.Width,
            heightPx,
            heightPx / scale,
            fontPx / scale,
            Math.Max(0, bar.PaddingX) / scale,
            atTop,
            scale);
    }

    /// <summary>
    /// How far, in device-independent units, the bar is pushed out of its window at <paramref name="offset"/> (0 shown,
    /// 1 hidden): up for a bar at the top, down for a bar at the bottom.
    /// </summary>
    public double SlideDistance(double offset) => (AtTop ? -1 : 1) * Math.Clamp(offset, 0, 1) * BarHeight;
}

/// <summary>The timing of the "show the bar for 3 seconds" test.</summary>
public static class BarPreview
{
    /// <summary>The example text of the test; it never enters the queue.</summary>
    public const string SampleText = "COROLLA PRATA | PLACA: ABC-1D23 | VIDRO ABERTO";

    /// <summary>How long the whole test pass lasts.</summary>
    public static readonly TimeSpan Duration = TimeSpan.FromSeconds(3);

    /// <summary>
    /// The scroll speed, in pixels per second, that makes one pass (slide in, scroll, slide out) last exactly
    /// <paramref name="total"/>. It ignores the user's speed setting on purpose: the test is about position, size and colors.
    /// </summary>
    public static double SpeedFor(double screenWidth, double textWidth, TimeSpan slide, TimeSpan total)
    {
        var scrolling = Math.Max(0.5, (total - (2 * slide)).TotalSeconds);
        return Math.Max(1, (screenWidth + textWidth) / scrolling);
    }
}
