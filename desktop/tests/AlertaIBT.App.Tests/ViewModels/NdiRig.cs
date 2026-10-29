using AlertaIBT.App.ViewModels;
using AlertaIBT.Core.Ndi;
using AlertaIBT.Core.Screens;
using AlertaIBT.Core.Settings;
using Microsoft.Extensions.Time.Testing;

namespace AlertaIBT.App.Tests.ViewModels;

/// <summary>An NDI runtime of the tests: it is there (or not) and its finder lists the sources that are set.</summary>
internal sealed class FakeNdi : INdiRuntime
{
    public bool IsAvailable { get; set; } = true;

    public string Version => "fake";

    public string? UnavailableReason => IsAvailable ? null : NdiConstants.RuntimeMissingText;

    public string? LoadedFrom => null;

    public List<NdiSourceInfo> Found { get; } = [];

    public List<IReadOnlyList<string>> FinderIps { get; } = [];

    public INdiFinder CreateFinder(IReadOnlyList<string> extraIps)
    {
        FinderIps.Add(extraIps);
        return new FakeFinder(this);
    }

    public INdiNativeReceiver CreateReceiver(NdiSourceRef source, string receiverName) => throw new NotSupportedException();

    public void Dispose()
    {
    }

    private sealed class FakeFinder(FakeNdi owner) : INdiFinder
    {
        public bool WaitForChange(int timeoutMs)
        {
            Thread.Sleep(10);
            return false;
        }

        public IReadOnlyList<NdiSourceInfo> GetSources() => owner.Found.ToList();

        public void Dispose()
        {
        }
    }
}

/// <summary>A receiver the tests drive by hand: <see cref="Push"/> sends a test picture as the capture thread would.</summary>
internal sealed class FakeLane(FakeTimeProvider clock) : ICameraReceiver
{
    private long _last;
    private long _received;

    public event NdiFrameHandler? Frame;

    public event Action? StateChanged;

    public NdiConnectionState State { get; private set; }

    public NdiSourceRef? Source { get; private set; }

    public bool HasFrameSinceConnect { get; private set; }

    public long LastFrameTimestamp => _last;

    public long FramesReceived => _received;

    public long FramesDropped => 0;

    public int Width { get; private set; }

    public int Height { get; private set; }

    public double Fps => 60;

    public string? LastError => null;

    public void Connect(NdiSourceRef? source)
    {
        Source = source;
        HasFrameSinceConnect = false;
        State = source is null ? NdiConnectionState.Idle : NdiConnectionState.Connecting;
        StateChanged?.Invoke();
    }

    /// <summary>The state changes (a cable pulled: Lost).</summary>
    public void Set(NdiConnectionState state)
    {
        State = state;
        StateChanged?.Invoke();
    }

    /// <summary>Sends one 16:9 test picture: color bars on top of a gradient, with a line at every 10% of the height so the crop is easy to see.</summary>
    public void Push(int hue = 0, int width = 640, int height = 360)
    {
        clock.Advance(TimeSpan.FromMilliseconds(60));
        var data = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = ((y * width) + x) * 4;
                var bar = (x * 7 / width + hue) % 7;
                byte r = (byte)((bar & 4) != 0 ? 200 : 40);
                byte g = (byte)((bar & 2) != 0 ? 200 : 40);
                byte b = (byte)((bar & 1) != 0 ? 200 : 40);
                var shade = 0.55 + (0.45 * y / height);
                data[i] = (byte)(b * shade);
                data[i + 1] = (byte)(g * shade);
                data[i + 2] = (byte)(r * shade);
                data[i + 3] = 255;
                if (y % (height / 10) < 2)
                {
                    data[i] = data[i + 1] = data[i + 2] = 245;
                }
            }
        }

        var now = clock.GetTimestamp();
        _last = now;
        _received++;
        Width = width;
        Height = height;
        HasFrameSinceConnect = true;
        State = NdiConnectionState.Connected;
        StateChanged?.Invoke();
        Frame?.Invoke(new NdiFrameView(width, height, width * 4, data, now));
    }

    public void Dispose()
    {
    }
}

/// <summary>The NDI tab on the real engine (<see cref="NdiService"/> with its switcher) over fake receivers, real settings in a temp folder, fake monitors.</summary>
internal sealed class NdiRig : IDisposable
{
    public NdiRig(int cameras = 3, bool runtime = true, IReadOnlyList<ScreenInfo>? screens = null, Func<IReadOnlyList<NdiCameraSlot>, IReadOnlyList<NdiCameraSlot>>? cameraSlots = null, NdiSettings? ndi = null)
    {
        var slots = Enumerable.Range(1, NdiSettings.SlotCount)
            .Select(n => n <= cameras
                ? new NdiCameraSlot(Names.Length >= n ? Names[n - 1] : $"Cam{n}", $"PC (Cam{n})", 50)
                : new NdiCameraSlot($"C\U000000E2mera {n}", string.Empty))
            .ToList();
        var initial = new AppSettings { Ndi = (ndi ?? new NdiSettings()) with { Cameras = cameraSlots?.Invoke(slots) ?? slots } };
        Settings = new SettingsRig(initial);
        Runtime = new FakeNdi { IsAvailable = runtime };
        Screens = screens ?? [new ScreenInfo("p1", "\\\\.\\DISPLAY1", 0, 0, 1920, 1080, true), new ScreenInfo("p2", "\\\\.\\DISPLAY3", 1920, 0, 2560, 768, false)];
        Service = new NdiService(Runtime, Settings.Settings, Settings.Clock, null, _ => Add(new FakeLane(Settings.Clock)), TimeSpan.FromSeconds(5));
        Vm = new NdiViewModel(
            Service,
            Settings.Settings,
            () => Screens,
            () => Screens.FirstOrDefault(s => s.Width == 2560 && s.Height == 768),
            () => Hotkeys,
            () => Rechecks.Dequeue(),
            null,
            (title, message) =>
            {
                Confirmations.Add((title, message));
                return ConfirmAnswer;
            },
            input =>
            {
                EditRequests.Add(input);
                var answer = Edits.Count > 0 ? Edits.Dequeue() : null;
                return answer is not null && input.Check(answer.Name, answer.Source) is null ? answer : null;
            },
            ips =>
            {
                return Ips.Count > 0 ? Ips.Dequeue() : null;
            },
            Opened.Add);
    }

    public static string[] Names { get; set; } = [];

    public SettingsRig Settings { get; }

    public FakeNdi Runtime { get; }

    public IReadOnlyList<ScreenInfo> Screens { get; set; }

    public NdiHotkeyController? Hotkeys { get; set; }

    public NdiService Service { get; }

    public NdiViewModel Vm { get; }

    public List<FakeLane> Lanes { get; } = [];

    public List<(string Title, string Message)> Confirmations { get; } = [];

    public bool ConfirmAnswer { get; set; } = true;

    public List<CameraEditInput> EditRequests { get; } = [];

    public Queue<CameraEditResult?> Edits { get; } = new();

    public Queue<IReadOnlyList<string>?> Ips { get; } = new();

    public Queue<bool> Rechecks { get; } = new();

    public List<string> Opened { get; } = [];

    public NdiSettings Ndi => Settings.Settings.Current.Ndi;

    /// <summary>The fake receiver that is connected to camera <paramref name="n"/> (1-based, source <c>PC (CamN)</c>).</summary>
    public FakeLane LaneOf(int n) => Lanes.Last(lane => lane.Source?.Name == $"PC (Cam{n})");

    /// <summary>Camera <paramref name="n"/> to the Preview, with its first picture, as the owner would see it a moment later.</summary>
    public void Preview(int n, int hue = 0)
    {
        Vm.Cameras.First(c => c.Slot == n - 1).SelectCommand.Execute(null);
        LaneOf(n).Push(hue);
    }

    /// <summary>Camera <paramref name="n"/> on air (Preview, then Cortar).</summary>
    public void GoOnAir(int n, int hue = 0)
    {
        Preview(n, hue);
        Vm.TakeCommand.Execute(null);
    }

    public void Dispose()
    {
        Vm.Dispose();
        Service.Dispose();
        Settings.Dispose();
    }

    private FakeLane Add(FakeLane lane)
    {
        Lanes.Add(lane);
        return lane;
    }
}
