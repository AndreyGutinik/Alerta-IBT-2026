using AlertaIBT.Core.Overlay;

namespace AlertaIBT.Core.Tests.Overlay;

public class BarTimelineTests
{
    private static readonly TimeSpan Slide = TimeSpan.FromMilliseconds(320);

    // 2560 px screen, 1000 px of text, 90 px/s: the numbers of the spec.
    private static BarTimeline Make(double screen = 2560, double text = 1000, double speed = 90, TimeSpan? slide = null) =>
        new(screen, text, speed, slide ?? Slide);

    private static TimeSpan ScrollDuration => TimeSpan.FromSeconds(3560.0 / 90.0);

    [Fact]
    public void Total_is_slide_in_plus_the_scroll_plus_slide_out()
    {
        Assert.Equal(Slide + ScrollDuration + Slide, Make().Total);
        Assert.InRange(Make().Total.TotalSeconds, 40.19, 40.20);
    }

    [Fact]
    public void At_zero_the_bar_is_hidden_and_about_to_slide_in()
    {
        var frame = Make().At(TimeSpan.Zero);

        Assert.Equal(BarPhase.In, frame.Phase);
        Assert.Equal(1.0, frame.Offset);
        Assert.Equal(0.0, frame.Opacity);
        Assert.Equal(2560.0, frame.TextX);
    }

    [Fact]
    public void The_slide_in_is_an_ease_out_cubic()
    {
        var timeline = Make();

        var half = timeline.At(TimeSpan.FromMilliseconds(160));
        Assert.Equal(BarPhase.In, half.Phase);
        Assert.True(half.Offset < 0.2, $"offset at half the slide was {half.Offset}");
        Assert.Equal(0.125, half.Offset, 6);
        Assert.Equal(0.875, half.Opacity, 6);

        var quarter = timeline.At(TimeSpan.FromMilliseconds(80));
        Assert.Equal(Math.Pow(0.75, 3), quarter.Offset, 6);
    }

    [Fact]
    public void The_text_stays_off_screen_on_the_right_while_the_bar_slides_in()
    {
        var timeline = Make();

        for (var ms = 0; ms < 320; ms += 20)
        {
            Assert.Equal(2560.0, timeline.At(TimeSpan.FromMilliseconds(ms)).TextX);
        }
    }

    [Fact]
    public void When_the_slide_in_ends_the_scroll_starts_with_the_bar_fully_visible()
    {
        var frame = Make().At(Slide);

        Assert.Equal(BarPhase.Scroll, frame.Phase);
        Assert.Equal(2560.0, frame.TextX);
        Assert.Equal(0.0, frame.Offset);
        Assert.Equal(1.0, frame.Opacity);
    }

    [Fact]
    public void Just_before_the_slide_in_ends_it_is_still_the_slide_in()
    {
        var frame = Make().At(Slide - TimeSpan.FromTicks(1));

        Assert.Equal(BarPhase.In, frame.Phase);
        Assert.InRange(frame.Offset, 0.0, 1e-6);
    }

    [Fact]
    public void The_text_moves_left_at_the_given_speed_computed_from_the_time()
    {
        var timeline = Make();

        Assert.Equal(2560.0 - 90.0, timeline.At(Slide + TimeSpan.FromSeconds(1)).TextX, 6);
        Assert.Equal(2560.0 - 900.0, timeline.At(Slide + TimeSpan.FromSeconds(10)).TextX, 6);
        Assert.Equal(2560.0 - 90.0 * 2.5, timeline.At(Slide + TimeSpan.FromSeconds(2.5)).TextX, 6);
        Assert.All(
            new[] { 1, 10, 25 },
            seconds => Assert.Equal(BarPhase.Scroll, timeline.At(Slide + TimeSpan.FromSeconds(seconds)).Phase));
    }

    [Fact]
    public void At_the_end_of_the_scroll_the_text_has_just_left_the_screen()
    {
        var timeline = Make();

        var end = timeline.At(Slide + ScrollDuration);
        Assert.Equal(-1000.0, end.TextX, 6);
        Assert.Equal(BarPhase.Out, end.Phase);

        var justBefore = timeline.At(Slide + ScrollDuration - TimeSpan.FromMilliseconds(1));
        Assert.Equal(BarPhase.Scroll, justBefore.Phase);
        Assert.InRange(justBefore.TextX, -1000.0, -999.9);
    }

    [Fact]
    public void The_slide_out_is_an_ease_out_that_hides_the_bar()
    {
        var timeline = Make();
        var outStart = Slide + ScrollDuration;

        var start = timeline.At(outStart);
        Assert.Equal(0.0, start.Offset, 6);
        Assert.Equal(1.0, start.Opacity, 6);

        var half = timeline.At(outStart + TimeSpan.FromMilliseconds(160));
        Assert.Equal(BarPhase.Out, half.Phase);
        Assert.Equal(0.875, half.Offset, 6);
        Assert.Equal(0.125, half.Opacity, 6);
        Assert.Equal(-1000.0, half.TextX);

        var last = timeline.At(timeline.Total - TimeSpan.FromTicks(1));
        Assert.Equal(BarPhase.Out, last.Phase);
        Assert.InRange(last.Offset, 1 - 1e-6, 1.0);
        Assert.InRange(last.Opacity, 0.0, 1e-6);
    }

    [Fact]
    public void After_Total_it_is_done_and_hidden_forever()
    {
        var timeline = Make();

        foreach (var late in new[] { TimeSpan.Zero, TimeSpan.FromSeconds(1), TimeSpan.FromHours(5) })
        {
            var frame = timeline.At(timeline.Total + late);
            Assert.Equal(BarPhase.Done, frame.Phase);
            Assert.Equal(1.0, frame.Offset);
            Assert.Equal(0.0, frame.Opacity);
            Assert.Equal(-1000.0, frame.TextX);
        }
    }

    [Fact]
    public void A_time_before_zero_is_the_first_frame()
    {
        var timeline = Make();

        Assert.Equal(timeline.At(TimeSpan.Zero), timeline.At(TimeSpan.FromSeconds(-3)));
    }

    [Fact]
    public void Sweeping_the_whole_timeline_keeps_every_value_in_range_and_moves_forward_only()
    {
        var timeline = Make();
        var previous = timeline.At(TimeSpan.Zero);
        var phases = new List<BarPhase> { previous.Phase };

        for (var t = TimeSpan.Zero; t <= timeline.Total + TimeSpan.FromSeconds(1); t += TimeSpan.FromMilliseconds(7))
        {
            var frame = timeline.At(t);

            Assert.InRange(frame.Offset, 0.0, 1.0);
            Assert.InRange(frame.Opacity, 0.0, 1.0);
            Assert.InRange(frame.TextX, -1000.0, 2560.0);
            Assert.True(frame.TextX <= previous.TextX, $"the text moved right at {t}");
            if (frame.Phase != phases[^1])
            {
                phases.Add(frame.Phase);
            }

            previous = frame;
        }

        Assert.Equal([BarPhase.In, BarPhase.Scroll, BarPhase.Out, BarPhase.Done], phases);
    }

    [Fact]
    public void At_is_a_pure_function_of_the_time_so_a_restart_after_the_LED_returns_is_a_new_clean_run()
    {
        var timeline = Make();
        var mid = Slide + TimeSpan.FromSeconds(12);

        Assert.Equal(timeline.At(mid), timeline.At(mid));
        Assert.Equal(timeline.At(mid), Make().At(mid));

        // The LED vanished mid-scroll: the same alert runs again from the start with a fresh clock.
        var restarted = Make().At(TimeSpan.Zero);
        Assert.Equal(BarPhase.In, restarted.Phase);
        Assert.Equal(2560.0, restarted.TextX);
        Assert.Equal(0.0, restarted.Opacity);
    }

    [Fact]
    public void A_longer_text_or_a_faster_speed_changes_the_duration()
    {
        Assert.Equal(Slide + TimeSpan.FromSeconds((2560 + 3000) / 90.0) + Slide, Make(text: 3000).Total);
        Assert.Equal(Slide + TimeSpan.FromSeconds(3560 / 300.0) + Slide, Make(speed: 300).Total);
        Assert.Equal(Slide + TimeSpan.FromSeconds(2560 / 90.0) + Slide, Make(text: 0).Total);
    }

    [Fact]
    public void A_zero_slide_goes_straight_to_the_scroll_and_to_done()
    {
        var timeline = Make(slide: TimeSpan.Zero);

        Assert.Equal(ScrollDuration, timeline.Total);
        var start = timeline.At(TimeSpan.Zero);
        Assert.Equal(BarPhase.Scroll, start.Phase);
        Assert.Equal(0.0, start.Offset);
        Assert.Equal(1.0, start.Opacity);
        Assert.Equal(BarPhase.Done, timeline.At(timeline.Total).Phase);
    }

    [Theory]
    [InlineData(0, 100, 90, 320)]
    [InlineData(-1, 100, 90, 320)]
    [InlineData(2560, -1, 90, 320)]
    [InlineData(2560, 100, 0, 320)]
    [InlineData(2560, 100, -5, 320)]
    [InlineData(2560, 100, 90, -1)]
    [InlineData(double.NaN, 100, 90, 320)]
    [InlineData(2560, double.PositiveInfinity, 90, 320)]
    [InlineData(2560, 100, double.NaN, 320)]
    public void Impossible_numbers_are_refused(double screen, double text, double speed, int slideMs)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BarTimeline(screen, text, speed, TimeSpan.FromMilliseconds(slideMs)));
    }

    [Fact]
    public void A_speed_so_slow_the_timeline_cannot_be_represented_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BarTimeline(2560, 1000, 1e-12, Slide));
    }
}
