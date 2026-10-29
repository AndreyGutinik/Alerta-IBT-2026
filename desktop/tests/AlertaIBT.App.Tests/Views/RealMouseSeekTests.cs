using AlertaIBT.App.Views;

namespace AlertaIBT.App.Tests.Views;

// The timeline of the player driven by the real input path (hit testing, mouse capture, routing) in a real, shown window. The owner's bug:
// dragging the timeline did not seek to where the thumb was let go, the thumb snapped back and the music stayed where the press began.
// (Release asked the capture to go before the seek was committed, and the lost capture was taken for a cancelled drag.)
public sealed class RealMouseSeekTests
{
    // The sound card pulls a little sound and the player's position timer ticks a few times: whatever would rewrite the thumb gets its chance.
    private static void TickTimer(RealInputRig rig)
    {
        rig.Listen(0.3);
        rig.Som.Settings.Clock.Advance(TimeSpan.FromSeconds(2));
        RealInputRig.Pump();
    }

    private static double EngineSeconds(RealInputRig rig) => rig.Som.Player.GetStatus().Position.TotalSeconds;

    private static void AssertEverywhereAt(RealInputRig rig, SeekSlider slider, double expected, double tolerance)
    {
        Assert.False(slider.IsDragging);
        Assert.False(rig.Player.IsSeeking);
        Assert.InRange(EngineSeconds(rig), expected - tolerance, expected + tolerance);
        Assert.InRange(slider.Value, expected - tolerance, expected + tolerance);
        Assert.InRange(rig.Player.SeekValue, expected - tolerance, expected + tolerance);
    }

    private static double SecondsAt(SeekSlider slider, double fraction)
    {
        // The thumb's centre sits at fraction of the room the thumb has (the track less its own width).
        return slider.Maximum * fraction;
    }

    [Fact]
    public void Press_at_20_percent_move_to_70_and_release_seeks_to_70_and_the_thumb_stays_there()
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new RealInputRig();
            var slider = rig.Page.SeekSlider;
            Assert.True(slider.IsEnabled);

            var from = rig.At(slider, 0.2, 0.5);
            var to = rig.At(slider, 0.7, 0.5);
            rig.MouseMove(from);
            rig.MouseDown(from);
            Assert.True(slider.IsDragging);
            rig.MouseMove(rig.At(slider, 0.45, 0.5), leftDown: true);
            rig.MouseMove(to, leftDown: true);
            Assert.InRange(rig.Player.SeekValue, SecondsAt(slider, 0.66), SecondsAt(slider, 0.74)); // the thumb follows while it is held
            Assert.InRange(EngineSeconds(rig), 74, 77); // the music has not jumped yet
            rig.MouseUp(to);
            TickTimer(rig);

            AssertEverywhereAt(rig, slider, SecondsAt(slider, 0.7), SecondsAt(slider, 0.04));
        });
    }

    [Fact]
    public void A_click_jumps_to_that_place()
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new RealInputRig();
            var slider = rig.Page.SeekSlider;

            rig.Click(rig.At(slider, 0.3, 0.5));
            TickTimer(rig);

            AssertEverywhereAt(rig, slider, SecondsAt(slider, 0.3), SecondsAt(slider, 0.04));
        });
    }

    [Fact]
    public void A_drag_while_paused_seeks_and_the_paused_thumb_stays_where_it_was_let_go()
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new RealInputRig(playing: false, paused: true);
            var slider = rig.Page.SeekSlider;
            Assert.True(slider.IsEnabled);

            var from = rig.At(slider, 0.2, 0.5);
            var to = rig.At(slider, 0.8, 0.5);
            rig.MouseMove(from);
            rig.MouseDown(from);
            rig.MouseMove(rig.At(slider, 0.5, 0.5), leftDown: true);
            rig.MouseMove(to, leftDown: true);
            rig.MouseUp(to);
            TickTimer(rig);

            AssertEverywhereAt(rig, slider, SecondsAt(slider, 0.8), SecondsAt(slider, 0.04));
            Assert.Equal(AlertaIBT.Core.Player.PlayerState.Paused, rig.Som.Player.GetStatus().State);
        });
    }

    [Fact]
    public void Dragging_past_the_right_end_goes_to_the_end()
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new RealInputRig();
            var slider = rig.Page.SeekSlider;

            var from = rig.At(slider, 0.5, 0.5);
            var beyond = rig.At(slider, 1.0, 0.5);
            beyond.X += 40;
            rig.MouseMove(from);
            rig.MouseDown(from);
            rig.MouseMove(beyond, leftDown: true);
            Assert.Equal(slider.Maximum, rig.Player.SeekValue, 3);
            rig.MouseUp(beyond);
            rig.Listen(0.3);
            RealInputRig.Pump();

            Assert.False(slider.IsDragging);
            Assert.False(rig.Player.IsSeeking);
            Assert.InRange(EngineSeconds(rig), slider.Maximum - 3, slider.Maximum + 1); // the end of the track
        });
    }

    [Fact]
    public void Dragging_past_the_left_end_goes_to_the_start()
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new RealInputRig();
            var slider = rig.Page.SeekSlider;

            var from = rig.At(slider, 0.5, 0.5);
            var beyond = rig.At(slider, 0.0, 0.5);
            beyond.X -= 40;
            rig.MouseMove(from);
            rig.MouseDown(from);
            rig.MouseMove(beyond, leftDown: true);
            Assert.Equal(0, rig.Player.SeekValue, 3);
            rig.MouseUp(beyond);
            TickTimer(rig);

            AssertEverywhereAt(rig, slider, 0, 3);
        });
    }

    [Fact]
    public void Letting_go_outside_the_window_part_of_the_slider_still_seeks_where_the_pointer_was()
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new RealInputRig();
            var slider = rig.Page.SeekSlider;

            var from = rig.At(slider, 0.2, 0.5);
            var to = rig.At(slider, 0.6, 0.5);
            to.Y += 150; // well under the line: the capture keeps the drag
            rig.MouseMove(from);
            rig.MouseDown(from);
            rig.MouseMove(to, leftDown: true);
            rig.MouseUp(to);
            TickTimer(rig);

            AssertEverywhereAt(rig, slider, SecondsAt(slider, 0.6), SecondsAt(slider, 0.04));
        });
    }
}
