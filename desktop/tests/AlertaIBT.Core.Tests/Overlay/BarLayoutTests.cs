using AlertaIBT.Core.Overlay;
using AlertaIBT.Core.Screens;
using AlertaIBT.Core.Settings;

namespace AlertaIBT.Core.Tests.Overlay;

public sealed class BarLayoutTests
{
    private static readonly ScreenInfo Led = new("led", @"\\.\DISPLAY2", 1920, 0, 2560, 768, false);

    [Fact]
    public void The_defaults_put_a_56_px_bar_at_the_top_of_the_led_with_a_36_px_font()
    {
        var layout = BarLayout.Compute(Led, new BarSettings(), 1.0);

        Assert.Equal(1920, layout.WindowX);
        Assert.Equal(0, layout.WindowY);
        Assert.Equal(2560, layout.WindowWidth);
        Assert.Equal(56, layout.WindowHeight);
        Assert.Equal(56, layout.BarHeight);
        Assert.Equal(36, layout.FontSize); // 64% of 56 = 35.84, a whole pixel
        Assert.Equal(24, layout.PaddingX);
        Assert.True(layout.AtTop);
    }

    [Fact]
    public void A_bar_at_the_bottom_sits_on_the_last_rows_of_the_screen()
    {
        var screen = Led with { Y = 100 };

        var layout = BarLayout.Compute(screen, new BarSettings { Position = "bottom", Height = 60 }, 1.0);

        Assert.Equal(100 + 768 - 60, layout.WindowY);
        Assert.False(layout.AtTop);
    }

    [Fact]
    public void The_position_is_read_ignoring_case_and_anything_unknown_means_top()
    {
        Assert.False(BarLayout.Compute(Led, new BarSettings { Position = "BOTTOM" }, 1).AtTop);
        Assert.True(BarLayout.Compute(Led, new BarSettings { Position = "middle" }, 1).AtTop);
    }

    [Fact]
    public void An_explicit_font_size_wins_over_the_automatic_one()
    {
        var layout = BarLayout.Compute(Led, new BarSettings { Height = 100, FontSize = 40 }, 1.0);

        Assert.Equal(40, layout.FontSize);
    }

    [Fact]
    public void The_window_stays_in_physical_pixels_and_the_inner_sizes_follow_the_monitor_scale()
    {
        var layout = BarLayout.Compute(Led, new BarSettings { Height = 60, PaddingX = 30, FontSize = 48 }, 1.5);

        Assert.Equal(2560, layout.WindowWidth);
        Assert.Equal(60, layout.WindowHeight);
        Assert.Equal(40, layout.BarHeight, 6);
        Assert.Equal(32, layout.FontSize, 6);
        Assert.Equal(20, layout.PaddingX, 6);
        Assert.Equal(1.5, layout.DpiScale);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-2.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void A_silly_scale_counts_as_one(double scale)
    {
        Assert.Equal(1.0, BarLayout.Compute(Led, new BarSettings(), scale).DpiScale);
    }

    [Fact]
    public void The_bar_is_never_taller_than_the_screen()
    {
        var small = Led with { Height = 40 };

        Assert.Equal(40, BarLayout.Compute(small, new BarSettings { Height = 120 }, 1).WindowHeight);
    }

    [Theory]
    [InlineData(true, 0.0, 0.0)]
    [InlineData(true, 0.5, -28.0)]
    [InlineData(true, 1.0, -56.0)]
    [InlineData(false, 1.0, 56.0)]
    [InlineData(false, 0.25, 14.0)]
    [InlineData(true, 1.7, -56.0)]
    [InlineData(true, -1.0, 0.0)]
    public void The_slide_goes_up_for_a_top_bar_and_down_for_a_bottom_bar_and_is_held_in_range(bool atTop, double offset, double expected)
    {
        var layout = BarLayout.Compute(Led, new BarSettings { Position = atTop ? "top" : "bottom" }, 1);

        Assert.Equal(expected, layout.SlideDistance(offset), 6);
    }

    [Fact]
    public void The_example_text_is_an_upper_case_alert_in_the_official_format()
    {
        Assert.Equal(BarPreview.SampleText, BarPreview.SampleText.ToUpperInvariant());
        Assert.Contains(" | PLACA: ", BarPreview.SampleText, StringComparison.Ordinal);
        Assert.Equal(TimeSpan.FromSeconds(3), BarPreview.Duration);
    }

    [Theory]
    [InlineData(2560, 900, 320, 3)]
    [InlineData(1920, 100, 100, 3)]
    [InlineData(2560, 0, 1500, 3)]
    public void The_preview_speed_makes_the_whole_pass_last_the_requested_time(double screen, double text, int slideMs, int seconds)
    {
        var slide = TimeSpan.FromMilliseconds(slideMs);
        var total = TimeSpan.FromSeconds(seconds);

        var speed = BarPreview.SpeedFor(screen, text, slide, total);
        var timeline = new BarTimeline(screen, text, speed, slide);

        // A slide longer than half the time leaves the scroll a minimum of half a second.
        var expected = total - (2 * slide) < TimeSpan.FromSeconds(0.5) ? TimeSpan.FromSeconds(0.5) + (2 * slide) : total;
        Assert.Equal(expected.TotalSeconds, timeline.Total.TotalSeconds, 3);
    }
}
