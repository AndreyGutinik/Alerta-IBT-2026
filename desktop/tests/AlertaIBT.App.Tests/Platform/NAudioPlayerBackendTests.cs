using AlertaIBT.App.Platform;

namespace AlertaIBT.App.Tests.Platform;

// Decoding only: a tiny WAV file made here goes through Media Foundation and the resampler. No output device is opened and nothing is played.
public sealed class NAudioPlayerBackendTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "alerta-ibt-backend-" + Guid.NewGuid().ToString("N"));

    public NAudioPlayerBackendTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // A scanner may hold the file for a moment.
        }
    }

    // A 16-bit PCM WAV: a constant half-scale value, so a conversion that changes the level or the channels shows.
    private string MakeWav(int sampleRate, int channels, double seconds)
    {
        var path = Path.Combine(_dir, $"tone-{sampleRate}-{channels}.wav");
        var frames = (int)(sampleRate * seconds);
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        var dataBytes = frames * channels * 2;
        writer.Write("RIFF"u8);
        writer.Write(36 + dataBytes);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * channels * 2);
        writer.Write((short)(channels * 2));
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(dataBytes);
        for (var i = 0; i < frames * channels; i++)
        {
            writer.Write((short)16384);
        }

        return path;
    }

    private static float[] ReadAll(Core.Player.IPcmTrack track, int channels)
    {
        var all = new List<float>();
        var buffer = new float[1024 * channels];
        int n;
        while ((n = track.Read(buffer, 0, buffer.Length)) > 0)
        {
            all.AddRange(buffer.Take(n));
        }

        return [.. all];
    }

    [Fact]
    public void A_mono_44k_file_becomes_float_stereo_48k_with_the_same_level_and_length()
    {
        var path = MakeWav(44100, 1, 1.0);

        using var track = new NAudioPlayerBackend().OpenTrack(path, 48000, 2);
        var samples = ReadAll(track, 2);

        Assert.InRange(samples.Length / 2, 47500, 48500); // one second at 48 kHz, give or take the resampler's edges
        Assert.InRange(track.Duration.TotalSeconds, 0.97, 1.03);
        var middle = samples.Length / 2;
        Assert.Equal(0.5f, samples[middle], 2);
        Assert.Equal(samples[middle], samples[middle + 1], 3); // left and right are equal
    }

    [Fact]
    public void A_file_at_the_device_rate_is_decoded_without_resampling_and_a_missing_file_throws()
    {
        var path = MakeWav(48000, 2, 0.5);

        using (var track = new NAudioPlayerBackend().OpenTrack(path, 48000, 2))
        {
            Assert.Equal(24000, ReadAll(track, 2).Length / 2); // 0.5 s exactly: no resampler in the way
        }

        Assert.ThrowsAny<Exception>(() => new NAudioPlayerBackend().OpenTrack(Path.Combine(_dir, "missing.flac"), 48000, 2));
    }
}
