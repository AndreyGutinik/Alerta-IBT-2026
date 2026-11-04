using System.Globalization;

namespace AlertaIBT.Core.Screens;

/// <summary>
/// The size, in physical pixels, of the LED / telao the app draws for: 2560x768 normally and 1920x1080 in "Templo menor" (both editable in the settings).
/// Everything that used to assume 2560x768 (the monitor that counts as the LED, the bar, the NDI window and its crop, the thumbnails) asks for this.
/// </summary>
/// <param name="Width">Width in pixels, above 0.</param>
/// <param name="Height">Height in pixels, above 0.</param>
public readonly record struct TargetSize(int Width, int Height)
{
    /// <summary>The normal LED of the church: 2560x768.</summary>
    public static TargetSize DefaultNormal { get; } = new(2560, 768);

    /// <summary>The smaller temple's screen: 1920x1080.</summary>
    public static TargetSize DefaultTemple { get; } = new(1920, 1080);

    /// <summary>Width divided by height (3.33 for the LED, 1.78 for 16:9).</summary>
    public double Aspect => Height <= 0 ? 1 : (double)Width / Height;

    /// <summary>Whether a monitor has exactly this size.</summary>
    public bool Matches(ScreenInfo screen) => screen.Width == Width && screen.Height == Height;

    /// <summary>The size as people write it: <c>2560 x 768</c>.</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Width} x {Height}");
}
