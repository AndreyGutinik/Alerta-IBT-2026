using AlertaIBT.Core.Screens;

namespace AlertaIBT.Core.Tests.Screens;

public class WindowMonitorChooserTests
{
    private static readonly ScreenInfo Desk = new(@"\\?\DISPLAY#DELL#1", @"\\.\DISPLAY1", 0, 0, 1920, 1080, true);
    private static readonly ScreenInfo Led = new(@"\\?\DISPLAY#LED#2", @"\\.\DISPLAY2", 1920, 0, 2560, 768, false);
    private static readonly ScreenInfo Side = new(@"\\?\DISPLAY#HP#3", @"\\.\DISPLAY3", 4480, 0, 1920, 1080, false);

    // ---- mouse on the LED ----

    [Fact]
    public void Mouse_on_the_LED_goes_to_the_primary_monitor()
    {
        var chosen = WindowMonitorChooser.Choose([Desk, Led], Led, 3000, 300);

        Assert.Same(Desk, chosen);
    }

    [Fact]
    public void Mouse_on_the_LED_when_the_LED_is_the_primary_goes_to_another_monitor()
    {
        var primaryLed = Led with { IsPrimary = true, X = 0 };
        var other = Desk with { IsPrimary = false, X = 2560 };

        var chosen = WindowMonitorChooser.Choose([primaryLed, other], primaryLed, 100, 100);

        Assert.Same(other, chosen);
    }

    [Fact]
    public void Mouse_on_the_LED_with_three_monitors_prefers_the_primary_over_the_others()
    {
        var chosen = WindowMonitorChooser.Choose([Side, Led, Desk], Led, 2000, 100);

        Assert.Same(Desk, chosen);
    }

    // ---- single monitor ----

    [Fact]
    public void A_single_monitor_that_is_the_LED_is_still_used()
    {
        var lone = Led with { IsPrimary = true, X = 0 };

        var chosen = WindowMonitorChooser.Choose([lone], lone, 500, 500);

        Assert.Same(lone, chosen);
    }

    [Fact]
    public void A_single_ordinary_monitor_is_used()
    {
        Assert.Same(Desk, WindowMonitorChooser.Choose([Desk], null, 10, 10));
        Assert.Same(Desk, WindowMonitorChooser.Choose([Desk], null, -5000, 9000)); // mouse outside everything
    }

    // ---- LED absent ----

    [Fact]
    public void Without_an_LED_the_monitor_under_the_mouse_is_used()
    {
        Assert.Same(Side, WindowMonitorChooser.Choose([Desk, Side], null, 5000, 200));
        Assert.Same(Desk, WindowMonitorChooser.Choose([Desk, Side], null, 100, 200));
    }

    [Fact]
    public void A_2560x768_monitor_is_not_avoided_when_it_is_not_the_known_LED()
    {
        var lookalike = new ScreenInfo(string.Empty, @"\\.\DISPLAY9", 1920, 0, 2560, 768, false);

        Assert.Same(lookalike, WindowMonitorChooser.Choose([Desk, lookalike], null, 3000, 100));
    }

    // ---- two monitors, mouse elsewhere ----

    [Fact]
    public void Mouse_on_the_ordinary_monitor_stays_there()
    {
        Assert.Same(Desk, WindowMonitorChooser.Choose([Desk, Led], Led, 800, 500));
    }

    [Fact]
    public void Mouse_outside_every_monitor_counts_as_the_nearest_one()
    {
        // Just below the LED (768 tall): nearest to the LED, so avoided, so the primary.
        Assert.Same(Desk, WindowMonitorChooser.Choose([Desk, Led], Led, 3000, 900));
        // Far right of the third monitor: nearest is that monitor.
        Assert.Same(Side, WindowMonitorChooser.Choose([Desk, Led, Side], Led, 9000, 100));
    }

    [Fact]
    public void The_LED_is_recognized_by_its_name_and_bounds_not_by_reference()
    {
        var sameLed = Led with { };

        Assert.Same(Desk, WindowMonitorChooser.Choose([Desk, sameLed], Led, 3000, 100));
    }

    [Fact]
    public void No_monitors_gives_null()
    {
        Assert.Null(WindowMonitorChooser.Choose([], null, 0, 0));
    }

    // ---- is a rectangle on the LED ----

    [Theory]
    [InlineData(2000, 100, 3000, 700, true)]
    [InlineData(100, 100, 900, 700, false)]
    [InlineData(1800, 100, 1925, 700, true)] // 5 px inside
    [InlineData(1800, 100, 1920, 700, false)] // touches the edge only
    [InlineData(2000, 800, 3000, 1000, false)] // below the LED
    public void OverlapsLed_tells_whether_a_rectangle_reaches_the_LED(int left, int top, int right, int bottom, bool expected)
    {
        Assert.Equal(expected, WindowMonitorChooser.OverlapsLed(Led, left, top, right, bottom));
    }

    [Fact]
    public void OverlapsLed_without_an_LED_is_false()
    {
        Assert.False(WindowMonitorChooser.OverlapsLed(null, 0, 0, 5000, 5000));
    }
}
