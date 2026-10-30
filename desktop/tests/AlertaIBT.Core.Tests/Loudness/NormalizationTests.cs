using AlertaIBT.Core.Loudness;
using AlertaIBT.Core.Player;
using AlertaIBT.Core.Settings;
using AlertaIBT.Core.Tests.Player;

namespace AlertaIBT.Core.Tests.Loudness;

/// <summary>The gain rule, the settings, and the gain as the mixer and the player apply it.</summary>
public sealed class NormalizationTests
{
    // ---- the rule ----

    [Theory]
    [InlineData(-8.0, 0.9, -16.0, -8.0, false)] // louder than the target: lowered, whatever the peak
    [InlineData(-30.0, 0.9, -16.0, 0.0, true)] // wants +14 dB but the peak 0.9 (-0.9 dBFS) is above -1 dBFS: no room at all
    [InlineData(-24.0, 0.05, -16.0, 8.0, false)] // quiet track with a low peak: the whole raise fits
    [InlineData(-24.0, 0.5, -16.0, 5.0205999, true)] // peak -6.02 dBFS: room is 5.02 dB, 8 wanted
    [InlineData(-16.0, 1.0, -16.0, 0.0, false)] // already there
    [InlineData(-14.0, 1.0, -16.0, -2.0, false)]
    public void Gain_is_target_minus_measured_with_the_boost_limited_by_the_peak(double measured, double peak, double target, double expectedDb, bool expectedCapped)
    {
        var gain = NormalizationGain.Compute(new TrackLoudness(measured, peak), target, enabled: true);

        Assert.True(gain.Measured);
        Assert.Equal(expectedDb, gain.GainDb, 6);
        Assert.Equal(expectedCapped, gain.Capped);
    }

    [Fact]
    public void After_the_gain_the_true_peak_never_passes_minus_one_dBFS_when_raising()
    {
        foreach (var lufs in new[] { -40.0, -30.0, -24.0, -20.0 })
        {
            foreach (var peak in new[] { 0.01, 0.1, 0.3, 0.5, 0.8, 0.95, 1.0 })
            {
                var gain = NormalizationGain.Compute(new TrackLoudness(lufs, peak), -14, enabled: true);
                var after = (20 * Math.Log10(peak)) + gain.GainDb;
                Assert.True(after <= NormalizationGain.PeakCeilingDb + 1e-9 || gain.GainDb == 0, $"{lufs} LUFS peak {peak}: {after:F2} dBFS");
            }
        }
    }

    [Fact]
    public void Off_or_unmeasured_means_zero_dB_and_the_attenuation_is_not_limited()
    {
        Assert.Equal(0, NormalizationGain.Compute(new TrackLoudness(-30, 0.1), -16, enabled: false).GainDb);
        Assert.False(NormalizationGain.Compute(null, -16, enabled: true).Measured);
        Assert.Equal(0, NormalizationGain.Compute(null, -16, enabled: true).GainDb);
        Assert.Equal(-44.0, NormalizationGain.Compute(new TrackLoudness(28, 1.0), -16, enabled: true).GainDb, 6);
        Assert.Equal(1f, NormalizationGain.None(-16).Linear);
        Assert.Equal(2f, new NormalizationGain(true, -22, 6.0206, false, -16).Linear, 3);
    }

    // ---- the settings ----

    [Fact]
    public void Defaults_are_on_minus_16_and_tags_on_also_for_files_that_have_no_value()
    {
        var defaults = new PlayerSettings();
        var loaded = SettingsJson.Load("{\"player\":{\"crossfadeSeconds\":5}}").Settings.Player;

        Assert.True(defaults.NormalizeEnabled);
        Assert.Equal(-16, defaults.NormalizeTargetLufs);
        Assert.True(defaults.WriteLoudnessTags);
        Assert.True(loaded.NormalizeEnabled);
        Assert.Equal(-16, loaded.NormalizeTargetLufs);
        Assert.True(loaded.WriteLoudnessTags);
    }

    [Fact]
    public void The_settings_roundtrip_and_an_odd_target_goes_to_the_nearest_menu_value()
    {
        var settings = SettingsJson.Load(null).Settings;
        var json = SettingsJson.Save(settings with { Player = settings.Player with { NormalizeEnabled = false, NormalizeTargetLufs = -23, WriteLoudnessTags = false } });
        var back = SettingsJson.Load(json).Settings.Player;

        Assert.False(back.NormalizeEnabled);
        Assert.Equal(-23, back.NormalizeTargetLufs);
        Assert.False(back.WriteLoudnessTags);
        Assert.Equal(-16, SettingsJson.Load("{\"player\":{\"normalizeTargetLufs\":-15.2}}").Settings.Player.NormalizeTargetLufs);
        Assert.Equal(-24, SettingsJson.Load("{\"player\":{\"normalizeTargetLufs\":-90}}").Settings.Player.NormalizeTargetLufs);
        Assert.Equal([-24.0, -23.0, -20.0, -18.0, -16.0, -14.0, -12.0], NormalizationGain.TargetChoices);
    }

    // ---- the mixer ----

    private const int Rate = 8000;

    private sealed class ConstantTrack(double seconds, float level) : IPcmTrack
    {
        private long _frame;

        public bool Disposed { get; private set; }

        public TimeSpan Duration => TimeSpan.FromSeconds(seconds);

        public TimeSpan Position => TimeSpan.FromSeconds((double)_frame / Rate);

        public int Read(float[] buffer, int offset, int count)
        {
            var total = (long)(seconds * Rate);
            var frames = (int)Math.Min(count / 2, total - _frame);
            for (var i = 0; i < frames * 2; i++)
            {
                buffer[offset + i] = level;
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

    [Fact]
    public void A_track_gain_multiplies_its_samples_and_the_volume_and_the_fade_multiply_on_top()
    {
        var mixer = new PlayerMixer(Rate, 2, action => action());
        mixer.SetVolume(0.5f, false, immediate: true);
        mixer.SetTrack(new ConstantTrack(2, 0.2f), 2f);

        var sound = Pull(mixer, 1.0);

        Assert.All(sound.Skip(100), s => Assert.Equal(0.2f * 2f * 0.5f, s, 4));
    }

    [Fact]
    public void Each_track_keeps_its_own_constant_gain_across_a_gapless_change()
    {
        var mixer = new PlayerMixer(Rate, 2, action => action());
        mixer.SetTrack(new ConstantTrack(1, 0.1f), 3f);
        mixer.SetNext(new ConstantTrack(1, 0.4f), null, 0.5f);

        var sound = Pull(mixer, 1.95);

        Assert.Equal(0.3f, sound[Rate / 2], 4);
        Assert.Equal(0.2f, sound[Rate + (Rate / 2)], 4);
        Assert.All(sound.Take(Rate), s => Assert.Equal(0.3f, s, 4));
        Assert.All(sound.Skip(Rate).Take(Rate - 400), s => Assert.Equal(0.2f, s, 4));
    }

    [Theory]
    [InlineData(CrossfadeCurve.Linear)]
    [InlineData(CrossfadeCurve.ConstantPower)]
    [InlineData(CrossfadeCurve.Intermediate)]
    public void In_a_crossfade_the_curve_is_unchanged_and_each_track_keeps_its_constant_gain(CrossfadeCurve curve)
    {
        var mixer = new PlayerMixer(Rate, 2, action => action());
        mixer.SetCrossfade(2);
        mixer.SetCrossfadeCurve(curve);
        mixer.SetTrack(new ConstantTrack(6, 0.1f), 4f); // outgoing: 0.4 at the output
        mixer.SetNext(new ConstantTrack(6, 0.5f), null, 0.2f); // incoming: 0.1 at the output

        var sound = Pull(mixer, 6.0);

        // The overlap is the last 2 s of the first track: 4 s to 6 s of the output. Mixed sample = in*0.1*curveIn + out*0.4*curveOut.
        for (var i = 0; i < 2 * Rate; i += 250)
        {
            var p = (double)i / (2 * Rate);
            var (outGain, inGain) = PlayerMixer.CrossfadeGains(p, curve);
            var expected = (0.1f * inGain) + (0.4f * outGain);
            Assert.Equal(expected, sound[(4 * Rate) + i], 3);
        }

        Assert.Equal(0.4f, sound[Rate * 3], 4); // before the overlap only the first track, at its own gain
        Assert.Equal(0.1f, sound[(6 * Rate) - 1], 2); // at the end only the incoming one, at its own
    }

    [Fact]
    public void Changing_the_gain_of_a_playing_track_ramps_over_300_ms_without_a_step()
    {
        var mixer = new PlayerMixer(Rate, 2, action => action());
        var track = new ConstantTrack(3, 0.2f);
        mixer.SetTrack(track, 1f);
        Pull(mixer, 0.5);

        mixer.SetTrackGain(track, 2f);
        var sound = Pull(mixer, 1.0);

        Assert.Equal(0.2f, sound[0], 2);
        var rampFrames = (int)(PlayerMixer.NormalizeRampSeconds * Rate);
        Assert.Equal(0.4f, sound[rampFrames + 50], 4);
        var biggestStep = 0f;
        for (var i = 1; i < sound.Length; i++)
        {
            biggestStep = Math.Max(biggestStep, Math.Abs(sound[i] - sound[i - 1]));
        }

        Assert.True(biggestStep < 0.2f / rampFrames * 1.5f, $"step {biggestStep}");
        Assert.Equal(2f, mixer.TrackGain(track));
    }

    [Fact]
    public void SetGains_retargets_the_current_and_the_waiting_track_and_skips_the_ones_it_has_no_answer_for()
    {
        var mixer = new PlayerMixer(Rate, 2, action => action());
        var current = new ConstantTrack(3, 0.2f);
        var next = new ConstantTrack(3, 0.2f);
        mixer.SetTrack(current, 1f);
        mixer.SetNext(next, null, 1f);

        mixer.SetGains(t => ReferenceEquals(t, current) ? 0.5f : null, immediate: true);

        Assert.Equal(0.5f, mixer.TrackGain(current));
        Assert.Equal(1f, mixer.TrackGain(next));
    }

    // ---- the player ----

    private sealed class FakeLoudness : ILoudnessProvider
    {
        public Dictionary<string, TrackLoudness> Known { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<string> Requested { get; } = [];

        public event Action<string>? Measured;

        public TrackLoudness? Get(string path) => Known.GetValueOrDefault(path);

        public void Request(string path) => Requested.Add(path);

        public void Raise(string path) => Measured?.Invoke(path);
    }

    private static string Track(int n) => $"{PlayerRig.Folder}/track {n:00}.flac";

    private static float[] Pump(PlayerRig rig, double seconds)
    {
        var output = rig.Backend.Output;
        var frames = (int)Math.Round(seconds * 48000);
        var result = new float[frames];
        for (var done = 0; done < frames; done += 480)
        {
            var block = output.Pump(Math.Min(480, frames - done));
            for (var i = 0; i < block.Length / 2; i++)
            {
                result[done + i] = block[i * 2];
            }
        }

        return result;
    }

    private static PlayerRig NewRig(FakeLoudness loudness, int tracks = 3)
    {
        var rig = new PlayerRig(tracks, loudness: loudness);
        rig.Backend.TrackFrames = 48000 * 4;
        rig.Backend.Level = 0.1f;
        rig.Change(p => p with { Shuffle = false, Repeat = "all", FadeInSeconds = 0, CrossfadeSeconds = 0, NormalizeEnabled = true, NormalizeTargetLufs = -16 });
        return rig;
    }

    [Fact]
    public void A_measured_track_plays_with_its_gain_and_the_status_tells_the_numbers()
    {
        var loudness = new FakeLoudness();
        loudness.Known[Track(1)] = new TrackLoudness(-22, 0.1); // +6 dB wanted, room is plenty
        using var rig = NewRig(loudness);

        rig.Player.Play();
        var sound = Pump(rig, 1.0);

        Assert.Equal(0.1f * (float)Math.Pow(10, 6.0 / 20), sound[^10], 3);
        var info = rig.Player.GetStatus().Normalization!;
        Assert.Equal(-22, info.MeasuredLufs);
        Assert.Equal(6.0, info.GainDb, 6);
        Assert.False(info.Capped);
        Assert.Equal(-16, info.TargetLufs);
    }

    [Fact]
    public void A_boost_that_would_clip_is_capped_and_the_status_says_so()
    {
        var loudness = new FakeLoudness();
        loudness.Known[Track(1)] = new TrackLoudness(-30, 0.5); // wants +14, the peak -6 dBFS leaves 5 dB
        using var rig = NewRig(loudness);

        rig.Player.Play();
        var sound = Pump(rig, 1.0);

        var info = rig.Player.GetStatus().Normalization!;
        Assert.True(info.Capped);
        Assert.Equal(5.0206, info.GainDb, 3);
        Assert.Equal(0.1f * (float)Math.Pow(10, 5.0206 / 20), sound[^10], 3);
    }

    [Fact]
    public void An_unmeasured_track_plays_at_zero_dB_is_requested_and_keeps_zero_dB_even_when_measured_meanwhile()
    {
        var loudness = new FakeLoudness();
        using var rig = NewRig(loudness);

        rig.Player.Play();
        Pump(rig, 0.5);
        loudness.Known[Track(1)] = new TrackLoudness(-22, 0.1);
        loudness.Raise(Track(1));
        var sound = Pump(rig, 0.5);

        Assert.Contains(Track(1), loudness.Requested);
        Assert.All(sound.Skip(100), s => Assert.Equal(0.1f, s, 4)); // the song does not jump in level halfway
        var info = rig.Player.GetStatus().Normalization!;
        Assert.Null(info.MeasuredLufs);
        Assert.Equal(0, info.GainDb);
        Assert.Equal(-22, info.LatestLufs);
    }

    [Fact]
    public void The_waiting_next_track_picks_up_its_gain_when_it_is_measured_before_it_starts()
    {
        var loudness = new FakeLoudness();
        using var rig = NewRig(loudness);

        rig.Player.Play(); // track 02 is decoded ahead, unmeasured
        loudness.Known[Track(2)] = new TrackLoudness(-22, 0.1);
        loudness.Raise(Track(2));
        var sound = Pump(rig, 4.5); // the change at 4.0 s is gapless

        Assert.Equal(0.1f, sound[48000], 4);
        Assert.Equal(0.1f * (float)Math.Pow(10, 6.0 / 20), sound[(int)(4.3 * 48000)], 3);
    }

    [Fact]
    public void Changing_the_target_live_ramps_the_playing_track_to_the_new_gain_and_off_returns_to_zero_dB()
    {
        var loudness = new FakeLoudness();
        loudness.Known[Track(1)] = new TrackLoudness(-22, 0.1);
        using var rig = NewRig(loudness);
        rig.Player.Play();
        Pump(rig, 0.5);

        rig.Change(p => p with { NormalizeTargetLufs = -20 }); // +2 dB
        var sound = Pump(rig, 0.6);
        Assert.Equal(0.1f * (float)Math.Pow(10, 2.0 / 20), sound[^10], 3);
        Assert.Equal(2.0, rig.Player.GetStatus().Normalization!.GainDb, 6);

        rig.Change(p => p with { NormalizeEnabled = false });
        sound = Pump(rig, 0.6);
        Assert.Equal(0.1f, sound[^10], 3);
        Assert.False(rig.Player.GetStatus().Normalization!.Enabled);
    }

    [Fact]
    public void Two_tracks_of_very_different_level_come_out_at_the_same_level_when_the_headroom_allows()
    {
        var loudness = new FakeLoudness();
        // Constant 0.0316 (-30 dBFS) and constant 0.2512 (-12 dBFS): for a constant level the loudness in LUFS is the level in dBFS (a DC-like fake), peak = level.
        loudness.Known[Track(1)] = new TrackLoudness(-30, 0.0316);
        loudness.Known[Track(2)] = new TrackLoudness(-12, 0.2512);
        using var rig = NewRig(loudness);
        rig.Backend.LevelFor = path => path.Contains("01") ? 0.0316f : 0.2512f;

        rig.Player.Play();
        var first = Pump(rig, 2.0);
        var second = Pump(rig, 2.5)[^2000..];

        var firstDb = 20 * Math.Log10(Math.Abs(first[^100]));
        var secondDb = 20 * Math.Log10(Math.Abs(second[^100]));
        Assert.InRange(Math.Abs(firstDb - secondDb), 0, 0.5);
        Assert.InRange(firstDb, -16.5, -15.5); // both land on the target
    }

    [Fact]
    public void Without_a_loudness_service_the_player_plays_everything_at_zero_dB()
    {
        using var rig = new PlayerRig(3);
        rig.Backend.TrackFrames = 48000 * 2;
        rig.Backend.Level = 0.1f;
        rig.Change(p => p with { FadeInSeconds = 0 });

        rig.Player.Play();
        var sound = Pump(rig, 0.5);

        Assert.Equal(0.1f, sound[^10], 4);
    }
}
