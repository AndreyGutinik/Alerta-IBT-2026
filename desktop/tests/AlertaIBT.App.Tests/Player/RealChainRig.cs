using AlertaIBT.App.Platform;
using AlertaIBT.App.Tests.ViewModels;
using AlertaIBT.Core.Player;
using AlertaIBT.Core.Settings;

namespace AlertaIBT.App.Tests.Player;

/// <summary>
/// The real sample chain of the player (Media Foundation decoder, resampler, channel map, PlayerMixer fade and volume) with only the sound card
/// replaced: the test pulls the mixer exactly as WASAPI does. Nothing is opened on a device, nothing is heard.
/// </summary>
internal sealed class RealChainRig : IDisposable
{
    public const int Rate = 48000;
    public const string FakePath = "/music/tone.flac";

    private readonly PlaylistLibrary _library;

    public RealChainRig(string realFile, int deviceRate = Rate, int deviceChannels = 2)
    {
        Settings = new SettingsRig();
        Backend = new RealChainBackend(realFile, deviceRate, deviceChannels);
        _library = new PlaylistLibrary(new SingleFileSystem(), _ => null, Settings.Clock, action => action());
        Settings.Settings.Update(s => s with
        {
            Player = s.Player with
            {
                Playlists = [new PlaylistEntry("Igreja", "/music")],
                CurrentPlaylist = "Igreja",
                Shuffle = false,
                Repeat = "one",
                Volume = 100,
                StartMuted = false,
            },
        });
        Player = new MusicPlayer(Settings.Settings, _library, Backend, new FakeAudio(), new FakeMetadata(), Settings.Clock, null, action => action(), new Random(1));
        Player.Start();
    }

    public SettingsRig Settings { get; }

    public RealChainBackend Backend { get; }

    public MusicPlayer Player { get; }

    public CaptureOutput Output => Backend.Outputs[^1];

    public void Change(Func<PlayerSettings, PlayerSettings> edit) => Settings.Settings.Update(s => s with { Player = edit(s.Player) });

    public void Dispose()
    {
        Player.Dispose();
        _library.Dispose();
        Settings.Dispose();
    }

    private sealed class SingleFileSystem : IMediaFileSystem
    {
        public bool DirectoryExists(string folder) => true;

        public IEnumerable<string> EnumerateFiles(string folder) => [FakePath];
    }
}

internal sealed class RealChainBackend(string realFile, int deviceRate, int deviceChannels) : IPlayerBackend
{
    private readonly NAudioPlayerBackend _real = new();

    public List<CaptureOutput> Outputs { get; } = [];

    public IPlayerOutput OpenOutput(string? deviceId, Func<int, int, IPcmSource> createSource)
    {
        var output = new CaptureOutput(createSource(deviceRate, deviceChannels));
        Outputs.Add(output);
        return output;
    }

    public IPcmTrack OpenTrack(string path, int sampleRate, int channels) => _real.OpenTrack(realFile, sampleRate, channels);
}

/// <summary>The "sound card": what it pulls is what would be heard.</summary>
internal sealed class CaptureOutput(IPcmSource source) : IPlayerOutput
{
    public IPcmSource Source { get; } = source;

    public string? DeviceId => "capture";

    public string? DeviceName => "capture";

    public bool Disposed { get; private set; }

    public event Action<Exception?>? Failed
    {
        add { }
        remove { }
    }

    /// <summary>Pulls <paramref name="frames"/> frames in one read.</summary>
    public float[] Pull(int frames)
    {
        var buffer = new float[frames * Source.Channels];
        Source.Read(buffer, 0, buffer.Length);
        return buffer;
    }

    /// <summary>Pulls <paramref name="seconds"/> of sound in 10 ms reads and returns the RMS of each window of <paramref name="windowSeconds"/>.</summary>
    public List<double> RmsWindows(double seconds, double windowSeconds)
    {
        var windows = new List<double>();
        var block = Source.SampleRate / 100;
        var perWindow = (int)Math.Round(windowSeconds * 100);
        var total = (int)Math.Round(seconds * 100);
        double sum = 0;
        long count = 0;
        for (var i = 1; i <= total; i++)
        {
            foreach (var sample in Pull(block))
            {
                sum += (double)sample * sample;
                count++;
            }

            if (i % perWindow == 0)
            {
                windows.Add(Math.Sqrt(sum / Math.Max(1, count)));
                sum = 0;
                count = 0;
            }
        }

        return windows;
    }

    public void Dispose() => Disposed = true;
}
