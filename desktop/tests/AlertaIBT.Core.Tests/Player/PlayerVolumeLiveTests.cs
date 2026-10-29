using AlertaIBT.Core.Player;
using AlertaIBT.Core.Settings;

namespace AlertaIBT.Core.Tests.Player;

// The player volume is heard the moment it is set (the mixer smooths 30 ms against clicks), and with "Suavizar mudanca de volume" it walks to the target.
public sealed class PlayerVolumeLiveTests
{
    private const int Rate = 8000;

    private static (PlayerMixer Mixer, FakeTrack Track) Setup()
    {
        var mixer = new PlayerMixer(Rate, 2, action => action());
        var track = new FakeTrack("/x.flac", Rate, 2, Rate * 60, 0.5f);
        mixer.SetTrack(track);
        return (mixer, track);
    }

    // The level of the left channel over each `window` seconds, for `seconds`.
    private static List<double> Levels(PlayerMixer mixer, double seconds, double window)
    {
        var levels = new List<double>();
        var buffer = new float[(int)(window * Rate) * 2];
        for (var i = 0; i < (int)Math.Round(seconds / window); i++)
        {
            mixer.Read(buffer, 0, buffer.Length);
            levels.Add(buffer.Where((_, j) => j % 2 == 0).Average(x => Math.Abs(x)) / 0.5);
        }

        return levels;
    }

    [Fact]
    public void Without_a_ramp_the_new_volume_is_there_within_30_ms()
    {
        var (mixer, _) = Setup();
        Levels(mixer, 0.1, 0.1);

        mixer.SetVolume(0.2f, muted: false);
        var levels = Levels(mixer, 0.1, 0.05);

        Assert.InRange(levels[1], 0.199, 0.201);
    }

    [Fact]
    public void A_mute_is_heard_within_30_ms()
    {
        var (mixer, _) = Setup();
        Levels(mixer, 0.1, 0.1);

        mixer.SetVolume(1f, muted: true);

        Assert.InRange(Levels(mixer, 0.1, 0.05)[1], 0, 0.001);
    }

    [Fact]
    public void With_a_ramp_the_volume_walks_down_over_the_chosen_time_and_crosses_the_middle_at_the_middle()
    {
        var (mixer, _) = Setup();
        Levels(mixer, 0.1, 0.1);

        mixer.SetVolume(0f, muted: false, rampSeconds: 1);
        var levels = Levels(mixer, 1.2, 0.1);

        Assert.True(levels.Zip(levels.Skip(1)).All(p => p.Second <= p.First + 1e-9), "monotonic");
        Assert.InRange(levels[4], 0.45, 0.65); // the window around 0.4 to 0.5 s
        Assert.True(levels[9] > 0.0 && levels[9] < 0.1, "still going at the last tenth");
        Assert.InRange(levels[11], 0, 0.001);
    }

    [Fact]
    public void A_mute_with_a_ramp_fades_out_and_the_unmute_fades_in()
    {
        var (mixer, _) = Setup();
        Levels(mixer, 0.1, 0.1);

        mixer.SetVolume(1f, muted: true, rampSeconds: 0.5);
        var down = Levels(mixer, 0.6, 0.05);
        mixer.SetVolume(1f, muted: false, rampSeconds: 0.5);
        var up = Levels(mixer, 0.6, 0.05);

        Assert.True(down[3] > 0.05 && down[3] < 0.95);
        Assert.InRange(down[^1], 0, 0.001);
        Assert.True(up.Zip(up.Skip(1)).All(p => p.Second >= p.First - 1e-9));
        Assert.InRange(up[^1], 0.999, 1.001);
    }

    [Fact]
    public void A_new_target_retargets_the_ramp_and_the_same_target_again_does_not_restart_it()
    {
        var (mixer, _) = Setup();
        Levels(mixer, 0.1, 0.1);
        mixer.SetVolume(0f, muted: false, rampSeconds: 1);
        Levels(mixer, 0.5, 0.5);

        mixer.SetVolume(0f, muted: false, rampSeconds: 1); // the settings echo of the same wish
        var rest = Levels(mixer, 0.6, 0.1);
        Assert.InRange(rest[^1], 0, 0.001); // it still ended on schedule (1 s after the first call)

        var (mixer2, _) = Setup();
        Levels(mixer2, 0.1, 0.1);
        mixer2.SetVolume(0f, muted: false, rampSeconds: 1);
        var half = Levels(mixer2, 0.5, 0.05)[^1];
        mixer2.SetVolume(1f, muted: false, rampSeconds: 1); // turns around from where it is
        var back = Levels(mixer2, 1.2, 0.05);
        Assert.InRange(back[0], half - 0.1, half + 0.2);
        Assert.InRange(back[^1], 0.999, 1.001);
    }

    [Fact]
    public void The_player_applies_a_live_volume_to_the_playing_sound_before_any_setting_is_saved()
    {
        using var rig = new PlayerRig();
        rig.Player.Play();
        rig.Backend.Output.PumpSeconds(0.2);

        rig.Player.ApplyLiveVolume(30, muted: false);
        var sound = rig.Backend.Output.Pump(rig.Backend.SampleRate / 10); // 100 ms later

        Assert.Equal(100, rig.Settings.Current.Player.Volume); // nothing was saved
        Assert.InRange(sound[^2], 0.5f * 0.3f - 0.005f, 0.5f * 0.3f + 0.005f);
    }

    [Fact]
    public void The_player_follows_the_ramp_setting()
    {
        using var rig = new PlayerRig();
        rig.Settings.Update(s => s with { Audio = s.Audio with { VolumeRampSeconds = 1 } });
        rig.Player.Play();
        rig.Backend.Output.PumpSeconds(0.2);

        rig.Player.ApplyLiveVolume(0, muted: false);
        var soon = rig.Backend.Output.Pump(rig.Backend.SampleRate / 10); // 0.1 s into a 1 s walk
        rig.Backend.Output.PumpSeconds(0.3);
        var middle = rig.Backend.Output.Pump(rig.Backend.SampleRate / 100); // around 0.5 s

        Assert.True(soon[^2] > 0.5f * 0.9f, "barely moved after 0.1 s");
        Assert.InRange(middle[^2], 0.5f * 0.35f, 0.5f * 0.65f);
        rig.Backend.Output.PumpSeconds(0.7);
        Assert.InRange(rig.Backend.Output.Pump(10)[^2], 0, 0.001f);
    }
}
