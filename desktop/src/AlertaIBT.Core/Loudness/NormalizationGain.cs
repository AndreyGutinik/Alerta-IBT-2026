namespace AlertaIBT.Core.Loudness;

/// <summary>The loudness of a measured track, as the player needs it.</summary>
/// <param name="Lufs">Integrated loudness, LUFS.</param>
/// <param name="TruePeak">True peak, linear.</param>
public sealed record TrackLoudness(double Lufs, double TruePeak);

/// <summary>
/// The gain that brings one track to the target loudness: <c>target - measured</c> in dB. Lowering is free; raising stops where the (true) peak after the gain
/// would pass <see cref="PeakCeilingDb"/>, so nothing ever clips and no limiter is needed. The gain is one multiplication of the float samples: no
/// re-encoding, no dither, the files are never touched.
/// </summary>
/// <param name="Measured">Whether the track had a measurement when it started (an unmeasured track plays at 0 dB).</param>
/// <param name="MeasuredLufs">The measured loudness, or <c>null</c>.</param>
/// <param name="GainDb">The gain applied, dB (0 when off or unmeasured).</param>
/// <param name="Capped">Whether a raise was cut short by the peak.</param>
/// <param name="TargetLufs">The target the gain was computed for.</param>
public readonly record struct NormalizationGain(bool Measured, double? MeasuredLufs, double GainDb, bool Capped, double TargetLufs)
{
    /// <summary>The highest peak (dBFS) a track may reach after a raise: -1 dBFS.</summary>
    public const double PeakCeilingDb = -1.0;

    /// <summary>The targets the menu offers (LUFS).</summary>
    public static readonly IReadOnlyList<double> TargetChoices = [-24, -23, -20, -18, -16, -14, -12];

    /// <summary>The default target.</summary>
    public const double DefaultTargetLufs = -16;

    /// <summary>The multiplication to apply to the samples.</summary>
    public float Linear => (float)Math.Pow(10.0, GainDb / 20.0);

    /// <summary>No correction (off, or the track is not measured yet).</summary>
    public static NormalizationGain None(double targetLufs) => new(false, null, 0, false, targetLufs);

    /// <summary>The gain of a track.</summary>
    /// <param name="loudness">Its measurement, or <c>null</c> when it has none yet.</param>
    /// <param name="targetLufs">The target loudness.</param>
    /// <param name="enabled">Whether the equalization is on.</param>
    public static NormalizationGain Compute(TrackLoudness? loudness, double targetLufs, bool enabled)
    {
        if (!enabled || loudness is null || !double.IsFinite(loudness.Lufs) || !double.IsFinite(targetLufs))
        {
            return None(targetLufs);
        }

        var wanted = targetLufs - loudness.Lufs;
        var capped = false;
        var gain = wanted;
        if (wanted > 0 && loudness.TruePeak > 0)
        {
            var peakDb = 20 * Math.Log10(loudness.TruePeak);
            var room = Math.Max(0, PeakCeilingDb - peakDb);
            if (wanted > room)
            {
                gain = room;
                capped = true;
            }
        }

        return new NormalizationGain(true, loudness.Lufs, gain, capped, targetLufs);
    }
}

/// <summary>What the screen says about the loudness of the track that is playing.</summary>
/// <param name="Enabled">Whether "Igualar volume" is on.</param>
/// <param name="TargetLufs">The target loudness.</param>
/// <param name="MeasuredLufs">The loudness the gain was computed from, or <c>null</c> when the track started without a measurement (it plays at 0 dB).</param>
/// <param name="GainDb">The gain applied, dB.</param>
/// <param name="Capped">Whether a raise was limited by the peak.</param>
/// <param name="LatestLufs">The loudness the cache holds now (it may exist even when the track started without it and plays at 0 dB until its next turn).</param>
public sealed record NormalizationInfo(bool Enabled, double TargetLufs, double? MeasuredLufs, double GainDb, bool Capped, double? LatestLufs);
