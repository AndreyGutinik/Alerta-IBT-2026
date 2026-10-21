using AlertaIBT.App.Overlay;
using AlertaIBT.App.Services;
using AlertaIBT.App.Tests.Overlay;
using AlertaIBT.Core.Screens;
using AlertaIBT.Core.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace AlertaIBT.App.Tests.Services;

public sealed class OverlayDisplayTests : IDisposable
{
    private static readonly ScreenInfo Led = new("led", @"\\.\DISPLAY2", 1920, 0, 2560, 768, false);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "alerta-ibt-disp-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new();
    private readonly FakeFrames _frames = new();
    private readonly FakeLed _led = new(Led);
    private readonly QueueUi _ui = new();
    private readonly SettingsService _settings;
    private readonly OverlayController _controller;
    private readonly OverlayDisplay _display;

    public OverlayDisplayTests()
    {
        _settings = new SettingsService(new SettingsStore(Path.Combine(_dir, "settings.json")), _clock);
        _controller = new OverlayController(_settings, _led, () => new FakeSurface(), _frames, _ui, NullLogger<OverlayController>.Instance, _clock);
        _display = new OverlayDisplay(_controller, _led);
    }

    public void Dispose()
    {
        _display.Dispose();
        _controller.Dispose();
        _settings.Dispose();
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void Led_presence_follows_the_led_source()
    {
        Assert.True(_display.LedPresent);

        _led.Raise(null);

        Assert.False(_display.LedPresent);
    }

    [Fact]
    public void Every_change_of_the_led_is_told_with_the_presence_it_leaves()
    {
        var seen = new List<bool>();
        _display.LedPresenceChanged += seen.Add;

        _led.Raise(null);
        _led.Raise(Led);
        _led.Raise(Led with { X = 0 }); // moved: still present

        Assert.Equal([false, true, true], seen);
    }

    [Fact]
    public void A_listener_that_throws_does_not_break_the_led_source()
    {
        _display.LedPresenceChanged += _ => throw new InvalidOperationException("listener bug");

        _led.Raise(null); // would propagate into the LED watcher's thread

        Assert.False(_display.LedPresent);
    }

    [Fact]
    public async Task Showing_goes_through_the_controller_and_ends_when_the_text_has_crossed()
    {
        var task = _display.ShowAsync("GOL BRANCO", CancellationToken.None);
        Assert.True(_controller.IsShowing);

        var origin = TimeSpan.FromSeconds(10);
        _frames.Push(origin);
        _frames.Push(origin + TimeSpan.FromMinutes(5));

        Assert.True(await task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Cancelling_ends_the_pass_with_false_so_the_service_can_put_the_alert_back()
    {
        using var cts = new CancellationTokenSource();
        var task = _display.ShowAsync("GOL BRANCO", cts.Token);

        await cts.CancelAsync();
        _ui.Drain();

        Assert.False(await task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task The_led_vanishing_ends_the_pass_with_false()
    {
        var task = _display.ShowAsync("GOL BRANCO", CancellationToken.None);

        _led.Raise(null);
        _ui.Drain();

        Assert.False(await task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    }
}
