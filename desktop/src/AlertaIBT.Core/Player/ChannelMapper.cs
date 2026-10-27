namespace AlertaIBT.Core.Player;

/// <summary>Moves interleaved float frames between channel counts without changing the loudness of a normal stereo track.</summary>
public static class ChannelMapper
{
    /// <summary>
    /// Converts <paramref name="frames"/> frames from <paramref name="sourceChannels"/> to <paramref name="targetChannels"/>. Mono is
    /// copied to left and right, stereo to mono is the average, and a device with more than two channels gets left and right on its
    /// first two (the rest stay silent: the player's outputs are stereo pairs such as "Out 3-4"). More than two source channels
    /// use only their first two.
    /// </summary>
    public static void Map(ReadOnlySpan<float> source, int sourceChannels, Span<float> target, int targetChannels, int frames)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sourceChannels, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(targetChannels, 1);

        if (sourceChannels == targetChannels)
        {
            source[..(frames * sourceChannels)].CopyTo(target);
            return;
        }

        for (var f = 0; f < frames; f++)
        {
            var left = source[f * sourceChannels];
            var right = sourceChannels > 1 ? source[(f * sourceChannels) + 1] : left;
            var o = f * targetChannels;
            if (targetChannels == 1)
            {
                target[o] = (left + right) * 0.5f;
                continue;
            }

            target[o] = left;
            target[o + 1] = right;
            for (var c = 2; c < targetChannels; c++)
            {
                target[o + c] = 0f;
            }
        }
    }
}
