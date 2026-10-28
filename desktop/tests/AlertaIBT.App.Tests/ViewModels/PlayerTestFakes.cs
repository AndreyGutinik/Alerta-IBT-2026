using AlertaIBT.Core.Audio;
using AlertaIBT.Core.Player;

namespace AlertaIBT.App.Tests.ViewModels;

// Fakes of the player's edges (no device is opened, no file is decoded, no sound) shared by the view-model tests.
internal sealed class FakeTrack(string path) : IPcmTrack
{
    private int _read;

    public string Path { get; } = path;

    public TimeSpan Duration => TimeSpan.FromSeconds(200);

    public TimeSpan Position => TimeSpan.FromSeconds(_read / 96000.0);

    public int Read(float[] buffer, int offset, int count)
    {
        Array.Fill(buffer, 0.5f, offset, count);
        _read += count;
        return count;
    }

    public void Seek(TimeSpan position) => _read = (int)(position.TotalSeconds * 96000);

    public void Dispose()
    {
    }
}

internal sealed class FakePlayerOutput(IPcmSource source) : IPlayerOutput
{
    public IPcmSource Source { get; } = source;

    public string? DeviceId => "dev";

    public string? DeviceName => "dev";

    public event Action<Exception?>? Failed
    {
        add { }
        remove { }
    }

    public void Dispose()
    {
    }
}

internal sealed class FakePlayerBackend : IPlayerBackend
{
    public List<FakePlayerOutput> Outputs { get; } = [];

    public IPlayerOutput OpenOutput(string? deviceId, Func<int, int, IPcmSource> createSource)
    {
        var output = new FakePlayerOutput(createSource(48000, 2));
        Outputs.Add(output);
        return output;
    }

    public IPcmTrack OpenTrack(string path, int sampleRate, int channels) => new FakeTrack(path);
}

internal sealed class FakeAudio : IAudioSystem
{
    public List<AudioDevice> Devices { get; } = [new AudioDevice("id-1", "Alto-falantes (Realtek)")];

    public event Action? DevicesChanged
    {
        add { }
        remove { }
    }

    public event Action? DefaultDeviceChanged
    {
        add { }
        remove { }
    }

    public event Action? SessionCreated
    {
        add { }
        remove { }
    }

    public IReadOnlyList<AudioDevice> ActiveRenderDevices() => Devices;

    public DefaultDevices GetDefaultDevices() => new("id-1", "id-1", "id-1");

    public bool SetDefaultRenderDevice(string deviceId) => true;

    public bool SetMasterVolume(string deviceId, int percent, bool muted) => true;

    public IReadOnlyList<AudioSessionInfo> GetSessions(string deviceId) => [];

    public bool SetSessionVolume(string deviceId, string sessionKey, int percent, bool muted) => true;

    public void WatchSessions(IReadOnlyList<string> deviceIds)
    {
    }

    public void Dispose()
    {
    }
}

internal sealed class FakeFiles : IMediaFileSystem
{
    public bool DirectoryExists(string folder) => true;

    public IEnumerable<string> EnumerateFiles(string folder) => ["/m/Amazing Grace - Josh.flac", "/m/Be Still - Rivers.flac", "/m/notes.txt"];
}

internal sealed class FakeMetadata : ITrackMetadataReader
{
    public TrackInfo Read(string path) => new(path, System.IO.Path.GetFileName(path), "Song", "Artist");
}
