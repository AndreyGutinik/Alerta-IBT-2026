using AlertaIBT.Core.Screens;

namespace AlertaIBT.Core.Tests.Screens;

public sealed class LedTrackerTests
{
    private static readonly ScreenInfo Primary = new("p", @"\\.\DISPLAY1", 0, 0, 1920, 1080, true);
    private static readonly ScreenInfo Led = new("led", @"\\.\DISPLAY2", 1920, 0, 2560, 768, false);

    private sealed class Monitors
    {
        public List<ScreenInfo> Screens { get; set; } = [];

        public IReadOnlyList<ScreenInfo> List() => Screens;
    }

    [Fact]
    public void Nothing_is_known_before_the_first_refresh()
    {
        var tracker = new LedTracker(() => [Primary, Led], () => null, testMode: false);

        Assert.Null(tracker.Current);
    }

    [Fact]
    public void The_led_appearing_is_a_change_and_looking_again_is_not()
    {
        var monitors = new Monitors { Screens = [Primary, Led] };
        var tracker = new LedTracker(monitors.List, () => null, false);

        Assert.True(tracker.Refresh());
        Assert.Equal(Led, tracker.Current);
        Assert.False(tracker.Refresh());
    }

    [Fact]
    public void No_led_at_start_is_not_a_change()
    {
        var monitors = new Monitors { Screens = [Primary] };
        var tracker = new LedTracker(monitors.List, () => null, false);

        Assert.False(tracker.Refresh());
        Assert.Null(tracker.Current);
    }

    [Fact]
    public void The_led_switching_off_and_on_are_changes()
    {
        var monitors = new Monitors { Screens = [Primary, Led] };
        var tracker = new LedTracker(monitors.List, () => null, false);
        tracker.Refresh();

        monitors.Screens = [Primary];
        Assert.True(tracker.Refresh());
        Assert.Null(tracker.Current);

        monitors.Screens = [Primary, Led];
        Assert.True(tracker.Refresh());
        Assert.Equal(Led, tracker.Current);
    }

    [Fact]
    public void A_resolution_change_of_the_led_makes_it_vanish()
    {
        var monitors = new Monitors { Screens = [Primary, Led] };
        var tracker = new LedTracker(monitors.List, () => null, false);
        tracker.Refresh();

        monitors.Screens = [Primary, Led with { Width = 1920, Height = 1080 }];

        Assert.True(tracker.Refresh());
        Assert.Null(tracker.Current);
    }

    [Fact]
    public void The_led_moving_to_another_position_is_a_change()
    {
        var monitors = new Monitors { Screens = [Primary, Led] };
        var tracker = new LedTracker(monitors.List, () => null, false);
        tracker.Refresh();

        monitors.Screens = [Primary, Led with { X = -2560 }];

        Assert.True(tracker.Refresh());
        Assert.Equal(-2560, tracker.Current!.X);
    }

    [Fact]
    public void With_two_leds_the_remembered_one_is_chosen()
    {
        var second = Led with { DevicePath = "led2", DeviceName = @"\\.\DISPLAY3", X = 4480 };
        var tracker = new LedTracker(() => [Primary, Led, second], () => "LED2", false);

        tracker.Refresh();

        Assert.Equal(second, tracker.Current);
    }

    [Fact]
    public void Test_mode_takes_the_primary_monitor_whatever_its_size()
    {
        var tracker = new LedTracker(() => [Led, Primary], () => null, testMode: true);

        Assert.True(tracker.Refresh());

        Assert.Equal(Primary, tracker.Current);
        Assert.True(tracker.TestMode);
    }

    [Fact]
    public void Test_mode_falls_back_to_the_first_monitor_when_none_is_primary_and_to_nothing_when_none_exists()
    {
        var notPrimary = Led with { };
        var tracker = new LedTracker(() => [notPrimary], () => null, true);
        tracker.Refresh();
        Assert.Equal(notPrimary, tracker.Current);

        var empty = new LedTracker(() => [], () => null, true);
        Assert.False(empty.Refresh());
        Assert.Null(empty.Current);
    }
}
