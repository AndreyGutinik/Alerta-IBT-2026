using AlertaIBT.Core.Loudness;

namespace AlertaIBT.Core.Tests.Loudness;

/// <summary>Accuracy of the BS.1770-4 / EBU R128 meter against the values the recommendation and EBU Tech 3341 give for synthetic signals.</summary>
public sealed class LoudnessMeterTests
{
    /// <summary>A sine of <paramref name="peakDb"/> dBFS (peak) in every channel, <paramref name="seconds"/> long; silence when peakDb is null.</summary>
    internal static float[] Sine(int rate, int channels, double seconds, double? peakDb, double frequency = 997, double phase = 0)
    {
        var frames = (int)Math.Round(seconds * rate);
        var data = new float[frames * channels];
        if (peakDb is not { } db)
        {
            return data;
        }

        var amplitude = Math.Pow(10, db / 20);
        for (var i = 0; i < frames; i++)
        {
            var value = (float)(amplitude * Math.Sin((2 * Math.PI * frequency * i / rate) + phase));
            for (var c = 0; c < channels; c++)
            {
                data[(i * channels) + c] = value;
            }
        }

        return data;
    }

    internal static LoudnessResult Measure(int rate, int channels, params float[][] parts)
    {
        var meter = new LoudnessMeter(rate, channels);
        foreach (var part in parts)
        {
            meter.Add(part);
        }

        return meter.Finish();
    }

    [Fact]
    public void KWeighting_coefficients_match_the_table_of_the_recommendation_at_48_kHz()
    {
        var k = LoudnessMeter.KWeightingCoefficients(48000);

        // BS.1770-4 table 1 (shelf) and table 2 (high pass).
        Assert.Equal(1.53512485958697, k[0], 9);
        Assert.Equal(-2.69169618940638, k[1], 9);
        Assert.Equal(1.19839281085285, k[2], 9);
        Assert.Equal(-1.69065929318241, k[3], 9);
        Assert.Equal(0.73248077421585, k[4], 9);
        Assert.Equal(1.0, k[5], 12);
        Assert.Equal(-2.0, k[6], 12);
        Assert.Equal(1.0, k[7], 12);
        Assert.Equal(-1.99004745483398, k[8], 9);
        Assert.Equal(0.99007225036621, k[9], 9);
    }

    [Theory]
    [InlineData(44100)]
    [InlineData(48000)]
    [InlineData(96000)]
    public void A_full_scale_997_Hz_stereo_sine_reads_zero_LUFS(int rate)
    {
        var result = Measure(rate, 2, Sine(rate, 2, 20, 0));

        Assert.NotNull(result.IntegratedLufs);
        Assert.InRange(result.IntegratedLufs!.Value, -0.05, 0.05);
    }

    [Theory]
    [InlineData(44100, -23.0)]
    [InlineData(48000, -23.0)]
    [InlineData(96000, -23.0)]
    [InlineData(48000, -33.0)]
    [InlineData(48000, -18.0)]
    public void A_stereo_sine_at_a_level_reads_that_level_in_LUFS(int rate, double level)
    {
        // EBU Tech 3341 tests 1 and 2: 997 Hz, stereo, -23 / -33 dBFS must read -23.0 / -33.0 LUFS (tolerance 0.1).
        var result = Measure(rate, 2, Sine(rate, 2, 20, level));

        Assert.InRange(result.IntegratedLufs!.Value, level - 0.1, level + 0.1);
    }

    [Fact]
    public void Mono_is_measured_as_dual_mono_so_it_reads_like_the_same_sine_in_both_channels()
    {
        var mono = Measure(48000, 1, Sine(48000, 1, 20, -23));
        var stereo = Measure(48000, 2, Sine(48000, 2, 20, -23));

        Assert.InRange(mono.IntegratedLufs!.Value, -23.1, -22.9);
        Assert.Equal(stereo.IntegratedLufs!.Value, mono.IntegratedLufs.Value, 3);
    }

    [Fact]
    public void Quantizing_to_16_bits_does_not_move_the_reading()
    {
        var samples = Sine(44100, 2, 20, -20);
        var quantized = samples.Select(v => (float)(Math.Round(v * 32767.0) / 32768.0)).ToArray();

        var exact = Measure(44100, 2, samples).IntegratedLufs!.Value;
        var sixteen = Measure(44100, 2, quantized).IntegratedLufs!.Value;

        Assert.InRange(Math.Abs(exact - sixteen), 0, 0.01);
    }

    [Fact]
    public void Silence_before_and_after_is_removed_by_the_absolute_gate()
    {
        // The silence is below -70 LUFS: the reading is that of the sine alone (Tech 3341 test 7 idea: gated silence).
        var result = Measure(48000, 2, Sine(48000, 2, 10, null), Sine(48000, 2, 20, -23), Sine(48000, 2, 10, -90));

        Assert.InRange(result.IntegratedLufs!.Value, -23.1, -22.9);
    }

    [Fact]
    public void A_stretch_more_than_ten_LU_below_the_rest_is_removed_by_the_relative_gate()
    {
        // 20 s at -23 and 20 s at -36: the mean sits near -25.8, the relative gate near -35.8, so the quiet half drops out (Tech 3341 test 5 idea).
        var result = Measure(48000, 2, Sine(48000, 2, 20, -23), Sine(48000, 2, 20, -36));

        Assert.InRange(result.IntegratedLufs!.Value, -23.1, -22.9);
    }

    [Fact]
    public void Two_levels_inside_the_gates_average_in_power()
    {
        var result = Measure(48000, 2, Sine(48000, 2, 20, -23), Sine(48000, 2, 20, -29));
        var expected = 10 * Math.Log10(0.5 * (Math.Pow(10, -2.3) + Math.Pow(10, -2.9)));

        Assert.InRange(result.IntegratedLufs!.Value, expected - 0.15, expected + 0.15);
    }

    [Fact]
    public void Nothing_loud_enough_has_no_loudness()
    {
        Assert.Null(Measure(48000, 2, Sine(48000, 2, 10, null)).IntegratedLufs);
        Assert.Null(Measure(48000, 2, Sine(48000, 2, 10, -80)).IntegratedLufs);
        Assert.Null(Measure(48000, 2, Sine(48000, 2, 0.3, -10)).IntegratedLufs); // shorter than one 400 ms block
    }

    [Fact]
    public void Chunk_sizes_do_not_change_the_result()
    {
        var samples = Sine(44100, 2, 15, -21);
        var whole = Measure(44100, 2, samples);
        var meter = new LoudnessMeter(44100, 2);
        var rng = new Random(3);
        for (var at = 0; at < samples.Length;)
        {
            var take = Math.Min(samples.Length - at, 2 * rng.Next(1, 9000));
            meter.Add(samples.AsSpan(at, take));
            at += take;
        }

        var chunked = meter.Finish();
        Assert.Equal(whole.IntegratedLufs!.Value, chunked.IntegratedLufs!.Value, 9);
        Assert.Equal(whole.TruePeak, chunked.TruePeak, 9);
    }

    [Fact]
    public void Surround_channels_weigh_more_and_the_LFE_does_not_count()
    {
        static float[] OneChannel(int channel)
        {
            var mono = Sine(48000, 1, 20, -23);
            var data = new float[mono.Length * 6];
            for (var i = 0; i < mono.Length; i++)
            {
                data[(i * 6) + channel] = mono[i];
            }

            return data;
        }

        var center = Measure(48000, 6, OneChannel(2)).IntegratedLufs!.Value;
        var surround = Measure(48000, 6, OneChannel(4)).IntegratedLufs!.Value;
        var lfe = Measure(48000, 6, OneChannel(3)).IntegratedLufs;

        Assert.InRange(center, -26.1, -25.9); // one front channel: 3 dB below two
        Assert.InRange(surround - center, 1.45, 1.55); // 1.41 in power terms is +1.5 dB
        Assert.Null(lfe);
    }

    [Fact]
    public void Peaks_a_full_scale_sine_has_a_sample_and_true_peak_near_one()
    {
        var result = Measure(48000, 2, Sine(48000, 2, 5, 0));

        Assert.InRange(result.SamplePeak, 0.99, 1.0);
        Assert.InRange(result.TruePeak, 0.995, 1.01);
        Assert.True(result.TruePeak >= result.SamplePeak);
    }

    [Fact]
    public void The_true_peak_sees_the_intersample_peak_the_samples_miss()
    {
        // A full-scale sine at a quarter of the rate, 45 degrees off the samples: every sample is +-0.7071 but the wave reaches 1.0.
        var result = Measure(44100, 2, Sine(44100, 2, 2, 0, frequency: 11025, phase: Math.PI / 4));

        Assert.InRange(result.SamplePeak, 0.70, 0.72);
        Assert.InRange(result.TruePeak, 0.98, 1.02);
    }

    [Fact]
    public void Five_minutes_of_stereo_44_1_kHz_measure_in_a_few_seconds()
    {
        var samples = Sine(44100, 2, 10, -20);
        var meter = new LoudnessMeter(44100, 2);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 30; i++)
        {
            meter.Add(samples);
        }

        watch.Stop();
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), $"300 s took {watch.Elapsed.TotalSeconds:F1} s");
        Assert.InRange(meter.Finish().IntegratedLufs!.Value, -20.1, -19.9);
    }
}
