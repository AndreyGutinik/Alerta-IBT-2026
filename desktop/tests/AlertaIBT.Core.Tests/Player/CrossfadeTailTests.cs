using AlertaIBT.Core.Settings;
using AlertaIBT.Core.Player;

namespace AlertaIBT.Core.Tests.Player;

// Real tracks end in a quiet tail and may start with silence: anchored on the file's own ends, a crossfade mixes two near-silent stretches and
// sounds like "the first one fell before the second came in". These tests use synthetic tones with such tails and intros.
public sealed class CrossfadeTailTests
{
    private const int Rate = 8000;

    // A stereo tone: `silence` seconds of nothing, `loud` seconds at 0.5, then a tail at -68 dBFS up to the end. `header` is the length the header
    // claims, `real` is how long the decoder really delivers (the header of a FLAC is often rounded down).
    private sealed class TailTrack(double frequency, double silence, double loud, double header, double real) : IPcmTrack
    {
        private long _frame;

        public bool Disposed { get; private set; }

        public TimeSpan Duration => TimeSpan.FromSeconds(header);

        public TimeSpan Position => TimeSpan.FromSeconds((double)_frame / Rate);

        public int Read(float[] buffer, int offset, int count)
        {
            var total = (long)(real * Rate);
            var frames = (int)Math.Min(count / 2, total - _frame);
            for (var i = 0; i < frames; i++)
            {
                var t = (double)(_frame + i) / Rate;
                var amplitude = t < silence ? 0 : t < silence + loud ? 0.5 : 0.0004;
                var value = (float)(amplitude * Math.Sin(2 * Math.PI * frequency * t));
                buffer[offset + (i * 2)] = value;
                buffer[offset + (i * 2) + 1] = value;
            }

            _frame += frames;
            return frames * 2;
        }

        public void Seek(TimeSpan position) => _frame = (long)(position.TotalSeconds * Rate);

        public void Dispose() => Disposed = true;
    }

    private static float[] Pull(PlayerMixer mixer, double seconds)
    {
        var frames = (int)Math.Round(seconds * Rate);
        var result = new float[frames];
        var buffer = new float[480 * 2];
        for (var done = 0; done < frames;)
        {
            var n = Math.Min(480, frames - done);
            mixer.Read(buffer, 0, n * 2);
            for (var i = 0; i < n; i++)
            {
                result[done + i] = buffer[i * 2];
            }

            done += n;
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

    // A (440 Hz) is loud until 14 s, then a quiet tail up to its 20 s header; B (660 Hz) is the next track.
    private static (PlayerMixer Mixer, TailTrack A, TailTrack B) Setup(bool withProfiles, double crossfade, double bSilence = 0)
    {
        var mixer = new PlayerMixer(Rate, 2, action => action());
        var a = new TailTrack(440, 0, 14, 20, 20.6);
        var b = new TailTrack(660, bSilence, 30, 30 + bSilence, 30.5 + bSilence);
        mixer.SetCrossfade(crossfade);
        mixer.SetCrossfadeCurve(CrossfadeCurve.ConstantPower);
        mixer.SetTrack(a);
        if (withProfiles)
        {
            mixer.SetProfile(a, new TrackProfile(TimeSpan.Zero, TimeSpan.FromSeconds(14), TimeSpan.FromSeconds(20.6)));
            b.Seek(TimeSpan.FromSeconds(bSilence));
            mixer.SetNext(b, new TrackProfile(TimeSpan.FromSeconds(bSilence), TimeSpan.FromSeconds(30 + bSilence), TimeSpan.FromSeconds(30.5 + bSilence)));
        }
        else
        {
            mixer.SetNext(b);
        }

        return (mixer, a, b);
    }

    [Fact]
    public void Anchored_on_the_last_sound_both_tracks_are_audible_together_with_constant_power()
    {
        var (mixer, _, _) = Setup(withProfiles: true, crossfade: 4);

        var sound = Pull(mixer, 16); // the overlap is 10 s to 14 s (the last sound of A), not 16 s to 20 s

        Assert.Equal(0.5, Amplitude(sound, 8, 1, 440), 2);
        Assert.Equal(0, Amplitude(sound, 8, 1, 660), 2);
        var lastOut = 2.0;
        var lastIn = -1.0;
        for (var start = 10.0; start + 0.2 <= 14.0 + 1e-6; start += 0.2)
        {
            var progress = (start + 0.1 - 10) / 4;
            var outgoing = Amplitude(sound, start, 0.2, 440) / 0.5;
            var incoming = Amplitude(sound, start, 0.2, 660) / 0.5;
            Assert.Equal(Math.Cos(progress * Math.PI / 2), outgoing, 1);
            Assert.Equal(Math.Sin(progress * Math.PI / 2), incoming, 1);
            Assert.InRange((outgoing * outgoing) + (incoming * incoming), 0.95, 1.05);
            Assert.True(outgoing < lastOut && incoming > lastIn, $"the outgoing falls and the incoming rises at {start:F1} s");
            lastOut = outgoing;
            lastIn = incoming;
        }

        // At the midpoint of the overlap (12 s) both are clearly there and nothing is silent.
        Assert.True(Amplitude(sound, 11.8, 0.2, 440) > 0.5 * 0.6, "the outgoing track is still loud at the midpoint");
        Assert.True(Amplitude(sound, 11.8, 0.2, 660) > 0.5 * 0.6, "the incoming track is already loud at the midpoint");
        Assert.Equal(0.5, Amplitude(sound, 14.5, 1, 660), 2); // after the overlap B plays alone at full level
        Assert.True(Amplitude(sound, 14.5, 1, 440) < 0.001);
    }

    [Fact]
    public void Without_the_profile_the_header_anchoring_overlaps_the_quiet_tail_which_is_the_reported_bug()
    {
        var (mixer, _, _) = Setup(withProfiles: false, crossfade: 4);

        var sound = Pull(mixer, 20.5); // the overlap is 16 s to 20 s: A is already a -68 dB tail there

        Assert.Equal(0.5, Amplitude(sound, 13, 1, 440), 2);
        Assert.True(Amplitude(sound, 17.8, 0.2, 440) < 0.001, "the outgoing track is silent in the middle of the overlap: one fell before the other entered");
    }

    [Fact]
    public void The_incoming_track_enters_at_its_first_sound_not_at_its_leading_silence()
    {
        var (mixer, _, _) = Setup(withProfiles: true, crossfade: 4, bSilence: 3);

        var sound = Pull(mixer, 15);

        // 0.2 s into the overlap B already sounds (the 3 s of silence were skipped).
        Assert.True(Amplitude(sound, 10.2, 0.2, 660) > 0.02, "B is audible from the start of the overlap");
        Assert.True(Amplitude(sound, 12, 0.2, 660) > 0.5 * 0.6, "and loud at the midpoint");
    }

    [Fact]
    public void The_screen_switches_to_the_new_track_at_the_start_of_the_anchored_overlap()
    {
        var (mixer, _, b) = Setup(withProfiles: true, crossfade: 4);
        IPcmTrack? advanced = null;
        mixer.TrackAdvanced += t => advanced = t;

        Pull(mixer, 9.9);
        Assert.Null(advanced);
        Pull(mixer, 0.3); // 10.2 s

        Assert.Same(b, advanced);
        Assert.Same(b, mixer.Track);
    }

    [Fact]
    public void A_short_effective_length_clamps_the_crossfade_to_half_of_it()
    {
        var mixer = new PlayerMixer(Rate, 2, action => action());
        var a = new TailTrack(440, 0, 6, 20, 20.6); // only 6 s of sound before the tail
        var b = new TailTrack(660, 0, 30, 30, 30.5);
        mixer.SetCrossfade(12);
        mixer.SetCrossfadeCurve(CrossfadeCurve.ConstantPower);
        mixer.SetTrack(a);
        mixer.SetProfile(a, new TrackProfile(TimeSpan.Zero, TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(20.6)));
        mixer.SetNext(b, new TrackProfile(TimeSpan.Zero, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30.5)));

        var sound = Pull(mixer, 8);

        Assert.Equal(0.5, Amplitude(sound, 2, 0.5, 440), 2); // alone until 3 s (6 s / 2)
        Assert.Equal(0, Amplitude(sound, 2, 0.5, 660), 2);
        Assert.True(Amplitude(sound, 4.4, 0.2, 660) > 0.5 * 0.5);
    }

    [Fact]
    public void The_analyzer_finds_the_last_sound_the_first_sound_and_the_real_length()
    {
        // 2 s of silence, 10 s of sound, then a -68 dB tail; the header says 20 s but the decoder delivers 20.6 s.
        var profile = TrackAnalyzer.Analyze(() => new TailTrack(440, 2, 10, 20, 20.6), Rate, 2);

        Assert.InRange(profile.EffectiveStart.TotalSeconds, 1.95, 2.1);
        Assert.InRange(profile.EffectiveEnd.TotalSeconds, 11.95, 12.1);
        Assert.InRange(profile.RealDuration.TotalSeconds, 20.55, 20.65);
    }

    [Fact]
    public void The_analyzer_reads_only_the_head_and_the_tail_of_a_long_track()
    {
        var opened = new List<CountingTrack>();

        _ = TrackAnalyzer.Analyze(
            () =>
            {
                var track = new CountingTrack();
                opened.Add(track);
                return track;
            },
            Rate,
            2);

        var seconds = opened.Sum(t => t.Samples) / 2.0 / Rate;
        Assert.InRange(seconds, 0, 20 + 46 + 1); // 20 s of head plus 45 s of tail, never the 600 s
    }

    private sealed class CountingTrack : IPcmTrack
    {
        private long _frame;

        public long Samples { get; private set; }

        public TimeSpan Duration => TimeSpan.FromSeconds(600);

        public TimeSpan Position => TimeSpan.FromSeconds((double)_frame / Rate);

        public int Read(float[] buffer, int offset, int count)
        {
            var frames = (int)Math.Min(count / 2, (600L * Rate) - _frame);
            Array.Fill(buffer, 0.5f, offset, frames * 2);
            _frame += frames;
            Samples += frames * 2;
            return frames * 2;
        }

        public void Seek(TimeSpan position) => _frame = (long)(position.TotalSeconds * Rate);

        public void Dispose()
        {
        }
    }
}
