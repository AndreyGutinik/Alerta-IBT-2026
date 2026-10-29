using AlertaIBT.Core.Audio;
using AlertaIBT.Core.Player;
using AlertaIBT.Core.Settings;

namespace AlertaIBT.Core.Tests.Player;

// Changing the player output while it plays moves the sound there at once: same position, one 150 ms micro-fade on each side, never a stop or a restart.
public sealed class OutputSwitchTests : IDisposable
{
    private readonly PlayerRig _rig = new();

    public void Dispose() => _rig.Dispose();

    private MusicPlayer Player => _rig.Player;

    private FakePlayerBackend Backend => _rig.Backend;

    private void ChooseWing()
    {
        _rig.PlugWing34();
        _rig.Change(p => p with { OutputDeviceId = PlayerRig.Wing34, OutputDeviceName = "WING-USB (Out 3-4)" });
    }

    private void Settle() => _rig.Clock.Advance(MusicPlayer.OutputMoveDelay);

    [Fact]
    public void Choosing_another_output_while_playing_moves_the_sound_there_at_once()
    {
        Player.Play();
        Backend.Outputs[0].PumpSeconds(0.5);

        ChooseWing();
        Settle();

        Assert.Equal(2, Backend.Outputs.Count);
        Assert.Equal(PlayerRig.Wing34, Backend.Output.RequestedId);
        Assert.Equal(PlayerState.Playing, Player.GetStatus().State);
        Assert.Equal("Sa\U000000EDda do player: WING-USB (Out 3-4)", Player.GetStatus().Output.Text);
        var heard = Backend.Output.Pump(4800);
        Assert.Contains(heard, sample => sample > 0.1f); // sound is coming out of the new device already
    }

    [Fact]
    public void The_track_continues_from_the_same_position_within_a_small_tolerance()
    {
        Player.Play();
        Backend.Outputs[0].PumpSeconds(3.0);
        var before = Player.GetStatus().Position.TotalSeconds;
        var path = Player.GetStatus().Track!.Path;

        ChooseWing();
        Settle();

        var after = Player.GetStatus();
        Assert.Equal(path, after.Track!.Path); // the same track, no restart and no skip to another
        Assert.InRange(after.Position.TotalSeconds, before - 0.1, before + 0.5);
        Assert.True(after.Position.TotalSeconds > 2.5, "not back at the start");
        Assert.True(Backend.Tracks.Last(t => t.Path == path).Position.TotalSeconds > 2.5);
    }

    [Fact]
    public void There_is_one_micro_fade_in_on_the_new_output_and_one_fade_out_on_the_old_one_and_then_the_old_one_closes()
    {
        Player.Play();
        Backend.Outputs[0].PumpSeconds(0.5);

        ChooseWing();
        Settle();

        // The new output rises over 150 ms (a ramp, never a jump to full).
        var fresh = Backend.Output.Pump(7200); // 150 ms at 48 kHz
        Assert.True(fresh[0] < 0.02f, "starts from silence");
        Assert.True(fresh[3600 * 2] > 0.1f && fresh[3600 * 2] < 0.4f, "about 75 ms in it is partway");
        Assert.InRange(Backend.Output.Pump(480)[^2], 0.49f, 0.51f); // then full level
        Assert.False(Backend.Outputs[0].Disposed); // the old one is still fading
        // The old output falls over the same 150 ms and then closes.
        var old = Backend.Outputs[0].Pump(7200);
        Assert.True(old[0] > 0.3f, "starts from full");
        Assert.True(old[^2] < 0.02f, "ends in silence");
        Assert.True(Backend.Outputs[0].Disposed);
    }

    [Fact]
    public void A_device_that_is_unplugged_falls_back_to_the_default_with_the_status_saying_so()
    {
        ChooseWing();
        Player.Play();
        Assert.Equal(PlayerRig.Wing34, Backend.Output.RequestedId);
        Backend.Outputs[0].PumpSeconds(1.0);

        _rig.Audio.Devices.RemoveAll(d => d.Id == PlayerRig.Wing34);
        _rig.Audio.RaiseDevicesChanged();
        Settle();

        Assert.Equal(2, Backend.Outputs.Count);
        Assert.Equal(PlayerRig.Speakers, Backend.Output.RequestedId);
        Assert.Equal(PlayerState.Playing, Player.GetStatus().State);
        Assert.Equal(OutputChoiceKind.Default, Player.GetStatus().Output.Kind);
        Assert.Contains("desconectado", Player.GetStatus().Output.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Rapid_changes_coalesce_to_the_last_one_with_a_single_move()
    {
        Player.Play();
        Backend.Outputs[0].PumpSeconds(0.2);
        _rig.PlugWing34();
        _rig.Audio.Devices.Add(new AudioDevice("{0.0.0.00000000}.{other}", "Outro (USB)"));

        _rig.Change(p => p with { OutputDeviceId = PlayerRig.Wing34, OutputDeviceName = "WING-USB (Out 3-4)" });
        _rig.Clock.Advance(TimeSpan.FromMilliseconds(10));
        _rig.Change(p => p with { OutputDeviceId = "{0.0.0.00000000}.{other}", OutputDeviceName = "Outro (USB)" });
        _rig.Clock.Advance(TimeSpan.FromMilliseconds(10));
        _rig.Change(p => p with { OutputDeviceId = PlayerRig.Speakers, OutputDeviceName = "Alto-falantes (Realtek)" });
        Settle();

        Assert.Single(Backend.Outputs); // it ended where it started: nothing opened at all

        _rig.Change(p => p with { OutputDeviceId = "{0.0.0.00000000}.{other}", OutputDeviceName = "Outro (USB)" });
        _rig.Clock.Advance(TimeSpan.FromMilliseconds(10));
        _rig.Change(p => p with { OutputDeviceId = PlayerRig.Wing34, OutputDeviceName = "WING-USB (Out 3-4)" });
        Settle();

        Assert.Equal(2, Backend.Outputs.Count); // one move, straight to the last choice
        Assert.Equal(PlayerRig.Wing34, Backend.Output.RequestedId);
    }

    [Fact]
    public void A_pause_then_a_change_resumes_on_the_new_output_with_no_restart_of_the_app()
    {
        Player.Play();
        Backend.Outputs[0].PumpSeconds(0.5);
        Player.Pause();
        Backend.Outputs[0].PumpSeconds(5.5);
        Assert.Equal(PlayerState.Paused, Player.GetStatus().State);

        ChooseWing();
        Settle();
        Assert.Single(Backend.Outputs); // nothing is sounding, nothing opens

        Player.Play();

        Assert.Equal(PlayerRig.Wing34, Backend.Output.RequestedId);
        Assert.Equal(PlayerState.Playing, Player.GetStatus().State);
    }

    [Fact]
    public void A_new_output_that_will_not_open_leaves_the_sound_where_it_is_and_says_why()
    {
        Player.Play();
        Backend.Outputs[0].PumpSeconds(0.5);

        Backend.OutputError = new InvalidOperationException("device busy");
        ChooseWing();
        Settle();

        Assert.Single(Backend.Outputs);
        Assert.False(Backend.Outputs[0].Disposed);
        Assert.Equal(PlayerState.Playing, Player.GetStatus().State);
        Assert.Contains("device busy", Player.GetStatus().LastError, StringComparison.Ordinal);
    }

    [Fact]
    public void The_old_output_is_closed_after_a_second_even_when_nothing_pulls_it_any_more()
    {
        Player.Play();
        Backend.Outputs[0].PumpSeconds(0.5);

        ChooseWing();
        Settle();
        Assert.False(Backend.Outputs[0].Disposed);

        _rig.Clock.Advance(TimeSpan.FromSeconds(1)); // an unplugged device never pulls its fade-out to the end

        Assert.True(Backend.Outputs[0].Disposed);
    }

    [Fact]
    public void A_change_that_resolves_to_the_same_output_does_not_move_anything()
    {
        Player.Play();
        Backend.Outputs[0].PumpSeconds(0.5);

        _rig.Change(p => p with { OutputDeviceId = PlayerRig.Speakers, OutputDeviceName = "Alto-falantes (Realtek)" });
        Settle();

        Assert.Single(Backend.Outputs);
    }
}
