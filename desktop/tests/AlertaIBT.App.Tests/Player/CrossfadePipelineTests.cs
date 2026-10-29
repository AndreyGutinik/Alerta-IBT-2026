using System.Diagnostics;
using AlertaIBT.Core.Player;

namespace AlertaIBT.App.Tests.Player;

// Fade lengths, gapless change and the crossfade through the real chain except the device: real files are decoded by Media Foundation, resampled
// (44.1 kHz material on a 48 kHz device), channel-mapped and pulled through the same PlayerMixer the sound card pulls. Two tones that do not
// interfere (440 Hz outgoing, 660 Hz incoming) let the level of each track be read apart with a Goertzel filter.
public sealed class CrossfadePipelineTests : IDisposable
{
    private const double FullRms = 0.35355339; // RMS of a 0.5-amplitude sine
    private const int Rate = RealChainRig.Rate;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "alerta-ibt-crossfade-" + Guid.NewGuid().ToString("N"));

    public CrossfadePipelineTests() => Directory.CreateDirectory(_dir);

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

    // A 24-bit stereo tone WAV of the given frequency, rate and length.
    private string Tone(double frequency, int rate = Rate, double seconds = 20, double amplitude = 0.5)
    {
        var path = Path.Combine(_dir, $"tone-{frequency}-{rate}-{seconds}.wav");
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
            var value = (int)(Math.Sin(2 * Math.PI * frequency * i / rate) * amplitude * 8388607);
            for (var c = 0; c < 2; c++)
            {
                writer.Write((byte)(value & 0xFF));
                writer.Write((byte)((value >> 8) & 0xFF));
                writer.Write((byte)((value >> 16) & 0xFF));
            }
        }

        return path;
    }

    // The left channel of `seconds` of what the sound card pulls, in 10 ms reads.
    private static float[] Capture(CaptureOutput output, double seconds)
    {
        var block = Rate / 100;
        var reads = (int)Math.Round(seconds * 100);
        var result = new float[reads * block];
        for (var i = 0; i < reads; i++)
        {
            var pulled = output.Pull(block);
            for (var f = 0; f < block; f++)
            {
                result[(i * block) + f] = pulled[f * 2];
            }
        }

        return result;
    }

    private static double Amplitude(float[] x, double startSeconds, double lengthSeconds, double frequency)
    {
        var start = (int)Math.Round(startSeconds * Rate);
        var length = (int)Math.Round(lengthSeconds * Rate);
        double re = 0;
        double im = 0;
        for (var i = 0; i < length; i++)
        {
            var angle = 2 * Math.PI * frequency * i / Rate;
            re += x[start + i] * Math.Cos(angle);
            im -= x[start + i] * Math.Sin(angle);
        }

        return 2 * Math.Sqrt((re * re) + (im * im)) / length;
    }

    private static double Rms(float[] x, double startSeconds, double lengthSeconds)
    {
        var start = (int)Math.Round(startSeconds * Rate);
        var length = (int)Math.Round(lengthSeconds * Rate);
        double sum = 0;
        for (var i = 0; i < length; i++)
        {
            sum += (double)x[start + i] * x[start + i];
        }

        return Math.Sqrt(sum / length);
    }

    private static double Db(double value, double reference) => 20 * Math.Log10(Math.Max(value, 1e-9) / reference);

    // ---- fades with the new lengths ----

    [Theory]
    [InlineData(0.5)]
    [InlineData(1)]
    [InlineData(10)]
    public void Play_fades_in_over_the_chosen_length_from_silence_with_the_quadratic_curve(double seconds)
    {
        using var rig = new RealChainRig(Tone(440));
        rig.Change(p => p with { FadeInSeconds = seconds });

        rig.Player.Play();
        var windows = rig.Output.RmsWindows(seconds, seconds / 10);

        for (var i = 1; i < windows.Count; i++)
        {
            Assert.True(windows[i] > windows[i - 1], $"window {i} is not louder than {i - 1}");
        }

        Assert.True(windows[0] < FullRms * 0.02, "it starts from silence");
        Assert.True(windows[4] < FullRms * 0.4, "at half the time it is still well below full (a straight line would be at 50%)");
        Assert.InRange(windows[^1], FullRms * 0.85, FullRms * 1.01);
        var after = rig.Output.RmsWindows(0.5, 0.5);
        Assert.InRange(after[0], FullRms * 0.99, FullRms * 1.01);
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(1)]
    [InlineData(10)]
    public void Pause_fades_out_over_the_chosen_length_and_then_pauses(double seconds)
    {
        using var rig = new RealChainRig(Tone(440, seconds: 30));
        rig.Change(p => p with { FadeOutSeconds = seconds });
        rig.Player.Play();
        rig.Output.RmsWindows(0.5, 0.5);

        rig.Player.Pause();
        Assert.True(rig.Player.GetStatus().Fading);
        var windows = rig.Output.RmsWindows(seconds, seconds / 10);

        for (var i = 1; i < windows.Count; i++)
        {
            Assert.True(windows[i] < windows[i - 1], $"window {i} is not quieter than {i - 1}");
        }

        Assert.True(windows[4] < FullRms * 0.4);
        Assert.True(windows[^1] < FullRms * 0.02, "it ends in silence, not at an audible level");
        Assert.Equal(PlayerState.Paused, rig.Player.GetStatus().State);
        Assert.True(rig.Output.Disposed);
    }

    [Fact]
    public void Zero_seconds_is_immediate_for_play_and_for_stop_apart_from_the_10_ms_anti_click_ramp()
    {
        using var rig = new RealChainRig(Tone(440));
        rig.Change(p => p with { FadeInSeconds = 0, FadeOutSeconds = 0 });

        rig.Player.Play();
        var start = rig.Output.Pull(Rate / 100);
        Assert.True(Math.Abs(start[0]) < 0.001f);
        var level = rig.Output.RmsWindows(0.1, 0.1);
        Assert.InRange(level[0], FullRms * 0.97, FullRms * 1.01);

        rig.Player.Stop();
        rig.Output.Pull(Rate / 50);
        Assert.Equal(PlayerState.Stopped, rig.Player.GetStatus().State);
    }

    [Fact]
    public void The_alarm_start_uses_the_same_fade_in_as_play()
    {
        using var rig = new RealChainRig(Tone(440));
        rig.Change(p => p with { FadeInSeconds = 2 });

        rig.Player.StartFromAlarm("Igreja");
        var windows = rig.Output.RmsWindows(2, 0.2);

        Assert.True(windows[0] < FullRms * 0.02);
        Assert.InRange(windows[^1], FullRms * 0.85, FullRms * 1.01);
    }

    // ---- gapless ----

    [Fact]
    public void A_real_flac_repeated_with_no_crossfade_has_no_gap_at_the_boundary_two_decoders_at_once()
    {
        using var rig = new RealChainRig(PlayerPipelineTests.Asset("tone-24-48.flac")); // 2 s, repeat one
        rig.Player.Play();

        var sound = Capture(rig.Output, 5.0);

        Assert.True(rig.Backend.Opened.Count >= 2, "the next one was opened while the first still played");
        var reference = Rms(sound, 0.5, 0.5);
        for (var start = 0.1; start + 0.01 <= 4.9; start += 0.01)
        {
            Assert.True(Db(Rms(sound, start, 0.01), reference) > -6, $"a quiet 10 ms window at {start:F2} s (gap at the boundary)");
        }
    }

    [Fact]
    public void Two_different_files_follow_each_other_gapless_with_the_resampler_on_each()
    {
        using var rig = new RealChainRig([Tone(440, 44100, 3), Tone(660, 48000, 3)], "all");
        rig.Player.Play();

        var sound = Capture(rig.Output, 5.8);

        Assert.Equal(Path.GetFileName(rig.Paths[1]), Path.GetFileName(rig.Player.GetStatus().Track!.Path));
        var reference = Rms(sound, 0.5, 0.5);
        for (var start = 0.1; start + 0.01 <= 5.7; start += 0.01)
        {
            Assert.True(Db(Rms(sound, start, 0.01), reference) > -6, $"gap at {start:F2} s");
        }
    }

    // ---- crossfade ----

    [Fact]
    public void A_crossfade_between_a_44_1_kHz_and_a_48_kHz_track_mixes_with_constant_power_through_the_real_chain()
    {
        using var rig = new RealChainRig([Tone(440, 44100, 8), Tone(660, 48000, 8)], "all");
        rig.Change(p => p with { CrossfadeSeconds = 2 });
        rig.Player.Play();

        var sound = Capture(rig.Output, 11); // the overlap is 6 to 8 s

        var lastOut = 2.0;
        var lastIn = -1.0;
        for (var start = 6.0; start < 8.0 - 1e-6; start += 0.1)
        {
            var progress = (start + 0.05 - 6) / 2;
            var outgoing = Amplitude(sound, start, 0.1, 440) / 0.5;
            var incoming = Amplitude(sound, start, 0.1, 660) / 0.5;
            Assert.Equal(Math.Cos(progress * Math.PI / 2), outgoing, 1);
            Assert.Equal(Math.Sin(progress * Math.PI / 2), incoming, 1);
            Assert.InRange((outgoing * outgoing) + (incoming * incoming), 0.95, 1.05); // constant power
            Assert.True(outgoing < lastOut && incoming > lastIn, $"the outgoing falls and the incoming rises at {start:F1} s");
            lastOut = outgoing;
            lastIn = incoming;
        }

        // The loudness of the whole run stays level (no dip in the middle), and no window anywhere is quiet.
        var reference = Rms(sound, 1, 1);
        for (var start = 0.2; start + 0.2 <= 10.8; start += 0.2)
        {
            Assert.InRange(Db(Rms(sound, start, 0.2), reference), -0.7, 0.7);
        }

        Assert.Equal(0.5, Amplitude(sound, 4, 1, 440), 2);
        Assert.Equal(0.5, Amplitude(sound, 9, 1, 660), 2);
        Assert.Equal(0, Amplitude(sound, 9, 1, 440), 2);
    }

    [Fact]
    public void The_screen_follows_the_new_track_at_the_start_of_the_overlap_and_the_old_decoder_is_freed_at_its_end()
    {
        using var rig = new RealChainRig([Tone(440, 44100, 8), Tone(660, 48000, 8)], "all");
        rig.Change(p => p with { CrossfadeSeconds = 2 });
        rig.Player.Play();

        Capture(rig.Output, 5.9);
        Assert.Equal(rig.Paths[0], rig.Player.GetStatus().Track!.Path);

        Capture(rig.Output, 0.3); // 6.2 s: the overlap began at 6.0 s
        var status = rig.Player.GetStatus();

        Assert.Equal(rig.Paths[1], status.Track!.Path);
        Assert.Equal(2, status.TrackIndex);
        Assert.InRange(status.Position.TotalSeconds, 0.1, 0.6); // the timeline restarted with the new track
        Assert.InRange(status.Duration.TotalSeconds, 7.9, 8.1);
        Assert.Equal(PlayerState.Playing, status.State);
    }

    [Fact]
    public void A_track_shorter_than_twice_the_crossfade_crosses_over_at_half_its_length()
    {
        using var rig = new RealChainRig([Tone(440, 44100, 3), Tone(660, 48000, 10)], "all");
        rig.Change(p => p with { CrossfadeSeconds = 5 }); // wanted 5 s, the track has 3: 1.5 s apply

        rig.Player.Play();
        var sound = Capture(rig.Output, 5);

        Assert.Equal(0.5, Amplitude(sound, 1.0, 0.4, 440), 2); // alone until 1.5 s
        Assert.Equal(0, Amplitude(sound, 1.0, 0.4, 660), 2);
        Assert.True(Amplitude(sound, 2.4, 0.1, 440) < 0.5 * 0.7, "well into the overlap (1.5 to 3 s) the outgoing track is down");
        Assert.True(Amplitude(sound, 2.8, 0.1, 660) > 0.5 * 0.85, "and the incoming one is nearly at full level");
        Assert.Equal(0, Amplitude(sound, 3.5, 1, 440), 2);
    }

    [Fact]
    public void A_pause_during_the_crossfade_takes_both_tracks_down_together_and_frees_both_decoders()
    {
        using var rig = new RealChainRig([Tone(440, 44100, 8), Tone(660, 48000, 8)], "all");
        rig.Change(p => p with { CrossfadeSeconds = 2, FadeOutSeconds = 1 });
        rig.Player.Play();
        Capture(rig.Output, 6.5); // in the overlap

        rig.Player.Pause();
        var fade = Capture(rig.Output, 1.05);

        var previous = double.MaxValue;
        for (var start = 0.0; start + 0.1 <= 1.0; start += 0.1)
        {
            var power = Math.Sqrt(Math.Pow(Amplitude(fade, start, 0.1, 440), 2) + Math.Pow(Amplitude(fade, start, 0.1, 660), 2));
            Assert.True(power < previous + 0.005, "the mix only goes down");
            previous = power;
        }

        Assert.True(previous < 0.01, "silent at the end");
        Assert.Equal(PlayerState.Paused, rig.Player.GetStatus().State);
        Assert.True(rig.Output.Disposed);
    }

    [Fact]
    public void A_seek_during_the_crossfade_cancels_it_and_the_sought_track_plays_alone()
    {
        using var rig = new RealChainRig([Tone(440, 44100, 8), Tone(660, 48000, 30)], "all");
        rig.Change(p => p with { CrossfadeSeconds = 2 });
        rig.Player.Play();
        Capture(rig.Output, 6.5);

        Assert.True(rig.Player.Seek(TimeSpan.FromSeconds(15)).Success);
        var sound = Capture(rig.Output, 1.0);

        Assert.Equal(0.5, Amplitude(sound, 0.5, 0.4, 660), 2);
        Assert.Equal(0, Amplitude(sound, 0.5, 0.4, 440), 2);
        Assert.Equal(Path.GetFileName(rig.Paths[1]), Path.GetFileName(rig.Player.GetStatus().Track!.Path));
        Assert.InRange(rig.Player.GetStatus().Position.TotalSeconds, 15.4, 16.2);
    }

    [Fact]
    public void The_end_of_a_playlist_that_does_not_repeat_has_no_crossfade_into_nothing()
    {
        using var rig = new RealChainRig([Tone(440, 48000, 4)], "off");
        rig.Change(p => p with { CrossfadeSeconds = 2 });

        rig.Player.Play();
        var sound = Capture(rig.Output, 4.5);

        Assert.Equal(0.5, Amplitude(sound, 2.8, 0.1, 440), 1); // the last seconds are not faded out
        Assert.Equal(PlayerState.Stopped, rig.Player.GetStatus().State);
    }

    [Fact]
    public void Next_does_not_crossfade_it_keeps_the_short_fade_out_and_fade_in()
    {
        using var rig = new RealChainRig([Tone(440, 48000, 10), Tone(660, 48000, 10)], "all");
        rig.Change(p => p with { CrossfadeSeconds = 4 });
        rig.Player.Play();
        Capture(rig.Output, 2);

        rig.Player.Next();
        var sound = Capture(rig.Output, 0.6);

        var quietest = Enumerable.Range(0, 50).Min(i => Rms(sound, i * 0.01, 0.01));
        Assert.True(quietest < FullRms * 0.1, "it passes through silence: a skip is a fade, not a mix");
        Assert.Equal(0.5, Amplitude(sound, 0.5, 0.1, 660), 1);
        Assert.Equal(0, Amplitude(sound, 0.5, 0.1, 440), 1);
    }

    [Fact]
    public void Two_decoders_with_a_flac_and_a_resampled_44_1_kHz_file_stay_light_and_nothing_stays_open()
    {
        using var rig = new RealChainRig([PlayerPipelineTests.Asset("tone-24-48.flac"), Tone(660, 44100, 20)], "all");
        rig.Change(p => p with { CrossfadeSeconds = 1.5 });
        rig.Player.Play();

        var watch = Stopwatch.StartNew();
        var sound = Capture(rig.Output, 20);
        watch.Stop();

        // 20 s of sound, two decoders overlapping, in a small fraction of the time it plays (the sound card pulls 10 ms at a time).
        Assert.True(watch.Elapsed.TotalSeconds < 4, $"decoding 20 s took {watch.Elapsed.TotalSeconds:F1} s");
        Assert.True(Rms(sound, 5, 1) > 0.1);
        rig.Player.Stop();
        Capture(rig.Output, 5.1);
        Assert.Equal(PlayerState.Stopped, rig.Player.GetStatus().State);
    }
}
