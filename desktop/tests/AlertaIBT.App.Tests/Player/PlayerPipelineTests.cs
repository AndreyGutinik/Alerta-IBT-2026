using AlertaIBT.Core.Player;

namespace AlertaIBT.App.Tests.Player;

// The real chain except the device: a real audio file is decoded by Media Foundation and pulled through the same mixer the sound card pulls
// (decoder -> resampler -> channel map -> PlayerMixer fade, seek duck and volume -> output).
public sealed class PlayerPipelineTests : IDisposable
{
    // The RMS of a 0.5-amplitude sine is 0.5 / sqrt(2).
    private const double FullRms = 0.35355339;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "alerta-ibt-pipeline-" + Guid.NewGuid().ToString("N"));

    public PlayerPipelineTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // A scanner may hold the file for a moment.
        }
    }

    internal static string Asset(string name) => Path.Combine(AppContext.BaseDirectory, "Assets", name);

    // A 24-bit stereo 440 Hz tone, long enough for a 5 s fade, written as a WAV (the committed FLAC is only 2 s). From second `stepAt` on the
    // amplitude is `after` instead of `before`, so a seek is audible in the level.
    private string Tone(int rate = RealChainRig.Rate, double seconds = 20, double before = 0.5, double after = 0.5, double stepAt = 10)
    {
        var path = Path.Combine(_dir, $"tone-{rate}-{before}-{after}.wav");
        var frames = (int)(rate * seconds);
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        var dataBytes = frames * 2 * 3;
        writer.Write("RIFF"u8);
        writer.Write(36 + dataBytes);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)2);
        writer.Write(rate);
        writer.Write(rate * 2 * 3);
        writer.Write((short)6);
        writer.Write((short)24);
        writer.Write("data"u8);
        writer.Write(dataBytes);
        for (var i = 0; i < frames; i++)
        {
            var amplitude = i < stepAt * rate ? before : after;
            var value = (int)(Math.Sin(2 * Math.PI * 440 * i / rate) * amplitude * 8388607);
            for (var c = 0; c < 2; c++)
            {
                writer.Write((byte)(value & 0xFF));
                writer.Write((byte)((value >> 8) & 0xFF));
                writer.Write((byte)((value >> 16) & 0xFF));
            }
        }

        return path;
    }

    private static void AssertMonotone(List<double> windows, bool falling)
    {
        for (var i = 1; i < windows.Count; i++)
        {
            var ok = falling ? windows[i] < windows[i - 1] : windows[i] > windows[i - 1];
            Assert.True(ok, $"window {i} ({windows[i]:F4}) is not {(falling ? "quieter" : "louder")} than {i - 1} ({windows[i - 1]:F4})");
        }
    }

    // ---- decoding ----

    [Fact]
    public void The_committed_24_bit_flac_decodes_to_the_tone_at_its_own_level()
    {
        using var rig = new RealChainRig(Asset("tone-24-48.flac"));
        rig.Player.Play();
        rig.Output.Pull(RealChainRig.Rate / 10); // the 10 ms click-free start and some settling

        var rms = rig.Output.RmsWindows(0.5, 0.5);

        Assert.InRange(rms[0], 0.25 * 0.98, 0.25 * 1.02); // the file's amplitude is 0.354
    }

    [Fact]
    public void A_44_1_kHz_file_on_a_48_kHz_device_keeps_its_level_through_the_resampler()
    {
        using var rig = new RealChainRig(Tone(rate: 44100));
        rig.Player.Play();
        rig.Output.Pull(RealChainRig.Rate / 10);

        var rms = rig.Output.RmsWindows(1, 1);

        Assert.InRange(rms[0], FullRms * 0.97, FullRms * 1.03);
    }

    // ---- fade out: pause and stop ----

    [Theory]
    [InlineData(5)]
    [InlineData(3)]
    public void A_pause_fades_the_real_samples_out_smoothly_and_then_pauses(int seconds)
    {
        using var rig = new RealChainRig(Tone());
        rig.Change(p => p with { FadeOutSeconds = seconds });
        rig.Player.Play();
        rig.Output.RmsWindows(0.5, 0.5);

        rig.Player.Pause();
        Assert.True(rig.Player.GetStatus().Fading);
        var windows = rig.Output.RmsWindows(seconds, 0.25);

        AssertMonotone(windows, falling: true);
        Assert.True(windows[0] < FullRms, "the first quarter second already is below full level");
        Assert.True(windows[windows.Count / 2] < FullRms * 0.4, "half way it is already well down (a straight line would still be at 50%)");
        Assert.True(windows[^1] < FullRms * 0.02, "the last window is silence, not a cut at audible level");
        Assert.Equal(PlayerState.Paused, rig.Player.GetStatus().State);
        Assert.True(rig.Output.Disposed);
    }

    [Fact]
    public void A_stop_fades_out_the_same_way_and_stops()
    {
        using var rig = new RealChainRig(Tone());
        rig.Change(p => p with { FadeOutSeconds = 3 });
        rig.Player.Play();
        rig.Output.RmsWindows(0.5, 0.5);

        rig.Player.Stop();
        var windows = rig.Output.RmsWindows(3, 0.25);

        AssertMonotone(windows, falling: true);
        Assert.Equal(PlayerState.Stopped, rig.Player.GetStatus().State);
    }

    [Fact]
    public void A_dry_stop_is_silent_within_a_few_milliseconds()
    {
        using var rig = new RealChainRig(Tone());
        rig.Change(p => p with { FadeOutSeconds = 0 });
        rig.Player.Play();
        rig.Output.RmsWindows(0.5, 0.5);

        rig.Player.Stop();
        var block = rig.Output.Pull(RealChainRig.Rate / 50); // 20 ms

        Assert.InRange(Math.Abs(block[0]), 0f, 0.6f);
        Assert.All(block.Skip(RealChainRig.Rate / 50 * 2 - 40), sample => Assert.Equal(0f, sample, 3));
        Assert.Equal(PlayerState.Stopped, rig.Player.GetStatus().State);
    }

    // ---- fade in: the alarm start ----

    [Fact]
    public void The_alarm_start_fades_in_from_silence_to_the_target_level()
    {
        using var rig = new RealChainRig(Tone());
        rig.Change(p => p with { FadeInSeconds = 5 });

        var start = rig.Player.StartFromAlarm("Igreja");
        Assert.Equal(AlarmStartOutcome.Started, start.Outcome);
        var windows = rig.Output.RmsWindows(5, 0.25);

        AssertMonotone(windows, falling: false);
        Assert.True(windows[0] < FullRms * 0.02, "it starts from silence");
        Assert.True(windows[windows.Count / 2] < FullRms * 0.4, "half way it is still well below the target");
        Assert.InRange(windows[^1], FullRms * 0.9, FullRms * 1.01); // the last quarter second is close to the target
        var after = rig.Output.RmsWindows(0.5, 0.5);
        Assert.InRange(after[0], FullRms * 0.99, FullRms * 1.01);
    }

    [Fact]
    public void A_dry_alarm_start_is_a_10_ms_ramp_not_a_click()
    {
        using var rig = new RealChainRig(Tone());
        rig.Change(p => p with { FadeInSeconds = 0 });

        rig.Player.StartFromAlarm("Igreja");
        var first = rig.Output.Pull(RealChainRig.Rate / 100);

        Assert.True(Math.Abs(first[0]) < 0.001f);
        var after = rig.Output.RmsWindows(0.2, 0.2);
        Assert.InRange(after[0], FullRms * 0.95, FullRms * 1.01);
    }

    // ---- volume and mute act on the same chain ----

    [Fact]
    public void The_player_volume_and_mute_scale_the_real_samples_and_a_fade_multiplies_with_them()
    {
        using var rig = new RealChainRig(Tone());
        rig.Player.Play();
        rig.Output.RmsWindows(0.5, 0.5);

        rig.Change(p => p with { Volume = 50 });
        rig.Output.RmsWindows(0.2, 0.2); // the 30 ms smoothing settles
        var half = rig.Output.RmsWindows(0.5, 0.5)[0];
        rig.Change(p => p with { StartMuted = true });
        rig.Output.RmsWindows(0.2, 0.2);
        var muted = rig.Output.RmsWindows(0.5, 0.5)[0];
        rig.Change(p => p with { StartMuted = false, Volume = 100 });
        rig.Output.RmsWindows(0.2, 0.2);
        var back = rig.Output.RmsWindows(0.5, 0.5)[0];

        Assert.InRange(half, FullRms * 0.5 * 0.98, FullRms * 0.5 * 1.02);
        Assert.Equal(0.0, muted, 4);
        Assert.InRange(back, FullRms * 0.98, FullRms * 1.02);

        rig.Change(p => p with { Volume = 50, FadeOutSeconds = 3 });
        rig.Output.RmsWindows(0.2, 0.2);
        rig.Player.Pause();
        var windows = rig.Output.RmsWindows(3, 0.25);
        Assert.True(windows[0] < FullRms * 0.5, "the fade starts from the volume, not from full");
        AssertMonotone(windows, falling: true);
    }

    // ---- seek ----

    [Fact]
    public void A_seek_jumps_the_real_decoder_without_a_click_and_the_chain_stays_intact()
    {
        using var rig = new RealChainRig(Tone(before: 0.2, after: 0.6, stepAt: 10));
        rig.Change(p => p with { Volume = 50 });
        rig.Player.Play();
        rig.Output.RmsWindows(0.5, 0.5);
        Assert.InRange(rig.Output.RmsWindows(0.5, 0.5)[0], 0.2 / Math.Sqrt(2) * 0.5 * 0.97, 0.2 / Math.Sqrt(2) * 0.5 * 1.03);

        rig.Player.Seek(TimeSpan.FromSeconds(14));
        var block = rig.Output.Pull(RealChainRig.Rate / 10); // 100 ms around the jump
        var biggestStep = 0.0;
        for (var i = 2; i < block.Length; i += 2)
        {
            biggestStep = Math.Max(biggestStep, Math.Abs(block[i] - block[i - 2]));
        }

        Assert.True(biggestStep < 0.02, $"no click at the jump: the biggest step is {biggestStep:F4}");
        rig.Output.RmsWindows(0.2, 0.2);
        var after = rig.Output.RmsWindows(0.5, 0.5)[0];
        Assert.InRange(after, 0.6 / Math.Sqrt(2) * 0.5 * 0.97, 0.6 / Math.Sqrt(2) * 0.5 * 1.03); // the second half of the file, at volume 50
        Assert.InRange(rig.Player.GetStatus().Position.TotalSeconds, 14.5, 15.5);
    }

    [Fact]
    public void A_seek_through_the_real_resampler_lands_where_it_was_asked()
    {
        using var rig = new RealChainRig(Tone(rate: 44100, before: 0.2, after: 0.6, stepAt: 10));
        rig.Player.Play();
        rig.Output.RmsWindows(0.5, 0.5);

        rig.Player.Seek(TimeSpan.FromSeconds(12));
        rig.Output.RmsWindows(0.3, 0.3);
        var after = rig.Output.RmsWindows(0.5, 0.5)[0];

        Assert.InRange(after, 0.6 / Math.Sqrt(2) * 0.96, 0.6 / Math.Sqrt(2) * 1.04);
        Assert.InRange(rig.Player.GetStatus().Position.TotalSeconds, 12.5, 13.5);
    }

    [Fact]
    public void A_seek_during_a_fade_out_leaves_the_fade_running()
    {
        using var rig = new RealChainRig(Tone());
        rig.Change(p => p with { FadeOutSeconds = 3 });
        rig.Player.Play();
        rig.Output.RmsWindows(0.5, 0.5);
        rig.Player.Pause();
        var before = rig.Output.RmsWindows(1, 0.5);

        rig.Player.Seek(TimeSpan.FromSeconds(5));
        var windows = rig.Output.RmsWindows(2, 0.25);

        Assert.True(windows[1] < before[^1], "still falling after the seek");
        Assert.True(windows[^1] < FullRms * 0.02);
        Assert.Equal(PlayerState.Paused, rig.Player.GetStatus().State);
    }

    [Fact]
    public void A_seek_while_paused_resumes_that_track_from_the_new_place_through_the_real_decoder()
    {
        using var rig = new RealChainRig(Tone(before: 0.2, after: 0.6, stepAt: 10));
        rig.Change(p => p with { FadeOutSeconds = 0 });
        rig.Player.Play();
        rig.Output.RmsWindows(0.5, 0.5);
        rig.Player.Pause();
        rig.Output.RmsWindows(0.1, 0.1);
        Assert.Equal(PlayerState.Paused, rig.Player.GetStatus().State);

        Assert.True(rig.Player.Seek(TimeSpan.FromSeconds(13)).Success);
        rig.Player.Play();
        rig.Output.RmsWindows(0.3, 0.3);
        var level = rig.Output.RmsWindows(0.5, 0.5)[0];

        Assert.InRange(level, 0.6 / Math.Sqrt(2) * 0.97, 0.6 / Math.Sqrt(2) * 1.03);
        Assert.InRange(rig.Player.GetStatus().Position.TotalSeconds, 13.5, 14.5);
    }
}
