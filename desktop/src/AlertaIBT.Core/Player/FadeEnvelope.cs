namespace AlertaIBT.Core.Player;

/// <summary>
/// A gain ramp counted in sample frames, so a fade is exact to the sample and never clicks: it moves the gain from where it is to a target
/// over a number of frames, then holds. The curve is quadratic, not a straight line: the ear hears level on a logarithmic scale, so a
/// linear-in-amplitude fade-out stays loud for most of its length and then drops off a cliff in the last second ("it counts 5 seconds and
/// stops abruptly"), and a linear fade-in is already at 80% after a third of its time. Squaring the progress spreads the audible change over
/// the whole fade. Not thread-safe (the mixer owns it behind its lock).
/// </summary>
public sealed class FadeEnvelope
{
    /// <summary>The shortest ramp, in milliseconds: a "dry" start or stop still ramps over this, so there is never a click.</summary>
    public const int MinRampMs = 10;

    private float _gain = 1f;
    private float _from = 1f;
    private float _target = 1f;
    private int _total;
    private int _remaining;

    /// <summary>The gain now, 0 to 1.</summary>
    public float Gain => _gain;

    /// <summary>The gain the ramp is heading to (or holding).</summary>
    public float Target => _target;

    /// <summary>Whether a ramp is still running.</summary>
    public bool IsFading => _remaining > 0;

    /// <summary>The frames left in the ramp.</summary>
    public int RemainingFrames => _remaining;

    /// <summary>How many frames a fade of <paramref name="seconds"/> takes (never less than <see cref="MinRampMs"/>).</summary>
    public static int FramesFor(double seconds, int sampleRate)
    {
        var min = sampleRate * MinRampMs / 1000;
        return Math.Max(min, (int)Math.Round(Math.Max(0, seconds) * sampleRate));
    }

    /// <summary>Jumps to <paramref name="gain"/> at once and holds it.</summary>
    public void Set(float gain)
    {
        _gain = Math.Clamp(gain, 0f, 1f);
        _from = _gain;
        _target = _gain;
        _remaining = 0;
        _total = 0;
    }

    /// <summary>Starts a ramp from the current gain to <paramref name="target"/> over <paramref name="frames"/> frames (at least one).</summary>
    public void FadeTo(float target, int frames)
    {
        _from = _gain;
        _target = Math.Clamp(target, 0f, 1f);
        _total = Math.Max(1, frames);
        _remaining = _total;
    }

    /// <summary>The gain at <paramref name="progress"/> (0 to 1) of a ramp from <paramref name="from"/> to <paramref name="to"/>: quadratic, rising slowly at first (fade-in) or falling quickly at first (fade-out) and always ending gently.</summary>
    public static float Curve(float from, float to, float progress)
    {
        if (to >= from)
        {
            return from + ((to - from) * progress * progress);
        }

        var left = 1f - progress;
        return to + ((from - to) * left * left);
    }

    /// <summary>The gain for the next frame; the ramp advances by one frame.</summary>
    public float Next()
    {
        if (_remaining > 0)
        {
            _remaining--;
            _gain = _remaining == 0 ? _target : Curve(_from, _target, 1f - ((float)_remaining / _total));
        }

        return _gain;
    }
}
