namespace AlertaIBT.Core.Player;

/// <summary>
/// A gain ramp counted in sample frames, so a fade is exact to the sample and never clicks: it moves the gain in equal steps from where it
/// is to a target over a number of frames, then holds. Not thread-safe (the mixer owns it behind its lock).
/// </summary>
public sealed class FadeEnvelope
{
    /// <summary>The shortest ramp, in milliseconds: a "dry" start or stop still ramps over this, so there is never a click.</summary>
    public const int MinRampMs = 10;

    private float _gain = 1f;
    private float _target = 1f;
    private float _step;
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
        _target = _gain;
        _remaining = 0;
        _step = 0;
    }

    /// <summary>Starts a ramp from the current gain to <paramref name="target"/> over <paramref name="frames"/> frames (at least one).</summary>
    public void FadeTo(float target, int frames)
    {
        _target = Math.Clamp(target, 0f, 1f);
        _remaining = Math.Max(1, frames);
        _step = (_target - _gain) / _remaining;
    }

    /// <summary>The gain for the next frame; the ramp advances by one frame.</summary>
    public float Next()
    {
        if (_remaining > 0)
        {
            _remaining--;
            _gain = _remaining == 0 ? _target : _gain + _step;
        }

        return _gain;
    }
}
