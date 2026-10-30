namespace AlertaIBT.Core.Settings;

/// <summary>The shape of the crossfade ("mescla"): how fast the outgoing track falls and the incoming one rises.</summary>
public enum CrossfadeCurve
{
    /// <summary>Straight lines (1-p, p): the outgoing track falls from the start; the middle dips 3 dB. The default.</summary>
    Linear = 0,

    /// <summary>Constant power (cos, sin): the loudness stays even, but the ends are flat (the outgoing track barely falls at first).</summary>
    ConstantPower = 1,

    /// <summary>The 50/50 blend of the two: the outgoing track clearly falls from the start and the middle dips only about 1.4 dB.</summary>
    Intermediate = 2,
}

/// <summary>The gains of each <see cref="CrossfadeCurve"/> (pure) and its text in the settings file.</summary>
public static class CrossfadeCurves
{
    /// <summary>The gains of the outgoing and the incoming track at <paramref name="progress"/> (0 to 1, clamped): exactly 1/0 at 0 and 0/1 at 1.</summary>
    public static (float Outgoing, float Incoming) Gains(CrossfadeCurve curve, double progress)
    {
        var p = Math.Clamp(double.IsNaN(progress) ? 0 : progress, 0, 1);
        if (p <= 0)
        {
            return (1f, 0f);
        }

        if (p >= 1)
        {
            return (0f, 1f);
        }

        var angle = p * (Math.PI / 2);
        var (cos, sin) = (Math.Cos(angle), Math.Sin(angle));
        return curve switch
        {
            CrossfadeCurve.ConstantPower => ((float)cos, (float)sin),
            CrossfadeCurve.Intermediate => ((float)((0.5 * (1 - p)) + (0.5 * cos)), (float)((0.5 * p) + (0.5 * sin))),
            _ => ((float)(1 - p), (float)p),
        };
    }

    /// <summary>The text stored in the settings file.</summary>
    public static string ToText(CrossfadeCurve curve) => curve switch
    {
        CrossfadeCurve.ConstantPower => "constantPower",
        CrossfadeCurve.Intermediate => "intermediate",
        _ => "linear",
    };

    /// <summary>Reads the stored text; anything unknown or missing is <see cref="CrossfadeCurve.Linear"/>.</summary>
    public static CrossfadeCurve Parse(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        "constantpower" => CrossfadeCurve.ConstantPower,
        "intermediate" => CrossfadeCurve.Intermediate,
        _ => CrossfadeCurve.Linear,
    };
}
