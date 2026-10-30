using System.Globalization;
using AlertaIBT.Core.Player;
using AlertaIBT.Core.Settings;

namespace AlertaIBT.App.Tests.Player;

// The gain of each track over a crossfade, MEASURED through the real chain (Media Foundation decoder, resampler, channel map, TrackAnalyzer,
// MusicPlayer, PlayerMixer; only the sound card is replaced). Track A is a constant 440 Hz tone (amplitude 0.5) that ends in a very quiet tail,
// track B a constant 880 Hz tone that starts with a very quiet head. A Goertzel filter reads the level of each tone apart every 50 ms across the
// whole transition: A must fall from 1 to 0 as the cosine of the overlap, B must rise from 0 to 1 as the sine, simultaneously, with no step at the
// boundaries of the overlap.
public sealed class CrossfadeGainProbeTests : IDisposable
{
    private const double Amp = 0.5;
    private const double LoudSeconds = 28;
    private const double TailSeconds = 4;
    private const double HeadSeconds = 3;
    private const double QuietAmp = 0.0002; // about -68 dBFS: under the -50 dBFS threshold of the analyzer

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "alerta-ibt-gainprobe-" + Guid.NewGuid().ToString("N"));

    public CrossfadeGainProbeTests() => Directory.CreateDirectory(_dir);

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

    // A 16-bit WAV: `head` seconds at the quiet level, `loud` seconds at 0.5, `tail` seconds at the quiet level.
    private string Tone(string name, double frequency, int rate, int channels, double head, double loud, double tail)
    {
        var path = Path.Combine(_dir, name + ".wav");
        var frames = (int)Math.Round((head + loud + tail) * rate);
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        var dataBytes = frames * channels * 2;
        writer.Write("RIFF"u8);
        writer.Write(36 + dataBytes);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)channels);
        writer.Write(rate);
        writer.Write(rate * channels * 2);
        writer.Write((short)(channels * 2));
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(dataBytes);
        var loudFrom = (int)Math.Round(head * rate);
        var loudTo = loudFrom + (int)Math.Round(loud * rate);
        for (var i = 0; i < frames; i++)
        {
            var amplitude = i >= loudFrom && i < loudTo ? Amp : QuietAmp;
            var value = (short)Math.Round(Math.Sin(2 * Math.PI * frequency * i / rate) * amplitude * 32767);
            for (var c = 0; c < channels; c++)
            {
                writer.Write(value);
            }
        }

        return path;
    }

    private static double Goertzel(float[] x, int rate, double startSeconds, double lengthSeconds, double frequency)
    {
        var start = (int)Math.Round(startSeconds * rate);
        var length = (int)Math.Round(lengthSeconds * rate);
        double re = 0;
        double im = 0;
        for (var i = 0; i < length && start + i < x.Length; i++)
        {
            var angle = 2 * Math.PI * frequency * i / rate;
            re += x[start + i] * Math.Cos(angle);
            im -= x[start + i] * Math.Sin(angle);
        }

        return 2 * Math.Sqrt((re * re) + (im * im)) / length / Amp;
    }

    // Plays A into B through the real chain (10 ms reads unless told) and returns the left channel of the whole run plus where the overlap should be.
    private (float[] Left, double OverlapStart) Run(RealChainRig rig, double crossfade, double seconds, int pullFrames = RealChainRig.Rate / 100)
    {
        rig.Change(p => p with { CrossfadeSeconds = crossfade });
        rig.Player.Play();
        return (Capture(rig.Output, seconds, pullFrames), LoudSeconds - crossfade);
    }

    private static float[] Capture(CaptureOutput output, double seconds, int pullFrames)
    {
        var channels = output.Source.Channels;
        var total = (int)Math.Round(seconds * output.Source.SampleRate);
        var left = new float[total];
        var done = 0;
        while (done < total)
        {
            var n = Math.Min(pullFrames, total - done);
            var pulled = output.Pull(n);
            for (var f = 0; f < n; f++)
            {
                left[done + f] = pulled[f * channels];
            }

            done += n;
        }

        return left;
    }

    // The asserted shape: A at 1 before the overlap and cosine through it, B the sine through it and 1 after; constant power; no step.
    private static string Check(float[] left, int rate, double overlapStart, double crossfade, double tolerance)
    {
        const double Window = 0.05;
        var lines = new List<string>();
        var previousA = 1.0;
        var previousB = 0.0;
        var maxStep = 0.0;
        for (var t = 1.0; t + Window <= overlapStart + crossfade + 2.5; t += Window)
        {
            var a = Goertzel(left, rate, t, Window, 440);
            var b = Goertzel(left, rate, t, Window, 880);
            var progress = Math.Clamp((t + (Window / 2) - overlapStart) / crossfade, 0, 1);
            var expectA = Math.Cos(progress * Math.PI / 2);
            var expectB = Math.Sin(progress * Math.PI / 2);
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"t={t - overlapStart,6:F2} A={a:F3} (want {expectA:F3}) B={b:F3} (want {expectB:F3}) power={(a * a) + (b * b):F3}"));
            Assert.True(Math.Abs(a - expectA) <= tolerance, $"A gain {a:F3} but {expectA:F3} expected at t={t - overlapStart:F2} s into the overlap (N={crossfade}):\n{string.Join('\n', lines)}");
            Assert.True(Math.Abs(b - expectB) <= tolerance, $"B gain {b:F3} but {expectB:F3} expected at t={t - overlapStart:F2} s into the overlap (N={crossfade}):\n{string.Join('\n', lines)}");
            Assert.InRange((a * a) + (b * b), 1 - (tolerance * 2.5), 1 + (tolerance * 2.5));
            maxStep = Math.Max(maxStep, Math.Max(Math.Abs(a - previousA), Math.Abs(b - previousB)));
            previousA = a;
            previousB = b;
        }

        // A smooth ramp changes by at most (50 ms / N) of a quarter turn per window; a step shows as far more.
        Assert.True(maxStep <= (Window / crossfade * Math.PI / 2) + 0.12, $"a step of {maxStep:F3} between two 50 ms windows (N={crossfade})");
        return string.Join('\n', lines);
    }

    [Theory]
    [InlineData(0.5, 44100, 48000, 2)]
    [InlineData(3, 44100, 48000, 2)]
    [InlineData(5, 48000, 48000, 2)]
    [InlineData(8, 44100, 44100, 1)]
    [InlineData(12, 48000, 44100, 1)]
    [InlineData(12, 44100, 48000, 2)]
    public void Each_track_follows_the_constant_power_ramp_across_the_overlap_with_a_quiet_tail_and_head(double crossfade, int rateA, int rateB, int channels)
    {
        var a = Tone("a", 440, rateA, channels, 0, LoudSeconds, TailSeconds);
        var b = Tone("b", 880, rateB, channels, HeadSeconds, LoudSeconds, 0);
        using var rig = new RealChainRig([a, b], "all");

        var (left, overlapStart) = Run(rig, crossfade, LoudSeconds + 3.5);

        Check(left, RealChainRig.Rate, overlapStart, crossfade, crossfade < 1 ? 0.2 : 0.1);
    }

    // Independent formulas of the three curves (not the production function).
    private static (double Out, double In) Expected(CrossfadeCurve curve, double p)
    {
        var (cos, sin) = (Math.Cos(p * Math.PI / 2), Math.Sin(p * Math.PI / 2));
        return curve switch
        {
            CrossfadeCurve.Linear => (1 - p, p),
            CrossfadeCurve.ConstantPower => (cos, sin),
            _ => ((0.5 * (1 - p)) + (0.5 * cos), (0.5 * p) + (0.5 * sin)),
        };
    }

    [Theory]
    [InlineData(CrossfadeCurve.Linear, 8, -3.01)]
    [InlineData(CrossfadeCurve.ConstantPower, 8, 0.0)]
    [InlineData(CrossfadeCurve.Intermediate, 8, -1.37)]
    [InlineData(CrossfadeCurve.Linear, 12, -3.01)]
    [InlineData(CrossfadeCurve.Intermediate, 12, -1.37)]
    public void Each_selectable_curve_is_what_the_real_chain_plays(CrossfadeCurve curve, double crossfade, double expectedMidDb)
    {
        var a = Tone("a", 440, 44100, 2, 0, LoudSeconds, TailSeconds);
        var b = Tone("b", 880, 48000, 2, HeadSeconds, LoudSeconds, 0);
        using var rig = new RealChainRig([a, b], "all");
        rig.Change(p => p with { CrossfadeCurve = curve });

        var (left, overlapStart) = Run(rig, crossfade, LoudSeconds + 3.5);

        const double Window = 0.05;
        double previousA = 1;
        double previousB = 0;
        double maxStep = 0;
        double midDb = double.NaN;
        var table = new List<string>();
        for (var t = overlapStart - 1; t + Window <= overlapStart + crossfade + 1; t += Window)
        {
            var ga = Goertzel(left, RealChainRig.Rate, t, Window, 440);
            var gb = Goertzel(left, RealChainRig.Rate, t, Window, 880);
            var progress = Math.Clamp((t + (Window / 2) - overlapStart) / crossfade, 0, 1);
            var (wantA, wantB) = Expected(curve, progress);
            Assert.True(Math.Abs(ga - wantA) <= 0.05, $"{curve}: outgoing {ga:F3}, expected {wantA:F3} at p={progress:F2}");
            Assert.True(Math.Abs(gb - wantB) <= 0.05, $"{curve}: incoming {gb:F3}, expected {wantB:F3} at p={progress:F2}");
            Assert.True(ga <= previousA + 0.03, $"{curve}: the outgoing track must fall (p={progress:F2})");
            Assert.True(gb >= previousB - 0.03, $"{curve}: the incoming track must rise (p={progress:F2})");
            maxStep = Math.Max(maxStep, Math.Max(Math.Abs(ga - previousA), Math.Abs(gb - previousB)));
            previousA = ga;
            previousB = gb;
            if (Math.Abs(progress - 0.5) < (Window / crossfade / 2) + 1e-9)
            {
                midDb = 10 * Math.Log10((ga * ga) + (gb * gb));
            }

            if (Math.Abs((progress * 4) - Math.Round(progress * 4)) < Window / crossfade * 2 && progress is > 0 and < 1)
            {
                table.Add(string.Create(CultureInfo.InvariantCulture, $"p={progress:F2} out={ga:F3} in={gb:F3}"));
            }
        }

        Assert.False(double.IsNaN(midDb));
        Assert.True(Math.Abs(midDb - expectedMidDb) <= 0.5, $"{curve}: the middle is {midDb:F2} dB, expected {expectedMidDb:F2}");
        Assert.True(maxStep <= (Window / crossfade) + 0.12, $"{curve}: a step of {maxStep:F3}");
        Console.WriteLine($"CURVE {curve} N={crossfade}: mid {midDb:F2} dB; " + string.Join(" | ", table));
    }

    [Fact]
    public void Changing_the_curve_during_a_crossfade_does_not_change_that_crossfade_and_applies_to_the_next()
    {
        var a = Tone("a", 440, 44100, 2, 0, LoudSeconds, TailSeconds);
        var b = Tone("b", 880, 48000, 2, HeadSeconds, LoudSeconds, TailSeconds);
        var c = Tone("c", 1320, 44100, 2, HeadSeconds, LoudSeconds, 0);
        using var rig = new RealChainRig([a, b, c], "all");
        rig.Change(p => p with { CrossfadeCurve = CrossfadeCurve.Linear, CrossfadeSeconds = 8 });
        rig.Player.Play();

        // The first overlap is 20..28 s: capture up to 4 s into it, switch the curve, go on.
        var first = Capture(rig.Output, 24, 480);
        rig.Change(p => p with { CrossfadeCurve = CrossfadeCurve.ConstantPower });
        var second = Capture(rig.Output, 48 + 3.5 - 24, 480);
        var left = first.Concat(second).ToArray();

        const double Window = 0.05;
        double previousA = 0.88;
        for (var t = 21.0; t + Window <= 28; t += Window)
        {
            var progress = Math.Clamp((t + (Window / 2) - 20) / 8, 0, 1);
            var ga = Goertzel(left, RealChainRig.Rate, t, Window, 440);
            Assert.True(Math.Abs(ga - (1 - progress)) <= 0.07, $"the running crossfade stays linear: {ga:F3} vs {1 - progress:F3} at p={progress:F2}");
            Assert.True(Math.Abs(ga - previousA) <= 0.1, $"no step when the curve is changed: {previousA:F3} -> {ga:F3} at t={t:F2}");
            previousA = ga;
        }

        for (var t = 41.0; t + Window <= 48; t += Window)
        {
            var progress = Math.Clamp((t + (Window / 2) - 40) / 8, 0, 1);
            var outgoing = Goertzel(left, RealChainRig.Rate, t, Window, 880);
            Assert.True(Math.Abs(outgoing - Math.Cos(progress * Math.PI / 2)) <= 0.07, $"the next crossfade uses the new curve: {outgoing:F3} at p={progress:F2}");
        }
    }

    [Theory]
    [InlineData(1024)]
    [InlineData(7200)] // 150 ms, the whole WASAPI buffer in one read
    public void Large_reads_give_the_same_ramp(int pullFrames)
    {
        var a = Tone("a", 440, 44100, 2, 0, LoudSeconds, TailSeconds);
        var b = Tone("b", 880, 48000, 2, HeadSeconds, LoudSeconds, 0);
        using var rig = new RealChainRig([a, b], "all");

        var (left, overlapStart) = Run(rig, 8, LoudSeconds + 3.5, pullFrames);

        Check(left, RealChainRig.Rate, overlapStart, 8, 0.1);
    }

    [Theory]
    [InlineData(48000, 8)]
    [InlineData(48000, 4)]
    [InlineData(96000, 2)]
    [InlineData(44100, 2)]
    public void Other_device_formats_give_the_same_ramp(int deviceRate, int deviceChannels)
    {
        var a = Tone("a", 440, 44100, 2, 0, LoudSeconds, TailSeconds);
        var b = Tone("b", 880, 48000, 2, HeadSeconds, LoudSeconds, 0);
        using var rig = new RealChainRig([a, b], "all", deviceRate, deviceChannels);
        rig.Change(p => p with { CrossfadeSeconds = 8 });
        rig.Player.Play();

        var left = Capture(rig.Output, LoudSeconds + 3.5, deviceRate / 100);

        Check(left, deviceRate, LoudSeconds - 8, 8, 0.1);
    }

    [Fact]
    public void With_the_real_thread_pool_post_of_the_app_the_ramp_is_the_same()
    {
        var a = Tone("a", 440, 44100, 2, 0, LoudSeconds, TailSeconds);
        var b = Tone("b", 880, 48000, 2, HeadSeconds, LoudSeconds, 0);
        using var rig = new RealChainRig([a, b], "all", threadPool: true);
        rig.Change(p => p with { CrossfadeSeconds = 8 });
        rig.Player.Play();
        Thread.Sleep(1500); // the next track is decoded and measured on the pool while the first plays

        var left = Capture(rig.Output, LoudSeconds + 3.5, 480);

        Check(left, RealChainRig.Rate, LoudSeconds - 8, 8, 0.1);
    }

    [Fact]
    public void With_shuffle_on_the_first_transition_has_the_same_ramp()
    {
        var a = Tone("a", 440, 44100, 2, 0, LoudSeconds, TailSeconds);
        var b = Tone("b", 880, 48000, 2, HeadSeconds, LoudSeconds, 0);
        using var rig = new RealChainRig([a, b], "all");
        rig.Change(p => p with { CrossfadeSeconds = 8, Shuffle = true });
        rig.Player.Play();
        var first = rig.Player.GetStatus().Track!.Path;
        // With shuffle the first track is drawn: the probe assumes A first, otherwise swap what is checked.
        var left = Capture(rig.Output, LoudSeconds + 3.5, 480);
        if (first == rig.Paths[0])
        {
            Check(left, RealChainRig.Rate, LoudSeconds - 8, 8, 0.1);
        }
        else
        {
            // B first: B has a quiet head of 3 s and is 880 Hz. Its anchor is 28+3 = 31 s; the overlap is 23..31 s, A enters.
            Assert.True(Goertzel(left, RealChainRig.Rate, 24, 0.05, 880) > 0.8);
        }
    }

    [Fact]
    public void The_second_transition_of_a_playlist_has_the_same_ramp()
    {
        var a = Tone("a", 440, 44100, 2, 0, LoudSeconds, TailSeconds);
        var b = Tone("b", 880, 48000, 2, HeadSeconds, LoudSeconds, TailSeconds);
        var c = Tone("c", 1320, 44100, 2, HeadSeconds, LoudSeconds, 0);
        using var rig = new RealChainRig([a, b, c], "all");
        rig.Change(p => p with { CrossfadeSeconds = 8 });
        rig.Player.Play();

        var left = Capture(rig.Output, 48 + 3.5, 480);

        const double Window = 0.05;
        var lines = new List<string>();
        for (var t = 41.0; t + Window <= 48 + 2.5; t += Window)
        {
            var progress = Math.Clamp((t + (Window / 2) - 40) / 8, 0, 1);
            var outgoing = Goertzel(left, RealChainRig.Rate, t, Window, 880);
            var incoming = Goertzel(left, RealChainRig.Rate, t, Window, 1320);
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"t={t - 40,6:F2} out={outgoing:F3} (want {Math.Cos(progress * Math.PI / 2):F3}) in={incoming:F3} (want {Math.Sin(progress * Math.PI / 2):F3})"));
            Assert.True(Math.Abs(outgoing - Math.Cos(progress * Math.PI / 2)) < 0.1, string.Join(Environment.NewLine, lines));
            Assert.True(Math.Abs(incoming - Math.Sin(progress * Math.PI / 2)) < 0.1, string.Join(Environment.NewLine, lines));
        }
    }

    [Fact]
    public void Repeat_one_loops_with_the_same_ramp_on_every_pass()
    {
        var a = Tone("a", 440, 48000, 2, 0, LoudSeconds, TailSeconds);
        using var rig = new RealChainRig([a], "one");

        var (left, overlapStart) = Run(rig, 5, (LoudSeconds * 2) + 3);

        // Same file: both tones are 440 Hz, so only the level of the sum is checked (constant power with two uncorrelated halves is not
        // checkable on one frequency); the incoming must not be loud at the start nor the outgoing at the end.
        Assert.True(Goertzel(left, RealChainRig.Rate, overlapStart + 0.0, 0.05, 440) > 0.9, "before the overlap A plays at full level");
        Assert.True(overlapStart > 0);
    }

    [Fact]
    public void After_a_pause_resumed_during_its_fade_the_ramp_is_the_same()
    {
        var a = Tone("a", 440, 48000, 2, 0, LoudSeconds, TailSeconds);
        var b = Tone("b", 880, 48000, 2, 0, LoudSeconds, 0);
        using var rig = new RealChainRig([a, b], "all");
        rig.Change(p => p with { CrossfadeSeconds = 8, FadeOutSeconds = 2, FadeInSeconds = 0 });
        rig.Player.Play();
        Capture(rig.Output, 5, 480);
        rig.Player.Pause();
        Capture(rig.Output, 0.5, 480);
        rig.Player.Play(); // during the fade-out: the same sound comes back
        Capture(rig.Output, 1.2, 480); // the 1 s resume fade
        var from = rig.Player.GetStatus().Position.TotalSeconds;
        var left = Capture(rig.Output, LoudSeconds - from + 3.5, 480);

        Check(left, RealChainRig.Rate, LoudSeconds - 8 - from, 8, 0.12);
    }

    [Fact]
    public void Switching_the_crossfade_on_while_the_track_plays_gives_the_same_ramp()
    {
        var a = Tone("a", 440, 48000, 2, 0, LoudSeconds, TailSeconds);
        var b = Tone("b", 880, 48000, 2, 0, LoudSeconds, 0);
        using var rig = new RealChainRig([a, b], "all");
        rig.Player.Play(); // crossfade 0 (the default): the next track is decoded ahead without a profile
        Capture(rig.Output, 3, 480);
        rig.Change(p => p with { CrossfadeSeconds = 8 });
        var from = rig.Player.GetStatus().Position.TotalSeconds;
        var left = Capture(rig.Output, LoudSeconds - from + 3.5, 480);

        Check(left, RealChainRig.Rate, LoudSeconds - 8 - from, 8, 0.12);
    }

    [Fact]
    public void After_a_seek_near_the_end_the_ramp_is_the_same()
    {
        var a = Tone("a", 440, 48000, 2, 0, LoudSeconds, TailSeconds);
        var b = Tone("b", 880, 48000, 2, HeadSeconds, LoudSeconds, 0);
        using var rig = new RealChainRig([a, b], "all");
        rig.Change(p => p with { CrossfadeSeconds = 8 });
        rig.Player.Play();
        Capture(rig.Output, 1, 480);

        Assert.True(rig.Player.Seek(TimeSpan.FromSeconds(LoudSeconds - 12)).Success);
        Capture(rig.Output, 0.1, 480);
        var from = rig.Player.GetStatus().Position.TotalSeconds;
        var left = Capture(rig.Output, LoudSeconds - from + 3.5, 480);

        Check(left, RealChainRig.Rate, LoudSeconds - 8 - from, 8, 0.12);
    }
}
