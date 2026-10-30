namespace AlertaIBT.Core.Loudness;

/// <summary>What a measurement found.</summary>
/// <param name="IntegratedLufs">The integrated loudness (ITU-R BS.1770-4 with the EBU R128 gates), or <c>null</c> when nothing was loud enough to measure (silence, or shorter than one 400 ms block).</param>
/// <param name="SamplePeak">The highest absolute sample, linear (1.0 is full scale).</param>
/// <param name="TruePeak">The highest absolute value after 4x oversampling, linear (never below <paramref name="SamplePeak"/>).</param>
/// <param name="Seconds">How much audio was measured.</param>
public readonly record struct LoudnessResult(double? IntegratedLufs, double SamplePeak, double TruePeak, double Seconds);

/// <summary>
/// Integrated loudness of a stream of float samples, ITU-R BS.1770-4 as used by EBU R128: K-weighting (a high shelf and a high pass, computed for the
/// real sample rate), 400 ms blocks every 100 ms (75 % overlap), the absolute gate at -70 LUFS and the relative gate 10 LU below the gated mean,
/// the channel weights of the recommendation (the surrounds count 1.41, the LFE is left out) and a 4x oversampled true peak. A mono stream is measured
/// as if both speakers played it (dual mono), because that is how the player sends it to a stereo output.
/// </summary>
public sealed class LoudnessMeter
{
    private const double Offset = -0.691;
    private const double AbsoluteGate = -70.0;
    private const double RelativeGate = -10.0;
    private const int Oversample = 4;
    private const int TapsPerPhase = 13;

    private static readonly double[][] PolyphaseTaps = BuildPolyphase();

    private readonly int _sampleRate;
    private readonly int _channels;
    private readonly double[] _weights;
    private readonly Biquad[] _shelf;
    private readonly Biquad[] _highPass;
    private readonly float[][] _history;
    private readonly int _subLength;
    private readonly List<double> _subEnergies = [];
    private double _subSum;
    private int _subCount;
    private double _samplePeak;
    private double _truePeak;
    private long _frames;
    private int _historyPos;

    /// <param name="sampleRate">Frames per second of the samples (any rate: the filters are computed for it).</param>
    /// <param name="channels">Samples per frame.</param>
    public LoudnessMeter(int sampleRate, int channels)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 8000);
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);

        _sampleRate = sampleRate;
        _channels = channels;
        _weights = ChannelWeights(channels);
        _shelf = new Biquad[channels];
        _highPass = new Biquad[channels];
        var shelf = Biquad.HighShelf(sampleRate);
        var highPass = Biquad.HighPass(sampleRate);
        for (var c = 0; c < channels; c++)
        {
            _shelf[c] = shelf;
            _highPass[c] = highPass;
        }

        _history = new float[channels][];
        for (var c = 0; c < channels; c++)
        {
            _history[c] = new float[TapsPerPhase];
        }

        _subLength = Math.Max(1, (int)Math.Round(sampleRate * 0.1));
    }

    /// <summary>The weight of each channel in the sum: 1 for the fronts, 1.41 for the surrounds, 0 for the LFE; 2 for a mono stream (dual mono).</summary>
    public static double[] ChannelWeights(int channels)
    {
        const double surround = 1.41421356237;
        return channels switch
        {
            1 => [2.0],
            2 => [1.0, 1.0],
            6 => [1.0, 1.0, 1.0, 0.0, surround, surround],
            8 => [1.0, 1.0, 1.0, 0.0, surround, surround, surround, surround],
            _ => Enumerable.Repeat(1.0, channels).ToArray(),
        };
    }

    /// <summary>The K-weighting coefficients for a rate: the shelf (b0 b1 b2 a1 a2) then the high pass (b0 b1 b2 a1 a2); for checking against the recommendation's table.</summary>
    internal static double[] KWeightingCoefficients(int sampleRate)
    {
        var shelf = Biquad.HighShelf(sampleRate);
        var highPass = Biquad.HighPass(sampleRate);
        return [.. shelf.Coefficients(), .. highPass.Coefficients()];
    }

    /// <summary>Adds interleaved samples (a whole number of frames; a trailing partial frame is ignored).</summary>
    public void Add(ReadOnlySpan<float> samples)
    {
        var frames = samples.Length / _channels;
        for (var f = 0; f < frames; f++)
        {
            var energy = 0.0;
            var slot = _historyPos;
            for (var c = 0; c < _channels; c++)
            {
                var x = samples[(f * _channels) + c];
                var abs = Math.Abs((double)x);
                if (abs > _samplePeak)
                {
                    _samplePeak = abs;
                }

                _history[c][slot] = x;
                var peak = TruePeakOf(_history[c], slot);
                if (peak > _truePeak)
                {
                    _truePeak = peak;
                }

                if (_weights[c] > 0)
                {
                    var y = _highPass[c].Process(_shelf[c].Process(x));
                    energy += _weights[c] * y * y;
                }
            }

            _historyPos = (slot + 1) % TapsPerPhase;
            _subSum += energy;
            if (++_subCount >= _subLength)
            {
                _subEnergies.Add(_subSum / _subLength);
                _subSum = 0;
                _subCount = 0;
            }
        }

        _frames += frames;
    }

    /// <summary>Adds <paramref name="count"/> samples of <paramref name="buffer"/> from <paramref name="offset"/>.</summary>
    public void Add(float[] buffer, int offset, int count) => Add(buffer.AsSpan(offset, count));

    /// <summary>The result for everything added so far.</summary>
    public LoudnessResult Finish()
    {
        var seconds = (double)_frames / _sampleRate;
        return new LoudnessResult(Integrated(), _samplePeak, Math.Max(_samplePeak, _truePeak), seconds);
    }

    private double? Integrated()
    {
        // A block is four sub-blocks of 100 ms: the mean power of the 400 ms that end at its last sub-block.
        var blocks = new List<double>();
        for (var i = 3; i < _subEnergies.Count; i++)
        {
            blocks.Add((_subEnergies[i] + _subEnergies[i - 1] + _subEnergies[i - 2] + _subEnergies[i - 3]) / 4.0);
        }

        var aboveAbsolute = blocks.Where(z => Loudness(z) > AbsoluteGate).ToList();
        if (aboveAbsolute.Count == 0)
        {
            return null;
        }

        var relativeThreshold = Loudness(aboveAbsolute.Average()) + RelativeGate;
        var gated = aboveAbsolute.Where(z => Loudness(z) > relativeThreshold).ToList();
        return gated.Count == 0 ? null : Loudness(gated.Average());
    }

    private static double Loudness(double power) => power <= 0 ? double.NegativeInfinity : Offset + (10 * Math.Log10(power));

    // The highest absolute value among the 4 interpolated points that the newest sample finishes (the ring is read from the newest back).
    private static double TruePeakOf(float[] ring, int newest)
    {
        var peak = 0.0;
        for (var p = 0; p < Oversample; p++)
        {
            var taps = PolyphaseTaps[p];
            var sum = 0.0;
            var index = newest;
            for (var k = 0; k < TapsPerPhase; k++)
            {
                sum += taps[k] * ring[index];
                index = index == 0 ? TapsPerPhase - 1 : index - 1;
            }

            var abs = Math.Abs(sum);
            if (abs > peak)
            {
                peak = abs;
            }
        }

        return peak;
    }

    // A 49-tap windowed sinc split into 4 phases of 13 taps; every phase sums to 1 so a constant stays constant. Phase 0 is the sample itself (delayed 6).
    private static double[][] BuildPolyphase()
    {
        const int length = (Oversample * (TapsPerPhase - 1)) + 1;
        var h = new double[length];
        var center = (length - 1) / 2.0;
        for (var i = 0; i < length; i++)
        {
            var t = (i - center) / Oversample;
            var sinc = Math.Abs(t) < 1e-12 ? 1.0 : Math.Sin(Math.PI * t) / (Math.PI * t);
            var x = 2 * Math.PI * i / (length - 1);
            var window = 0.35875 - (0.48829 * Math.Cos(x)) + (0.14128 * Math.Cos(2 * x)) - (0.01168 * Math.Cos(3 * x));
            h[i] = sinc * window;
        }

        var phases = new double[Oversample][];
        for (var p = 0; p < Oversample; p++)
        {
            phases[p] = new double[TapsPerPhase];
            for (var k = 0; k < TapsPerPhase; k++)
            {
                var index = (Oversample * k) + p;
                phases[p][k] = index < length ? h[index] : 0;
            }

            var sum = phases[p].Sum();
            for (var k = 0; k < TapsPerPhase; k++)
            {
                phases[p][k] /= sum;
            }
        }

        return phases;
    }

    // One second-order section (direct form II transposed).
    private struct Biquad
    {
        private double _b0;
        private double _b1;
        private double _b2;
        private double _a1;
        private double _a2;
        private double _z1;
        private double _z2;

        // Stage 1 of BS.1770: the high shelf that models the head (about +4 dB above 2 kHz). Coefficients for any rate, as in libebur128.
        public static Biquad HighShelf(int sampleRate)
        {
            const double f0 = 1681.974450955533;
            const double gain = 3.999843853973347;
            const double q = 0.7071752369554196;
            var k = Math.Tan(Math.PI * f0 / sampleRate);
            var vh = Math.Pow(10.0, gain / 20.0);
            var vb = Math.Pow(vh, 0.4996667741545416);
            var a0 = 1.0 + (k / q) + (k * k);
            return new Biquad
            {
                _b0 = (vh + (vb * k / q) + (k * k)) / a0,
                _b1 = 2.0 * ((k * k) - vh) / a0,
                _b2 = (vh - (vb * k / q) + (k * k)) / a0,
                _a1 = 2.0 * ((k * k) - 1.0) / a0,
                _a2 = (1.0 - (k / q) + (k * k)) / a0,
            };
        }

        // Stage 2 of BS.1770: the revised low-frequency B curve (a high pass near 38 Hz).
        public static Biquad HighPass(int sampleRate)
        {
            const double f0 = 38.13547087602444;
            const double q = 0.5003270373238773;
            var k = Math.Tan(Math.PI * f0 / sampleRate);
            var a0 = 1.0 + (k / q) + (k * k);
            return new Biquad
            {
                _b0 = 1.0,
                _b1 = -2.0,
                _b2 = 1.0,
                _a1 = 2.0 * ((k * k) - 1.0) / a0,
                _a2 = (1.0 - (k / q) + (k * k)) / a0,
            };
        }

        public readonly double[] Coefficients() => [_b0, _b1, _b2, _a1, _a2];

        public double Process(double x)
        {
            var y = (_b0 * x) + _z1;
            _z1 = (_b1 * x) - (_a1 * y) + _z2;
            _z2 = (_b2 * x) - (_a2 * y);
            return y;
        }
    }
}
