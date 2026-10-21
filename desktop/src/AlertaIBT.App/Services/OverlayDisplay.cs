using AlertaIBT.App.Overlay;
using AlertaIBT.Core.Screens;
using AlertaIBT.Core.Services;

namespace AlertaIBT.App.Services;

/// <summary>The alert service's view of the bar: the overlay controller for playing, the LED source for knowing if there is a screen.</summary>
public sealed class OverlayDisplay : IAlertDisplay, IDisposable
{
    private readonly OverlayController _controller;
    private readonly ILedSource _led;

    /// <param name="controller">Plays one alert at a time on the bar.</param>
    /// <param name="led">Says where the LED is.</param>
    public OverlayDisplay(OverlayController controller, ILedSource led)
    {
        ArgumentNullException.ThrowIfNull(controller);
        ArgumentNullException.ThrowIfNull(led);

        _controller = controller;
        _led = led;
        _led.LedChanged += OnLedChanged;
    }

    /// <inheritdoc />
    public event Action<bool>? LedPresenceChanged;

    /// <inheritdoc />
    public bool LedPresent => _led.Led is not null;

    /// <inheritdoc />
    public Task<bool> ShowAsync(string text, CancellationToken ct) => _controller.ShowAsync(text, ct);

    /// <inheritdoc />
    public void Dispose() => _led.LedChanged -= OnLedChanged;

    // Any change (appears, vanishes, resolution, moves) is told with the presence it leaves.
    private void OnLedChanged(ScreenInfo? screen)
    {
        try
        {
            LedPresenceChanged?.Invoke(screen is not null);
        }
        catch (Exception)
        {
            // The LED watcher raises this on its own thread: a listener's bug must not stop it. The listener reports its own errors.
        }
    }
}
