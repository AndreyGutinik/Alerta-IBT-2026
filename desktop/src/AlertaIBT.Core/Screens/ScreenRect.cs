namespace AlertaIBT.Core.Screens;

/// <summary>
/// A rectangle in physical virtual-desktop pixels, as Windows' <c>RECT</c>: <see cref="Right"/> and <see cref="Bottom"/> are
/// exclusive. A monitor left of or above the primary one has negative coordinates. A struct, so comparing it never allocates.
/// </summary>
/// <param name="Left">The left edge.</param>
/// <param name="Top">The top edge.</param>
/// <param name="Right">The right edge (exclusive).</param>
/// <param name="Bottom">The bottom edge (exclusive).</param>
public readonly record struct ScreenRect(int Left, int Top, int Right, int Bottom)
{
    /// <summary>The width in pixels.</summary>
    public int Width => Right - Left;

    /// <summary>The height in pixels.</summary>
    public int Height => Bottom - Top;

    /// <summary>Whether the rectangle has no area (a monitor Windows reported wrongly).</summary>
    public bool IsEmpty => Right <= Left || Bottom <= Top;

    /// <summary>The x of the center pixel (rounded down), correct for negative coordinates too.</summary>
    public int CenterX => Left + (Right - Left) / 2;

    /// <summary>The y of the center pixel (rounded down), correct for negative coordinates too.</summary>
    public int CenterY => Top + (Bottom - Top) / 2;

    /// <summary>Whether the pixel (<paramref name="x"/>, <paramref name="y"/>) is inside.</summary>
    public bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;

    /// <summary>The pixel of the rectangle nearest to (<paramref name="x"/>, <paramref name="y"/>): the point itself when it is inside.</summary>
    public (int X, int Y) Clamp(int x, int y) =>
        (Math.Clamp(x, Left, Math.Max(Left, Right - 1)), Math.Clamp(y, Top, Math.Max(Top, Bottom - 1)));
}

/// <summary>Finds the primary monitor in a list of monitors (the fallback when Windows cannot say directly).</summary>
public static class PrimaryMonitorRect
{
    /// <summary>The bounds of the monitor flagged primary, or <c>null</c> when none is (or its size is not positive).</summary>
    public static ScreenRect? From(IEnumerable<ScreenInfo> screens)
    {
        ArgumentNullException.ThrowIfNull(screens);

        foreach (var screen in screens)
        {
            if (screen.IsPrimary && screen.Width > 0 && screen.Height > 0)
            {
                return new ScreenRect(screen.X, screen.Y, screen.X + screen.Width, screen.Y + screen.Height);
            }
        }

        return null;
    }
}
