using System.Runtime.InteropServices;
using AlertaIBT.Core.Player;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AlertaIBT.App.Platform;

/// <summary>
/// The Windows side of the music player: Media Foundation decodes the file exactly as it is (24-bit FLAC arrives as 32-bit float, never
/// cut to 16), the Media Foundation resampler (quality 60, the best) converts the rate to the device's mix format only when it differs, and a
/// shared-mode WASAPI stream (event-driven, 150 ms) plays it, so Holyrics and the other programs keep their sound. Nothing is touched before
/// the first call; tests never use this class (they use a fake backend).
/// </summary>
public sealed class NAudioPlayerBackend : IPlayerBackend
{
    /// <summary>The requested buffer, in milliseconds.</summary>
    public const int LatencyMs = 150;

    /// <summary>The best quality of the Media Foundation resampler.</summary>
    public const int ResamplerQuality = 60;

    /// <inheritdoc />
    public IPlayerOutput OpenOutput(string? deviceId, Func<int, int, IPcmSource> createSource)
    {
        ArgumentNullException.ThrowIfNull(createSource);

        // The player raises PlaybackStopped on the context that was current when it was built: never the UI thread's.
        var context = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var device = deviceId is null
                ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
                : enumerator.GetDevice(deviceId);
            try
            {
                WaveFormat mix;
                using (var client = device.CreateAudioClient())
                {
                    mix = client.MixFormat;
                }

                var source = createSource(mix.SampleRate, mix.Channels);
                var provider = new SourceWaveProvider(source);
                var player = new WasapiPlayerBuilder()
                    .WithDevice(device)
                    .WithSharedMode()
                    .WithEventSync()
                    .WithLatency(LatencyMs)
                    .Build();
                try
                {
                    player.Init(provider);
                    var output = new WasapiOutput(player, device.ID, device.FriendlyName);
                    player.Play();
                    return output;
                }
                catch
                {
                    player.Dispose();
                    throw;
                }
            }
            finally
            {
                device.Dispose();
            }
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(context);
        }
    }

    /// <inheritdoc />
    public IPcmTrack OpenTrack(string path, int sampleRate, int channels)
    {
        var reader = new MediaFoundationReader(path, new MediaFoundationReader.MediaFoundationReaderSettings { RequestFloatOutput = true });
        try
        {
            IWaveProvider provider = reader;
            var format = reader.WaveFormat;
            if (format.SampleRate != sampleRate)
            {
                provider = new MediaFoundationResampler(reader, WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, format.Channels))
                {
                    ResamplerQuality = ResamplerQuality,
                };
            }

            return new MediaFoundationTrack(reader, provider, format.Channels, channels);
        }
        catch
        {
            reader.Dispose();
            throw;
        }
    }

    // Pulls the player's mixer as 32-bit float at the device's rate and channel count.
    private sealed class SourceWaveProvider : WaveProvider32
    {
        private readonly IPcmSource _source;
        private float[] _scratch = new float[4096];

        public SourceWaveProvider(IPcmSource source)
            : base(source.SampleRate, source.Channels) => _source = source;

        public override int Read(Span<float> buffer)
        {
            if (_scratch.Length < buffer.Length)
            {
                _scratch = new float[buffer.Length];
            }

            var n = _source.Read(_scratch, 0, buffer.Length);
            _scratch.AsSpan(0, n).CopyTo(buffer);
            return n;
        }
    }

    private sealed class WasapiOutput : IPlayerOutput
    {
        private readonly WasapiPlayer _player;
        private bool _disposing;

        public WasapiOutput(WasapiPlayer player, string deviceId, string deviceName)
        {
            _player = player;
            DeviceId = deviceId;
            DeviceName = deviceName;
            _player.PlaybackStopped += OnStopped;
        }

        public event Action<Exception?>? Failed;

        public string? DeviceId { get; }

        public string? DeviceName { get; }

        public void Dispose()
        {
            _disposing = true;
            _player.PlaybackStopped -= OnStopped;
            try
            {
                _player.Stop();
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException)
            {
                // A device that was unplugged cannot be stopped: there is nothing left to stop.
            }

            _player.Dispose();
        }

        // The source never ends, so a stop that was not asked for is the device going away.
        private void OnStopped(object? sender, StoppedEventArgs e)
        {
            if (!_disposing)
            {
                Failed?.Invoke(e.Exception);
            }
        }
    }

    // The decoded file as float samples with the output's channel count.
    private sealed class MediaFoundationTrack : IPcmTrack
    {
        private readonly MediaFoundationReader _reader;
        private readonly IWaveProvider _provider;
        private readonly int _sourceChannels;
        private readonly int _targetChannels;
        private float[] _scratch = [];

        public MediaFoundationTrack(MediaFoundationReader reader, IWaveProvider provider, int sourceChannels, int targetChannels)
        {
            _reader = reader;
            _provider = provider;
            _sourceChannels = sourceChannels;
            _targetChannels = targetChannels;
        }

        public TimeSpan Duration => _reader.TotalTime;

        public TimeSpan Position => _reader.CurrentTime;

        public int Read(float[] buffer, int offset, int count)
        {
            var frames = count / _targetChannels;
            if (frames == 0)
            {
                return 0;
            }

            if (_sourceChannels == _targetChannels)
            {
                return ReadFloats(buffer.AsSpan(offset, frames * _targetChannels));
            }

            var wanted = frames * _sourceChannels;
            if (_scratch.Length < wanted)
            {
                _scratch = new float[wanted];
            }

            var got = ReadFloats(_scratch.AsSpan(0, wanted)) / _sourceChannels;
            ChannelMapper.Map(_scratch, _sourceChannels, buffer.AsSpan(offset), _targetChannels, got);
            return got * _targetChannels;
        }

        public void Seek(TimeSpan position)
        {
            var last = _reader.TotalTime - TimeSpan.FromMilliseconds(50);
            _reader.CurrentTime = position > last ? last : position < TimeSpan.Zero ? TimeSpan.Zero : position;
            if (_provider is MediaFoundationResampler resampler)
            {
                resampler.Reposition(); // drop what the resampler had already converted from the old place
            }
        }

        public void Dispose()
        {
            (_provider as IDisposable)?.Dispose();
            _reader.Dispose();
        }

        // Fills as much of the span as the decoder has; returns the number of samples read (0 at the end).
        private int ReadFloats(Span<float> samples)
        {
            var bytes = MemoryMarshal.AsBytes(samples);
            var total = 0;
            while (total < bytes.Length)
            {
                var n = _provider.Read(bytes[total..]);
                if (n <= 0)
                {
                    break;
                }

                total += n;
            }

            return total / sizeof(float);
        }
    }
}
