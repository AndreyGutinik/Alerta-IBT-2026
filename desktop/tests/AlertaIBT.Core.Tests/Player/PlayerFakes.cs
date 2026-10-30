using AlertaIBT.Core.Audio;
using AlertaIBT.Core.Player;
using AlertaIBT.Core.Settings;
using AlertaIBT.Core.Tests.Audio;
using AlertaIBT.Core.Tests.Services;
using Microsoft.Extensions.Time.Testing;

namespace AlertaIBT.Core.Tests.Player;

internal sealed class FakeTrack : IPcmTrack
{
    private readonly int _channels;
    private readonly int _rate;
    private long _read;

    public FakeTrack(string path, int rate, int channels, int totalFrames, float level)
    {
        Path = path;
        _rate = rate;
        _channels = channels;
        TotalFrames = totalFrames;
        Level = level;
    }

    public string Path { get; }

    public int TotalFrames { get; }

    public float Level { get; }

    public bool Disposed { get; private set; }

    public TimeSpan Duration => TimeSpan.FromSeconds((double)TotalFrames / _rate);

    public TimeSpan Position => TimeSpan.FromSeconds((double)(_read / _channels) / _rate);

    public void Seek(TimeSpan to) => _read = (long)(to.TotalSeconds * _rate) * _channels;

    public int Read(float[] buffer, int offset, int count)
    {
        var left = (long)TotalFrames * _channels - _read;
        var n = (int)Math.Min(left, count);
        for (var i = 0; i < n; i++)
        {
            buffer[offset + i] = Level;
        }

        _read += n;
        return n;
    }

    public void Dispose() => Disposed = true;
}

internal sealed class FakeOutput : IPlayerOutput
{
    private readonly IPcmSource _source;

    public FakeOutput(string? requestedId, string? deviceId, IPcmSource source)
    {
        RequestedId = requestedId;
        DeviceId = deviceId;
        _source = source;
    }

    public string? RequestedId { get; }

    public string? DeviceId { get; }

    public string? DeviceName => DeviceId;

    public bool Disposed { get; private set; }

    public event Action<Exception?>? Failed;

    /// <summary>What the sound card would pull: <paramref name="frames"/> frames, as samples.</summary>
    public float[] Pump(int frames)
    {
        var buffer = new float[frames * _source.Channels];
        _source.Read(buffer, 0, buffer.Length);
        return buffer;
    }

    public void PumpSeconds(double seconds)
    {
        var total = (int)(seconds * _source.SampleRate);
        const int Block = 480;
        for (var done = 0; done < total; done += Block)
        {
            Pump(Math.Min(Block, total - done));
        }
    }

    public void Fail(Exception? error = null) => Failed?.Invoke(error);

    public void Dispose() => Disposed = true;
}

internal sealed class FakePlayerBackend : IPlayerBackend
{
    public int SampleRate { get; set; } = 48000;

    public int Channels { get; set; } = 2;

    public int TrackFrames { get; set; } = 48000 * 10;

    public float Level { get; set; } = 0.5f;

    /// <summary>The level of a file when it is not the default one (to tell tracks apart by ear, in a test).</summary>
    public Func<string, float>? LevelFor { get; set; }

    public Exception? OutputError { get; set; }

    /// <summary>Where the sound of a file really starts and ends (null: the backend cannot say, the header lengths are used).</summary>
    public Func<string, TrackProfile?>? ProfileFor { get; set; }

    public List<string> Analyzed { get; } = [];

    public TrackProfile? Analyze(string path, int sampleRate, int channels)
    {
        Analyzed.Add(path);
        return ProfileFor?.Invoke(path);
    }

    public HashSet<string> BadFiles { get; } = [];

    public List<FakeOutput> Outputs { get; } = [];

    public List<FakeTrack> Tracks { get; } = [];

    public FakeOutput Output => Outputs[^1];

    public FakeTrack Track => Tracks[^1];

    public IReadOnlyList<string> OpenedPaths => Tracks.Select(t => t.Path).ToList();

    public IPlayerOutput OpenOutput(string? deviceId, Func<int, int, IPcmSource> createSource)
    {
        if (OutputError is { } error)
        {
            throw error;
        }

        var source = createSource(SampleRate, Channels);
        var output = new FakeOutput(deviceId, deviceId ?? "default-device", source);
        Outputs.Add(output);
        return output;
    }

    public IPcmTrack OpenTrack(string path, int sampleRate, int channels)
    {
        if (BadFiles.Contains(path))
        {
            throw new IOException("corrupt file");
        }

        var track = new FakeTrack(path, sampleRate, channels, TrackFrames, LevelFor?.Invoke(path) ?? Level);
        Tracks.Add(track);
        return track;
    }
}

internal sealed class FakeMetadata : ITrackMetadataReader
{
    public TrackInfo Read(string path) =>
        new(path, System.IO.Path.GetFileName(path), System.IO.Path.GetFileNameWithoutExtension(path), "Artist");
}

internal sealed class FakeFolderWatcher : IFolderWatcher
{
    public bool Disposed { get; private set; }

    public event Action? Changed;

    public void Raise() => Changed?.Invoke();

    public void Dispose() => Disposed = true;
}

internal sealed class FakeMediaFileSystem : IMediaFileSystem
{
    public Dictionary<string, List<string>> Folders { get; } = new(StringComparer.OrdinalIgnoreCase);

    public int Scans { get; private set; }

    public bool DirectoryExists(string folder) => Folders.ContainsKey(folder);

    public IEnumerable<string> EnumerateFiles(string folder)
    {
        Scans++;
        return Folders.TryGetValue(folder, out var files) ? files.ToList() : [];
    }
}

/// <summary>A whole player on fakes: nothing here can open a device, decode a file or touch the real disk.</summary>
internal sealed class PlayerRig : IDisposable
{
    public const string Folder = "/music/igreja";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "alerta-ibt-player-" + Guid.NewGuid().ToString("N"));

    public PlayerRig(int tracks = 5, bool start = true, int seed = 7, AlertaIBT.Core.Loudness.ILoudnessProvider? loudness = null)
    {
        Directory.CreateDirectory(_dir);
        Clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 25, 17, 0, 0, TimeSpan.Zero));
        Settings = new SettingsService(new SettingsStore(Path.Combine(_dir, "settings.json"), Clock), Clock);
        Audio = new FakeAudioSystem();
        Backend = new FakePlayerBackend();
        FileSystem = new FakeMediaFileSystem();
        FileSystem.Folders[Folder] = Enumerable.Range(1, tracks).Select(i => $"{Folder}/track {i:00}.flac").ToList();
        Watchers = [];
        Library = new PlaylistLibrary(
            FileSystem,
            _ =>
            {
                var watcher = new FakeFolderWatcher();
                Watchers.Add(watcher);
                return watcher;
            },
            Clock,
            action => action());
        Log = new CapturingLogger();
        Player = new MusicPlayer(Settings, Library, Backend, Audio, new FakeMetadata(), Clock, Log, action => action(), new Random(seed), loudness);
        Settings.Update(s => s with { Player = s.Player with { Playlists = [new PlaylistEntry("Igreja", Folder)], CurrentPlaylist = "Igreja", FadeInSeconds = 0 } });
        Audio.Devices.Clear();
        Audio.Devices.Add(new AudioDevice(Speakers, "Alto-falantes (Realtek)"));
        Audio.DefaultMultimedia = Speakers;
        if (start)
        {
            Player.Start();
        }
    }

    public const string Speakers = "{0.0.0.00000000}.{speakers-rig}";
    public const string Wing34 = "{0.0.0.00000000}.{wing-34}";

    public FakeTimeProvider Clock { get; }

    public SettingsService Settings { get; }

    public FakeAudioSystem Audio { get; }

    public FakePlayerBackend Backend { get; }

    public FakeMediaFileSystem FileSystem { get; }

    public List<FakeFolderWatcher> Watchers { get; }

    public PlaylistLibrary Library { get; }

    public CapturingLogger Log { get; }

    public MusicPlayer Player { get; }

    public void Change(Func<PlayerSettings, PlayerSettings> edit) => Settings.Update(s => s with { Player = edit(s.Player) });

    /// <summary>The church's output appears.</summary>
    public void PlugWing34()
    {
        Audio.Devices.Add(new AudioDevice(Wing34, "WING-USB (Out 3-4)"));
        Audio.Add(new AudioDevice("{0.0.0.00000000}.{wing-12}", "WING-USB (Out 1-2)"));
    }

    public void Dispose()
    {
        Player.Dispose();
        Library.Dispose();
        Settings.Dispose();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // A scanner may hold a file for a moment.
        }
    }
}
