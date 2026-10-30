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
        : this([realFile], "one", deviceRate, deviceChannels)
    {
    }

    /// <summary>A playlist of several real files (in this order, shuffle off), each played by the real decoder; <paramref name="repeat"/> is the repeat mode.</summary>
    public RealChainRig(IReadOnlyList<string> realFiles, string repeat, int deviceRate = Rate, int deviceChannels = 2, bool threadPool = false)
    {
        Settings = new SettingsRig();
        var paths = realFiles.Select((_, i) => $"/music/track-{i + 1:00}.flac").ToList();
        Paths = paths;
        Backend = new RealChainBackend(paths.Zip(realFiles).ToDictionary(p => p.First, p => p.Second), deviceRate, deviceChannels);
        _library = new PlaylistLibrary(new ListFileSystem(paths), _ => null, Settings.Clock, action => action());
        Settings.Settings.Update(s => s with
        {
            Player = s.Player with
            {
                Playlists = [new PlaylistEntry("Igreja", "/music")],
                CurrentPlaylist = "Igreja",
                Shuffle = false,
                Repeat = repeat,
                Volume = 100,
                StartMuted = false,
                FadeInSeconds = 0,
            },
        });
        Player = new MusicPlayer(Settings.Settings, _library, Backend, new FakeAudio(), new FakeMetadata(), Settings.Clock, null, threadPool ? (action => ThreadPool.QueueUserWorkItem(_ => action())) : (action => action()), new Random(1));
        Player.Start();
    }

    public IReadOnlyList<string> Paths { get; }

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

    private sealed class ListFileSystem(IReadOnlyList<string> files) : IMediaFileSystem
    {
        public bool DirectoryExists(string folder) => true;

        public IEnumerable<string> EnumerateFiles(string folder) => files;
    }
}

internal sealed class RealChainBackend(Dictionary<string, string> realFiles, int deviceRate, int deviceChannels) : IPlayerBackend
{
    private readonly NAudioPlayerBackend _real = new();

    public List<CaptureOutput> Outputs { get; } = [];

    public IPlayerOutput OpenOutput(string? deviceId, Func<int, int, IPcmSource> createSource)
    {
        var output = new CaptureOutput(createSource(deviceRate, deviceChannels));
        Outputs.Add(output);
        return output;
    }

    public List<string> Opened { get; } = [];

    public TrackProfile? Analyze(string path, int sampleRate, int channels) => _real.Analyze(realFiles[path], sampleRate, channels);

    public IPcmTrack OpenTrack(string path, int sampleRate, int channels)
    {
        Opened.Add(path);
        return _real.OpenTrack(realFiles[path], sampleRate, channels);
    }
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
