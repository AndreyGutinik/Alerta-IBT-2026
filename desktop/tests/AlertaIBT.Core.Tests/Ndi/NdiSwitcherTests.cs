using AlertaIBT.Core.Ndi;
using AlertaIBT.Core.Settings;
using Microsoft.Extensions.Time.Testing;

namespace AlertaIBT.Core.Tests.Ndi;

public sealed class NdiSwitcherTests : IDisposable
{
    private readonly FakeTimeProvider _clock = new();
    private readonly List<FakeCameraReceiver> _receivers = [];
    private NdiSettings _settings = NdiTestSettings.With();
    private readonly NdiSwitcher _switcher;
    private readonly List<bool> _visibility = [];
    private readonly List<string> _errors = [];
    private int _programFrames;
    private double _lastPosition = -1;

    public NdiSwitcherTests()
    {
        _switcher = new NdiSwitcher(
            _ =>
            {
                var receiver = new FakeCameraReceiver(_clock);
                _receivers.Add(receiver);
                return receiver;
            },
            () => _settings,
            _clock);
        _switcher.WindowVisibleChanged += visible => _visibility.Add(visible);
        _switcher.Error += message => _errors.Add(message);
        _switcher.ProgramFrame += (in NdiFrameView frame, double position) =>
        {
            _programFrames++;
            _lastPosition = position;
        };
    }

    private FakeCameraReceiver A => _receivers[0];

    private FakeCameraReceiver B => _receivers[1];

    public void Dispose() => _switcher.Dispose();

    [Fact]
    public void Starts_with_the_layer_off_and_nothing_selected()
    {
        var status = _switcher.GetStatus();

        Assert.False(status.LayerOn);
        Assert.False(status.WindowVisible);
        Assert.Null(status.PreviewSlot);
        Assert.Null(status.ProgramSlot);
    }

    [Fact]
    public void Selecting_a_preview_reconnects_only_the_preview_receiver()
    {
        _switcher.SelectPreview(1);

        Assert.Empty(A.Connects);
        var preview = Assert.Single(B.Connects);
        Assert.Equal(new NdiSourceRef("PC (Cam2)", null), preview);
        Assert.Equal(1, _switcher.GetStatus().PreviewSlot);
        Assert.Equal("Cam2", _switcher.GetStatus().PreviewName);
    }

    [Fact]
    public void Selecting_the_same_preview_twice_does_not_reconnect()
    {
        _switcher.SelectPreview(1);
        _switcher.SelectPreview(1);

        Assert.Single(B.Connects);
    }

    [Fact]
    public void Take_swaps_and_the_old_program_stays_connected_as_the_preview()
    {
        _switcher.SelectPreview(0);
        B.Push();
        Assert.True(_switcher.Take().Success);
        Assert.Equal(0, _switcher.GetStatus().ProgramSlot);

        _switcher.SelectPreview(1); // reconnects the lane that is the preview now (A), never the program (B)
        A.Push();
        Assert.True(_switcher.Take().Success);

        Assert.Equal(1, _switcher.GetStatus().ProgramSlot);
        Assert.Equal(0, _switcher.GetStatus().PreviewSlot);
        Assert.Single(B.Connects); // the first program receiver was connected once and never touched again
        Assert.Equal(NdiConnectionState.Connected, B.State);
    }

    [Fact]
    public void Take_after_a_cut_leaves_the_old_program_receiver_connected_without_reconnecting()
    {
        _switcher.SelectPreview(0);
        B.Push();
        _switcher.Take();
        _switcher.SelectPreview(1);
        A.Push();
        _switcher.Take();

        // Now B (cam 1) is the preview again: still connected, same source, no extra Connect.
        Assert.Single(B.Connects);
        Assert.Equal(NdiConnectionState.Connected, B.State);
    }

    [Fact]
    public void Selecting_the_camera_that_is_on_air_is_a_no_op()
    {
        _switcher.SelectPreview(0);
        B.Push();
        _switcher.Take();
        var connects = A.Connects.Count + B.Connects.Count;

        var result = _switcher.SelectPreview(0);

        Assert.True(result.Success);
        Assert.Equal(connects, A.Connects.Count + B.Connects.Count);
        Assert.Null(_switcher.GetStatus().PreviewSlot);
    }

    [Fact]
    public void Take_before_the_first_frame_waits_then_cuts()
    {
        _switcher.SelectPreview(0);

        var result = _switcher.Take();

        Assert.True(result.Success);
        Assert.True(_switcher.GetStatus().TakePending);
        Assert.Null(_switcher.GetStatus().ProgramSlot);

        B.Push(); // the first frame arrives: the cut happens by itself, and that very frame goes on air
        Assert.False(_switcher.GetStatus().TakePending);
        Assert.Equal(0, _switcher.GetStatus().ProgramSlot);
        Assert.Equal(1, _programFrames);
    }

    [Fact]
    public void Take_that_never_gets_a_frame_reports_an_error_and_cuts_nothing()
    {
        _switcher.SelectPreview(0);
        _switcher.Take();

        _clock.Advance(NdiConstants.TakeTimeout + TimeSpan.FromSeconds(1));

        Assert.Single(_errors);
        Assert.False(_switcher.GetStatus().TakePending);
        Assert.Null(_switcher.GetStatus().ProgramSlot);
        B.Push();
        Assert.Null(_switcher.GetStatus().ProgramSlot); // a late frame must not cut anymore
    }

    [Fact]
    public void Take_without_a_preview_is_an_error()
    {
        var result = _switcher.Take();

        Assert.False(result.Success);
        Assert.Single(_errors);
    }

    [Fact]
    public void Selecting_a_slot_without_a_source_is_an_error()
    {
        var result = _switcher.SelectPreview(5);

        Assert.False(result.Success);
        Assert.Empty(B.Connects);
    }

    [Fact]
    public void Selecting_another_preview_cancels_a_pending_take()
    {
        _switcher.SelectPreview(0);
        _switcher.Take();
        _switcher.SelectPreview(1);

        B.Push();

        Assert.Null(_switcher.GetStatus().ProgramSlot);
        Assert.False(_switcher.GetStatus().TakePending);
    }

    [Fact]
    public void Layer_on_with_nothing_on_air_stays_hidden()
    {
        _switcher.SetLayer(true);

        Assert.True(_switcher.LayerOn);
        Assert.False(_switcher.WindowVisible);
    }

    [Fact]
    public void Layer_on_and_frames_make_the_window_visible_and_off_hides_it()
    {
        _switcher.SelectPreview(0);
        B.Push();
        _switcher.Take();
        _switcher.SetLayer(true);
        Assert.True(_switcher.WindowVisible);

        _switcher.SetLayer(false);

        Assert.False(_switcher.WindowVisible);
        Assert.Equal([true, false], _visibility);
    }

    [Fact]
    public void A_silent_source_freezes_for_five_seconds_then_hides_and_returns_with_frames()
    {
        _switcher.SelectPreview(0);
        B.Push();
        _switcher.Take();
        _switcher.SetLayer(true);

        _clock.Advance(TimeSpan.FromSeconds(4.5));
        Assert.True(_switcher.WindowVisible); // still frozen on the last picture

        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.False(_switcher.WindowVisible); // Holyrics shows normally

        B.Push();
        Assert.True(_switcher.WindowVisible); // frames are back and the layer is still on
    }

    [Fact]
    public void Frames_returning_do_not_show_the_window_when_the_layer_is_off()
    {
        _switcher.SelectPreview(0);
        B.Push();
        _switcher.Take();
        _switcher.SetLayer(true);
        _clock.Advance(TimeSpan.FromSeconds(6));
        _switcher.SetLayer(false);

        B.Push();

        Assert.False(_switcher.WindowVisible);
    }

    [Fact]
    public void Test_pattern_shows_the_window_even_with_the_layer_off()
    {
        _switcher.SetTestPattern(true);
        Assert.True(_switcher.WindowVisible);

        _switcher.SetTestPattern(false);
        Assert.False(_switcher.WindowVisible);
    }

    [Fact]
    public void Program_frames_carry_the_vertical_position_of_their_camera()
    {
        _switcher.SelectPreview(2);
        B.Push();
        _switcher.Take();

        B.Push();

        Assert.Equal(30, _lastPosition);
    }

    [Fact]
    public void Preview_frames_are_not_sent_to_the_program_sink()
    {
        _switcher.SelectPreview(0);
        B.Push();

        Assert.Equal(0, _programFrames);
    }

    [Fact]
    public void Changing_the_source_of_the_preview_slot_reconnects_it()
    {
        _switcher.SelectPreview(0);
        _settings = _settings with { Cameras = _settings.Cameras.Select((c, i) => i == 0 ? c with { Source = "192.168.0.9" } : c).ToList() };

        _switcher.ApplySettings();

        Assert.Equal(new NdiSourceRef(null, "192.168.0.9"), B.Connects[^1]);
    }

    [Fact]
    public void Thumbnails_exist_per_lane_and_keep_the_last_picture_with_the_camera()
    {
        _switcher.SelectPreview(0);
        B.Push(shade: 200);
        Assert.NotNull(_switcher.GetThumbnail(0));
        Assert.NotNull(_switcher.GetPreviewThumbnail());

        _switcher.Take();
        _switcher.SelectPreview(1);

        Assert.NotNull(_switcher.GetThumbnail(0)); // now on air: the live lane picture
        Assert.NotNull(_switcher.GetProgramThumbnail());
    }

    [Fact]
    public void Disposing_disposes_both_receivers()
    {
        _switcher.Dispose();

        Assert.True(A.Disposed);
        Assert.True(B.Disposed);
    }
}
