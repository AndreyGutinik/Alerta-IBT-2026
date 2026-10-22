using AlertaIBT.Core.Audio;

namespace AlertaIBT.Core.Tests.Audio;

/// <summary>
/// The Windows audio for the tests: devices, defaults and sessions are plain data, every write is recorded in <see cref="Calls"/>, and
/// nothing here can touch the real volume or output. It mimics Windows in one respect: choosing the default output raises the
/// "default changed" event (<see cref="EchoEvents"/>).
/// </summary>
public sealed class FakeAudioSystem : IAudioSystem
{
    public const string WingId = "{0.0.0.00000000}.{wing-1}";
    public const string SpeakersId = "{0.0.0.00000000}.{speakers-1}";
    public const string WingName = "Alto-falantes (WING USB)";
    public const string SpeakersName = "Alto-falantes (High Definition Audio Device)";

    private readonly object _gate = new();

    public List<AudioDevice> Devices { get; } = [new AudioDevice(WingId, WingName), new AudioDevice(SpeakersId, SpeakersName)];

    public string? DefaultConsole { get; set; } = SpeakersId;

    public string? DefaultMultimedia { get; set; } = SpeakersId;

    public string? DefaultCommunications { get; set; } = SpeakersId;

    public Dictionary<string, List<AudioSessionInfo>> Sessions { get; } = [];

    /// <summary>Every write, in order: <c>default:id</c>, <c>master:id:80:False</c>, <c>session:id:key:100:False</c>, <c>watch:id</c>.</summary>
    public List<string> Calls { get; } = [];

    public bool EchoEvents { get; set; } = true;

    public bool ThrowOnRead { get; set; }

    public bool FailSetDefault { get; set; }

    public bool FailSetVolume { get; set; }

    /// <summary>What a driver that fights back does after each selection: runs inside <see cref="SetDefaultRenderDevice"/>.</summary>
    public Action? AfterSetDefault { get; set; }

    public string? Watching { get; private set; }

    public event Action? DevicesChanged;

    public event Action? DefaultDeviceChanged;

    public event Action? SessionCreated;

    public IEnumerable<string> Writes(string kind) => Calls.Where(c => c.StartsWith(kind + ":", StringComparison.Ordinal));

    public void Add(AudioDevice device)
    {
        Devices.Add(device);
        DevicesChanged?.Invoke();
    }

    public void Remove(string id)
    {
        Devices.RemoveAll(d => d.Id == id);
        DevicesChanged?.Invoke();
    }

    public void WindowsSwitchesTo(string id)
    {
        DefaultConsole = DefaultMultimedia = DefaultCommunications = id;
        DefaultDeviceChanged?.Invoke();
    }

    public void AddSession(string deviceId, string key, string? process, bool system = false, bool raise = true)
    {
        lock (_gate)
        {
            if (!Sessions.TryGetValue(deviceId, out var list))
            {
                Sessions[deviceId] = list = [];
            }

            list.Add(new AudioSessionInfo(key, process, system));
        }

        if (raise)
        {
            SessionCreated?.Invoke();
        }
    }

    public IReadOnlyList<AudioDevice> ActiveRenderDevices()
    {
        Check();
        return [.. Devices];
    }

    public DefaultDevices GetDefaultDevices()
    {
        Check();
        return new DefaultDevices(DefaultConsole, DefaultMultimedia, DefaultCommunications);
    }

    public bool SetDefaultRenderDevice(string deviceId)
    {
        Calls.Add("default:" + deviceId);
        if (FailSetDefault || Devices.All(d => d.Id != deviceId))
        {
            return false;
        }

        DefaultConsole = DefaultMultimedia = DefaultCommunications = deviceId;
        if (EchoEvents)
        {
            DefaultDeviceChanged?.Invoke();
        }

        AfterSetDefault?.Invoke();
        return true;
    }

    public bool SetMasterVolume(string deviceId, int percent, bool muted)
    {
        Calls.Add($"master:{deviceId}:{percent}:{muted}");
        return !FailSetVolume && Devices.Any(d => d.Id == deviceId);
    }

    public IReadOnlyList<AudioSessionInfo> GetSessions(string deviceId)
    {
        Check();
        lock (_gate)
        {
            return Sessions.TryGetValue(deviceId, out var list) ? [.. list] : [];
        }
    }

    public bool SetSessionVolume(string deviceId, string sessionKey, int percent, bool muted)
    {
        Calls.Add($"session:{deviceId}:{sessionKey}:{percent}:{muted}");
        return !FailSetVolume;
    }

    public void WatchSessions(string? deviceId)
    {
        Watching = deviceId;
        Calls.Add("watch:" + deviceId);
    }

    public void Dispose() => Calls.Add("dispose");

    private void Check()
    {
        if (ThrowOnRead)
        {
            throw new InvalidOperationException("audio service is not running");
        }
    }
}
