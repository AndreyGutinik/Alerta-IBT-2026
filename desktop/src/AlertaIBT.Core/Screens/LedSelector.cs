namespace AlertaIBT.Core.Screens;

/// <summary>Picks the LED screen among the connected monitors.</summary>
public static class LedSelector
{
    /// <summary>The normal LED's physical width in pixels (the default target; Templo menor and the settings can change the target).</summary>
    public const int LedWidth = 2560;

    /// <summary>The normal LED's physical height in pixels.</summary>
    public const int LedHeight = 768;

    /// <summary>
    /// The LED of the normal size (2560x768): see <see cref="Select(IReadOnlyList{ScreenInfo}, string?, TargetSize)"/>.
    /// </summary>
    public static ScreenInfo? Select(IReadOnlyList<ScreenInfo> screens, string? lastDevicePath) =>
        Select(screens, lastDevicePath, TargetSize.DefaultNormal);

    /// <summary>
    /// The LED: only a monitor of exactly the <paramref name="target"/> size counts (2560x768 normally, 1920x1080 in Templo menor). With more than one, the one whose device path is
    /// <paramref name="lastDevicePath"/> (compared ignoring case); otherwise the first that is not the primary monitor;
    /// otherwise the first (a lone primary of that size). <c>null</c> when none is connected, which is the "LED off" state.
    /// </summary>
    /// <param name="screens">The connected monitors, in the order Windows lists them.</param>
    /// <param name="lastDevicePath">The device path of the LED used last, or null/blank when unknown.</param>
    /// <param name="target">The size a monitor must have to be the LED.</param>
    public static ScreenInfo? Select(IReadOnlyList<ScreenInfo> screens, string? lastDevicePath, TargetSize target)
    {
        ArgumentNullException.ThrowIfNull(screens);

        var leds = screens.Where(target.Matches).ToList();
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
