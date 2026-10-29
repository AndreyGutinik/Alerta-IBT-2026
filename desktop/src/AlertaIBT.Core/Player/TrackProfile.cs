namespace AlertaIBT.Core.Player;

/// <summary>
/// Where the sound of a track really is: real tracks end in a long quiet tail (or silence, applause) and may start with silence, so a crossfade
/// anchored on the file's own end and start mixes two near-silent stretches and sounds like "one fades before the other enters".
/// </summary>
/// <param name="EffectiveStart">The start of the first stretch above the silence threshold (zero when the track starts loud).</param>
/// <param name="EffectiveEnd">The end of the last stretch above the silence threshold.</param>
/// <param name="RealDuration">The length that was really decoded (the header of FLAC/MP3 is often rounded down to whole seconds).</param>
public sealed record TrackProfile(TimeSpan EffectiveStart, TimeSpan EffectiveEnd, TimeSpan RealDuration);

/// <summary>Finds the effective start and end of a track by decoding only its head and its tail (a bounded cost, never the whole file).</summary>
public static class TrackAnalyzer
{
    /// <summary>The level (peak, dBFS) below which a stretch counts as silence.</summary>
    public const double SilenceDb = -50;

    /// <summary>How much of the end of the track is scanned.</summary>
    public static readonly TimeSpan TailScan = TimeSpan.FromSeconds(45);

    /// <summary>How much of the start of the track is scanned.</summary>
    public static readonly TimeSpan HeadScan = TimeSpan.FromSeconds(20);

    private static readonly TimeSpan Block = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Scans a track and returns its profile. The head and the tail each use a freshly opened decoder (a decoder that already read and then seeks
    /// can hand back stale samples through the resampler, so the tail is always the first thing a decoder does after its seek).
    /// </summary>
    /// <param name="open">Opens the track; called twice, each result is disposed here.</param>
    /// <param name="sampleRate">Frames per second of its samples.</param>
    /// <param name="channels">Samples per frame.</param>
    public static TrackProfile Analyze(Func<IPcmTrack> open, int sampleRate, int channels)
    {
        ArgumentNullException.ThrowIfNull(open);

        var threshold = (float)Math.Pow(10, SilenceDb / 20);
        var blockSamples = Math.Max(1, (int)(Block.TotalSeconds * sampleRate)) * channels;
        var buffer = new float[blockSamples];

        // Head: the first block with sound (all quiet, or very short: leave it alone).
        var start = TimeSpan.Zero;
        TimeSpan duration;
        using (var head = open())
        {
            duration = head.Duration;
            var headBlocks = (int)(HeadScan / Block);
            for (var i = 0; i < headBlocks; i++)
            {
                var read = ReadBlock(head, buffer);
                if (read == 0)
                {
                    break;
                }

                if (Peak(buffer, read) > threshold)
                {
                    start = i * Block;
                    break;
                }
            }
        }

        // Tail: from the header length minus the window to the real end of the file.
        using var track = open();
        var from = duration > TailScan ? duration - TailScan : TimeSpan.Zero;
        if (from > TimeSpan.Zero)
        {
            track.Seek(from);
        }

        var origin = track.Position;
        double lastLoud = -1;
        long blocks = 0;
        while (true)
        {
            var read = ReadBlock(track, buffer);
            if (read == 0)
            {
                break;
            }

            if (Peak(buffer, read) > threshold)
            {
                lastLoud = blocks;
            }

            blocks++;
            if (read < buffer.Length)
            {
                break;
            }
        }

        var real = origin + (blocks * Block);
        if (real < duration)
        {
            real = duration;
        }

        var end = lastLoud < 0 ? real : origin + ((lastLoud + 1) * Block);
        if (end > real)
        {
            end = real;
        }

        return new TrackProfile(start, end, real);
    }

    private static int ReadBlock(IPcmTrack track, float[] buffer)
    {
        var filled = 0;
        while (filled < buffer.Length)
        {
            var n = track.Read(buffer, filled, buffer.Length - filled);
            if (n <= 0)
            {
                break;
            }

            filled += n;
        }

        return filled;
    }

    private static float Peak(float[] buffer, int count)
    {
        var peak = 0f;
        for (var i = 0; i < count; i++)
        {
            var value = Math.Abs(buffer[i]);
            if (value > peak)
            {
                peak = value;
            }
        }

        return peak;
    }
}
