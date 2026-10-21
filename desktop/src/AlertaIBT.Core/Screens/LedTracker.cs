namespace AlertaIBT.Core.Screens;

/// <summary>
/// Keeps track of which monitor is the LED. Each <see cref="Refresh"/> lists the monitors, picks the LED
/// (<see cref="LedSelector"/>) and tells whether it differs from the last time: it appeared, vanished (LED switched off),
/// changed resolution or moved. In test mode (<c>--test-led</c>) the primary monitor stands in for the LED whatever its size.
/// </summary>
public sealed class LedTracker
{
    private readonly Func<IReadOnlyList<ScreenInfo>> _enumerate;
    private readonly Func<string?> _lastDevicePath;
    private readonly bool _testMode;

    /// <param name="enumerate">Lists the connected monitors now.</param>
    /// <param name="lastDevicePath">The device path of the LED used last (from the settings), to prefer it when there are two.</param>
    /// <param name="testMode">Whether the primary monitor counts as the LED.</param>
    public LedTracker(Func<IReadOnlyList<ScreenInfo>> enumerate, Func<string?> lastDevicePath, bool testMode)
    {
        ArgumentNullException.ThrowIfNull(enumerate);
        ArgumentNullException.ThrowIfNull(lastDevicePath);

        _enumerate = enumerate;
        _lastDevicePath = lastDevicePath;
        _testMode = testMode;
    }

    /// <summary>The LED as of the last refresh; null when none is connected (the "LED off" state).</summary>
    public ScreenInfo? Current { get; private set; }

    /// <summary>Whether the primary monitor stands in for the LED.</summary>
    public bool TestMode => _testMode;

    /// <summary>Looks at the monitors again. True when the LED differs from the previous refresh.</summary>
    public bool Refresh()
    {
        var screens = _enumerate();
        var led = _testMode
            ? screens.FirstOrDefault(s => s.IsPrimary) ?? screens.FirstOrDefault()
            : LedSelector.Select(screens, _lastDevicePath());

        var changed = !Equals(led, Current);
        Current = led;
        return changed;
    }
}
