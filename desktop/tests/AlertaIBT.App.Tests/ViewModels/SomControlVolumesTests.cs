using AlertaIBT.App.Services;
using AlertaIBT.Core.Control;

namespace AlertaIBT.App.Tests.ViewModels;

// The console faders move the very rows of the Som tab: the same live volume, the same autosave, and the slider on the screen follows.
public sealed class SomControlVolumesTests : IDisposable
{
    private readonly SomRig _rig = new();
    private readonly SomControlVolumes _volumes;

    public SomControlVolumesTests()
    {
        _volumes = new SomControlVolumes(_rig.Som, action => action());
    }

    public void Dispose() => _rig.Dispose();

    [Fact]
    public void A_fader_sets_the_row_and_the_value_reaches_the_audio_and_the_saved_settings()
    {
        _volumes.Set(ControlAction.VolumeWindows, 55);

        Assert.Equal(55, _rig.Som.MasterRow.Volume);
        Assert.Equal(55, _volumes.Get(ControlAction.VolumeWindows));
        _rig.Settings.Rest();
        Assert.Equal(55, _rig.Current.Audio.MasterVolume);
        Assert.Equal(55, _rig.Settings.OnDisk().Audio.MasterVolume);
    }

    [Fact]
    public void The_three_faders_reach_windows_holyrics_and_the_player()
    {
        _volumes.Set(ControlAction.VolumeHolyrics, 40);
        _volumes.Set(ControlAction.VolumePlayer, 30);

        Assert.Equal(40, _rig.Som.HolyricsRow.Volume);
        Assert.Equal(30, _rig.Som.PlayerRow.Volume);
        _rig.Settings.Rest();
        Assert.Equal(30, _rig.Settings.OnDisk().Player.Volume);
        Assert.Null(_volumes.Get(ControlAction.Take));
    }

    [Fact]
    public void The_app_value_the_pickup_compares_is_what_the_slider_shows()
    {
        _rig.Som.PlayerRow.Volume = 70; // the slider dragged by hand

        Assert.Equal(70, _volumes.Get(ControlAction.VolumePlayer));
    }

    [Fact]
    public void With_a_volume_ramp_the_row_holds_the_target_at_once_and_the_audio_goes_there_over_the_ramp()
    {
        _rig.Som.VolumeRampSeconds = 2;

        _volumes.Set(ControlAction.VolumeWindows, 20);

        Assert.Equal(20, _volumes.Get(ControlAction.VolumeWindows)); // the pickup sees the target, not a half way value
        Assert.Equal(2, _rig.Current.Audio.VolumeRampSeconds);
    }
}
