namespace AlertaIBT.Core.Screens;

/// <summary>
/// Picks the monitor an app window (the quick panel, the main window) opens on. The LED shows what the congregation sees
/// and what OBS/NDI capture, so a window must never open there when another monitor exists.
/// </summary>
public static class WindowMonitorChooser
{
    /// <summary>
    /// The monitor under the mouse, unless it is the LED; then the primary monitor unless it is the LED; then any other monitor;
    /// and only when the LED is the one monitor there is, the LED itself (the window is also excluded from capture, so it is
    /// seen on the LED but never captured). A mouse outside every monitor counts as being on the nearest one.
    /// </summary>
    /// <param name="screens">The connected monitors.</param>
    /// <param name="led">The LED, or <c>null</c> when none is connected (then nothing needs avoiding).</param>
    /// <param name="mouseX">The mouse position in virtual-desktop pixels.</param>
    /// <param name="mouseY">The mouse position in virtual-desktop pixels.</param>
    /// <returns>The monitor, or <c>null</c> when there are no monitors at all.</returns>
    public static ScreenInfo? Choose(IReadOnlyList<ScreenInfo> screens, ScreenInfo? led, int mouseX, int mouseY)
    {
        ArgumentNullException.ThrowIfNull(screens);

        if (screens.Count == 0)
        {
            return null;
        }

        var allowed = screens.Where(s => !IsLed(s, led)).ToList();
        if (allowed.Count == 0)
        {
            return Nearest(screens, mouseX, mouseY); // only the LED exists
        }

        var underMouse = Nearest(screens, mouseX, mouseY);
        if (!IsLed(underMouse, led))
        {
            return underMouse;
        }

        return allowed.FirstOrDefault(s => s.IsPrimary) ?? allowed[0];
    }

    /// <summary>Whether a rectangle (in virtual-desktop pixels) touches the LED screen.</summary>
    public static bool OverlapsLed(ScreenInfo? led, int left, int top, int right, int bottom) =>
        led is not null && left < led.X + led.Width && right > led.X && top < led.Y + led.Height && bottom > led.Y;

    private static bool IsLed(ScreenInfo screen, ScreenInfo? led) =>
        led is not null
        && string.Equals(screen.DeviceName, led.DeviceName, StringComparison.OrdinalIgnoreCase)
        && screen.X == led.X
        && screen.Y == led.Y
        && screen.Width == led.Width
        && screen.Height == led.Height;

    // The monitor that holds the point, or the closest one when it is outside all of them.
    private static ScreenInfo Nearest(IReadOnlyList<ScreenInfo> screens, int x, int y)
    {
        var best = screens[0];
        var bestDistance = long.MaxValue;
        foreach (var screen in screens)
        {
            var dx = x < screen.X ? screen.X - x : x >= screen.X + screen.Width ? x - (screen.X + screen.Width - 1) : 0;
            var dy = y < screen.Y ? screen.Y - y : y >= screen.Y + screen.Height ? y - (screen.Y + screen.Height - 1) : 0;
            var distance = ((long)dx * dx) + ((long)dy * dy);
            if (distance < bestDistance)
            {
                best = screen;
                bestDistance = distance;
            }
        }

        return best;
    }
}
