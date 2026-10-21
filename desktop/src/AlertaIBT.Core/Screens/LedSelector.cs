namespace AlertaIBT.Core.Screens;

/// <summary>Picks the LED screen among the connected monitors.</summary>
public static class LedSelector
{
    /// <summary>The LED's physical width in pixels.</summary>
    public const int LedWidth = 2560;

    /// <summary>The LED's physical height in pixels.</summary>
    public const int LedHeight = 768;

    /// <summary>
    /// The LED: only a monitor of exactly 2560x768 counts. With more than one, the one whose device path is
    /// <paramref name="lastDevicePath"/> (compared ignoring case); otherwise the first that is not the primary monitor;
    /// otherwise the first (a lone 2560x768 primary). <c>null</c> when none is connected, which is the "LED off" state.
    /// </summary>
    /// <param name="screens">The connected monitors, in the order Windows lists them.</param>
    /// <param name="lastDevicePath">The device path of the LED used last, or null/blank when unknown.</param>
    public static ScreenInfo? Select(IReadOnlyList<ScreenInfo> screens, string? lastDevicePath)
    {
        ArgumentNullException.ThrowIfNull(screens);

        var leds = screens.Where(s => s.Width == LedWidth && s.Height == LedHeight).ToList();
        if (leds.Count == 0)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(lastDevicePath))
        {
            var remembered = leds.FirstOrDefault(s => string.Equals(s.DevicePath, lastDevicePath, StringComparison.OrdinalIgnoreCase));
            if (remembered is not null)
            {
                return remembered;
            }
        }

        return leds.FirstOrDefault(s => !s.IsPrimary) ?? leds[0];
    }
}
