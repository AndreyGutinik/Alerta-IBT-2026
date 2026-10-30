using AlertaIBT.Core.Settings;
using AlertaIBT.Core.Player;

namespace AlertaIBT.Core.Tests.Player;

// The mixer's gapless change and its constant-power crossfade, measured on the samples it produces (two pure tones that do not interfere:
// 440 Hz is the outgoing track, 660 Hz the incoming one, so the level of each can be read apart with a Goertzel filter).
public sealed class CrossfadeMixerTests
{
    private const int Rate = 8000;

    private sealed class SineTrack(int rate, double seconds, double frequency, double amplitude) : IPcmTrack
    {
        private long _frame;

        public bool Disposed { get; private set; }

        public TimeSpan Duration => TimeSpan.FromSeconds(seconds);

        public TimeSpan Position => TimeSpan.FromSeconds((double)_frame / rate);

        public int Read(float[] buffer, int offset, int count)
        {
            var total = (long)(seconds * rate);
            var frames = (int)Math.Min(count / 2, total - _frame);
            for (var i = 0; i < frames; i++)
            {
                var value = (float)(amplitude * Math.Sin(2 * Math.PI * frequency * (_frame + i) / rate));
                buffer[offset + (i * 2)] = value;
                buffer[offset + (i * 2) + 1] = value;
            }

            _frame += frames;
            return frames * 2;
        }

        public void Seek(TimeSpan position) => _frame = (long)(position.TotalSeconds * rate);

        public void Dispose() => Disposed = true;
    }

    private static PlayerMixer NewMixer() => new(Rate, 2, action => action());

    // Pulls `seconds` of sound in blocks of `block` frames and returns the left channel.
    private static float[] Pull(PlayerMixer mixer, double seconds, int block = 480)
    {
        var frames = (int)Math.Round(seconds * Rate);
        var result = new float[frames];
        var buffer = new float[block * 2];
        for (var done = 0; done < frames;)
        {
            var n = Math.Min(block, frames - done);
            mixer.Read(buffer, 0, n * 2);
            for (var i = 0; i < n; i++)
            {
                result[done + i] = buffer[i * 2];
            }

            done += n;
        }

        return result;
    }

    // The amplitude of `frequency` in x[start..start+length) (a whole number of its cycles).
    private static double Amplitude(float[] x, int start, int length, double frequency)
    {
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

    private static double Rms(float[] x, int start, int length)
    {
        double sum = 0;
        for (var i = 0; i < length; i++)
        {
            sum += (double)x[start + i] * x[start + i];
        }

        return Math.Sqrt(sum / length);
    }

    // ---- gapless ----

    [Fact]
    public void With_no_crossfade_the_next_track_follows_with_no_gap_inside_the_same_read()
    {
        var mixer = NewMixer();
        var advanced = new List<IPcmTrack>();
        var ended = 0;
        mixer.TrackAdvanced += advanced.Add;
        mixer.TrackEnded += () => ended++;
        var first = new SineTrack(Rate, 1, 440, 0.5);
        var second = new SineTrack(Rate, 1, 660, 0.5);
        mixer.SetTrack(first);
        mixer.SetNext(second);

        var sound = Pull(mixer, 2.0, block: 441); // 441 does not divide a second: the boundary falls in the middle of a read

        Assert.Single(advanced);
        Assert.Same(second, advanced[0]);
        Assert.True(first.Disposed);
        // The first 100 ms of each track are not silent and no 10 ms window of the whole run is quiet: no gap.
        for (var start = 0; start + 80 <= sound.Length - 100; start += 80)
        {
            Assert.True(Rms(sound, start, 80) > 0.25, $"a quiet window at {start / (double)Rate:F3} s");
        }

        Assert.Equal(0, ended); // the second track ends exactly at 2.0 s: the next read finds the end
        Pull(mixer, 0.1);
        Assert.Equal(1, ended);
    }

    [Fact]
    public void Without_a_next_track_the_end_is_announced_once_and_nothing_is_mixed_into_nothing()
    {
        var mixer = NewMixer();
        mixer.SetCrossfade(1);
        mixer.SetCrossfadeCurve(CrossfadeCurve.ConstantPower);
        var ended = 0;
        mixer.TrackEnded += () => ended++;
        mixer.SetTrack(new SineTrack(Rate, 3, 440, 0.5));

        var sound = Pull(mixer, 3.5);

        Assert.Equal(1, ended);
        Assert.True(Rms(sound, (int)(2.8 * Rate), 80) > 0.3, "the last second is not faded out: a crossfade into nothing does not exist");
        Assert.Equal(0, Rms(sound, (int)(3.2 * Rate), 80), 4);
    }

    // ---- crossfade ----

    [Fact]
    public void The_crossfade_mixes_with_constant_power_the_outgoing_falls_as_a_cosine_and_the_incoming_rises_as_a_sine()
    {
        var mixer = NewMixer();
        mixer.SetCrossfade(2);
        mixer.SetCrossfadeCurve(CrossfadeCurve.ConstantPower);
        mixer.SetTrack(new SineTrack(Rate, 6, 440, 0.5));
        mixer.SetNext(new SineTrack(Rate, 6, 660, 0.5));

        var sound = Pull(mixer, 6 + 4); // 4 s into the second track: the overlap is 4..6 s

        var window = Rate / 10;
        var lastTotal = -1.0;
        var lastOut = 2.0;
        var lastIn = -1.0;
        for (var start = 4 * Rate; start < 6 * Rate; start += window)
        {
            var progress = ((start + (window / 2.0)) / Rate - 4) / 2;
            var outgoing = Amplitude(sound, start, window, 440) / 0.5;
            var incoming = Amplitude(sound, start, window, 660) / 0.5;

            Assert.Equal(Math.Cos(progress * Math.PI / 2), outgoing, 1);
            Assert.Equal(Math.Sin(progress * Math.PI / 2), incoming, 1);
            Assert.Equal(1.0, (outgoing * outgoing) + (incoming * incoming), 1); // constant power
            Assert.True(outgoing < lastOut, "the outgoing track falls");
            Assert.True(incoming > lastIn, "the incoming track rises");
            lastOut = outgoing;
            lastIn = incoming;
            lastTotal = (outgoing * outgoing) + (incoming * incoming);
        }

        Assert.InRange(lastTotal, 0.95, 1.05);
        // Before and after the overlap each track plays alone at full level.
        Assert.Equal(0.5, Amplitude(sound, 3 * Rate, window, 440), 2);
        Assert.Equal(0.5, Amplitude(sound, 7 * Rate, window, 660), 2);
        Assert.Equal(0, Amplitude(sound, 7 * Rate, window, 440), 2);
    }

    [Fact]
    public void The_loudness_of_the_mix_stays_level_through_the_whole_overlap_no_dip_in_the_middle()
    {
        var mixer = NewMixer();
        mixer.SetCrossfade(3);
        mixer.SetCrossfadeCurve(CrossfadeCurve.ConstantPower);
        mixer.SetTrack(new SineTrack(Rate, 8, 440, 0.5));
        mixer.SetNext(new SineTrack(Rate, 8, 660, 0.5));

        var sound = Pull(mixer, 8);

        var window = Rate / 5; // 0.2 s: whole cycles of both tones
        var reference = Rms(sound, 0, window);
        for (var start = 5 * Rate; start + window <= 8 * Rate; start += window)
        {
            var db = 20 * Math.Log10(Rms(sound, start, window) / reference);
            Assert.InRange(db, -0.5, 0.5); // a straight line would dip by 3 dB at the middle
        }
    }

    [Fact]
    public void The_crossfade_begins_at_the_length_before_the_end_and_the_screen_follows_at_its_first_sample()
    {
        var mixer = NewMixer();
        mixer.SetCrossfade(1);
        mixer.SetCrossfadeCurve(CrossfadeCurve.ConstantPower);
        var incoming = new SineTrack(Rate, 5, 660, 0.5);
        var advancedAt = -1.0;
        var elapsed = 0.0;
        mixer.TrackAdvanced += _ => advancedAt = elapsed;
        mixer.SetTrack(new SineTrack(Rate, 4, 440, 0.5));
        mixer.SetNext(incoming);

        for (var i = 0; i < 400; i++)
        {
            Pull(mixer, 0.01);
            elapsed += 0.01;
            if (advancedAt >= 0)
            {
                break;
            }
        }

        Assert.InRange(advancedAt, 2.98, 3.05); // 4 s track, 1 s overlap: it starts at 3 s
        Assert.Same(incoming, mixer.Track);
        Assert.Equal(TimeSpan.FromSeconds(5), mixer.Duration);
        Assert.True(mixer.IsCrossfading);
        Assert.InRange(mixer.Position.TotalSeconds, 0, 0.05);
    }

    [Theory]
    [InlineData(2.0, 1.2, 0.6)]
    [InlineData(5.0, 8.0, 4.0)]
    [InlineData(3.0, 10.0, 3.0)]
    [InlineData(0.0, 10.0, 0.0)]
    public void The_length_is_clamped_to_half_of_the_track(double wanted, double trackSeconds, double expected)
    {
        Assert.Equal(expected, PlayerMixer.ClampCrossfade(wanted, TimeSpan.FromSeconds(trackSeconds), TimeSpan.Zero), 6);
    }

    [Fact]
    public void A_track_shorter_than_twice_the_crossfade_crosses_over_half_of_its_length()
    {
        var mixer = NewMixer();
        mixer.SetCrossfade(2); // wanted 2 s but the track is 1.2 s: 0.6 s applies
        mixer.SetCrossfadeCurve(CrossfadeCurve.ConstantPower);
        var advancedAt = -1.0;
        var elapsed = 0.0;
        mixer.TrackAdvanced += _ => advancedAt = elapsed;
        mixer.SetTrack(new SineTrack(Rate, 1.2, 440, 0.5));
        mixer.SetNext(new SineTrack(Rate, 6, 660, 0.5));

        for (var i = 0; i < 120 && advancedAt < 0; i++)
        {
            Pull(mixer, 0.01);
            elapsed += 0.01;
        }

        Assert.InRange(advancedAt, 0.58, 0.65);
    }

    [Fact]
    public void A_short_incoming_track_also_limits_the_overlap()
    {
        Assert.Equal(0.5, PlayerMixer.ClampCrossfade(4, TimeSpan.FromSeconds(100), TimeSpan.FromSeconds(1)), 6);
    }

    [Fact]
    public void The_outgoing_track_is_disposed_when_the_overlap_ends_and_not_before()
    {
        var mixer = NewMixer();
        mixer.SetCrossfade(1);
        mixer.SetCrossfadeCurve(CrossfadeCurve.ConstantPower);
        var outgoing = new SineTrack(Rate, 4, 440, 0.5);
        mixer.SetTrack(outgoing);
        mixer.SetNext(new SineTrack(Rate, 4, 660, 0.5));

        Pull(mixer, 3.5);
        Assert.False(outgoing.Disposed);
        Assert.True(mixer.IsCrossfading);

        Pull(mixer, 0.6);

        Assert.True(outgoing.Disposed);
        Assert.False(mixer.IsCrossfading);
    }

    // ---- fade, volume, seek during the mix ----

    [Fact]
    public void A_pause_fade_during_the_crossfade_takes_both_tracks_down_together()
    {
        var mixer = NewMixer();
        mixer.SetCrossfade(2);
        mixer.SetCrossfadeCurve(CrossfadeCurve.ConstantPower);
        mixer.SetTrack(new SineTrack(Rate, 6, 440, 0.5));
        mixer.SetNext(new SineTrack(Rate, 6, 660, 0.5));
        var sound = Pull(mixer, 4.5); // half a second into the overlap
        Assert.True(mixer.IsCrossfading);
        var done = false;

        mixer.FadeTo(0f, Rate, () => done = true);
        var fading = Pull(mixer, 1.0);

        Assert.True(done);
        var window = Rate / 10;
        double previous = double.MaxValue;
        for (var start = 0; start + window <= fading.Length; start += window)
        {
            var total = Math.Sqrt(Math.Pow(Amplitude(fading, start, window, 440), 2) + Math.Pow(Amplitude(fading, start, window, 660), 2));
            Assert.True(total <= previous + 0.01, "the mix only goes down");
            previous = total;
        }

        Assert.True(previous < 0.01, "silence at the end: both tracks are out");
        Assert.NotEmpty(sound);
    }

    [Fact]
    public void The_volume_and_the_mute_act_on_the_mix()
    {
        var mixer = NewMixer();
        mixer.SetCrossfade(2);
        mixer.SetCrossfadeCurve(CrossfadeCurve.ConstantPower);
        mixer.SetTrack(new SineTrack(Rate, 6, 440, 0.5));
        mixer.SetNext(new SineTrack(Rate, 6, 660, 0.5));
        mixer.SetVolume(0.5f, muted: false, immediate: true);

        var sound = Pull(mixer, 6);

        var window = Rate / 10;
        for (var start = 4 * Rate; start < 6 * Rate; start += window)
        {
            var outgoing = Amplitude(sound, start, window, 440);
            var incoming = Amplitude(sound, start, window, 660);
            Assert.InRange((outgoing * outgoing) + (incoming * incoming), 0.5 * 0.5 * 0.25 * 0.9, 0.5 * 0.5 * 0.25 * 1.1);
        }

        mixer.SetVolume(0.5f, muted: true, immediate: true);
        Assert.All(Pull(mixer, 0.1), sample => Assert.Equal(0f, sample));
    }

    [Fact]
    public void A_seek_during_the_crossfade_cancels_it_the_sought_track_plays_alone_and_the_other_is_disposed()
    {
        var mixer = NewMixer();
        mixer.SetCrossfade(2);
        mixer.SetCrossfadeCurve(CrossfadeCurve.ConstantPower);
        var outgoing = new SineTrack(Rate, 6, 440, 0.5);
        var incoming = new SineTrack(Rate, 60, 660, 0.5);
        mixer.SetTrack(outgoing);
        mixer.SetNext(incoming);
        Pull(mixer, 5);
        Assert.True(mixer.IsCrossfading);

        Assert.True(mixer.Seek(TimeSpan.FromSeconds(30)));
        var sound = Pull(mixer, 1);

        Assert.False(mixer.IsCrossfading);
        Assert.True(outgoing.Disposed);
        Assert.False(incoming.Disposed);
        var tail = Rate / 2;
        Assert.Equal(0.5, Amplitude(sound, Rate - tail, tail, 660), 2); // full level, alone
        Assert.Equal(0, Amplitude(sound, Rate - tail, tail, 440), 2);
        Assert.InRange(mixer.Position.TotalSeconds, 30.5, 31.1);
    }

    [Fact]
    public void A_seek_before_the_crossfade_just_moves_the_track_and_the_overlap_still_comes_at_the_end()
    {
        var mixer = NewMixer();
        mixer.SetCrossfade(1);
        mixer.SetCrossfadeCurve(CrossfadeCurve.ConstantPower);
        var advanced = 0;
        mixer.TrackAdvanced += _ => advanced++;
        mixer.SetTrack(new SineTrack(Rate, 10, 440, 0.5));
        mixer.SetNext(new SineTrack(Rate, 10, 660, 0.5));
        Pull(mixer, 1);

        mixer.Seek(TimeSpan.FromSeconds(7));
        Pull(mixer, 1.8); // 7 s plus 1.8 s, less the 12 ms duck: about 8.8 s of the track, the overlap starts at 9 s

        Assert.Equal(0, advanced);
        Pull(mixer, 0.4);
        Assert.Equal(1, advanced);
    }

    // ---- ownership ----

    [Fact]
    public void Replacing_or_clearing_the_tracks_disposes_the_current_the_waiting_and_the_outgoing_ones()
    {
        var mixer = NewMixer();
        mixer.SetCrossfade(1);
        mixer.SetCrossfadeCurve(CrossfadeCurve.ConstantPower);
        var a = new SineTrack(Rate, 4, 440, 0.5);
        var b = new SineTrack(Rate, 4, 660, 0.5);
        var c = new SineTrack(Rate, 4, 880, 0.5);
        mixer.SetTrack(a);
        mixer.SetNext(b);
        Pull(mixer, 3.5); // b is incoming now, a is going out
        mixer.SetNext(c);

        mixer.SetTrack(null);

        Assert.True(a.Disposed);
        Assert.True(b.Disposed);
        Assert.True(c.Disposed);
        Assert.Null(mixer.Track);
        Assert.False(mixer.HasNext);
    }

    [Fact]
    public void A_waiting_track_that_is_replaced_or_cleared_is_disposed()
    {
        var mixer = NewMixer();
        mixer.SetTrack(new SineTrack(Rate, 4, 440, 0.5));
        var first = new SineTrack(Rate, 4, 660, 0.5);
        var second = new SineTrack(Rate, 4, 880, 0.5);

        mixer.SetNext(first);
        mixer.SetNext(second);
        Assert.True(first.Disposed);
        Assert.True(mixer.HasNext);

        mixer.ClearNext();
        Assert.True(second.Disposed);
        Assert.False(mixer.HasNext);
    }

    [Fact]
    public void The_gain_curve_is_constant_power_at_every_point()
    {
        for (var i = 0; i <= 1000; i++)
        {
            var (outgoing, incoming) = PlayerMixer.CrossfadeGains(i / 1000.0);
            Assert.Equal(1.0, (outgoing * outgoing) + (incoming * incoming), 5);
        }

        Assert.Equal((1f, 0f), PlayerMixer.CrossfadeGains(0));
        Assert.Equal(0f, PlayerMixer.CrossfadeGains(1).Outgoing, 5);
    }
}
