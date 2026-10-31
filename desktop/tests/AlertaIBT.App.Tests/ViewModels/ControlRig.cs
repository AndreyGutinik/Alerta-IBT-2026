using System.Net;
using AlertaIBT.App.ViewModels;
using AlertaIBT.Core.Control;
using AlertaIBT.Core.Settings;

namespace AlertaIBT.App.Tests.ViewModels;

/// <summary>A stand-in for the Art-Net socket: the test raises packets and states by hand.</summary>
internal sealed class FakeArtNet : IArtNetSource
{
    public ArtNetState State { get; private set; } = ArtNetState.Stopped;

    public bool Disposed { get; private set; }

    public event Action<ArtNetArrival>? Packet;

    public event Action? StateChanged;

    public void Start()
    {
        State = ArtNetState.Listening;
        StateChanged?.Invoke();
    }

    public void Stop() => State = ArtNetState.Stopped;

    public void Dispose()
    {
        Disposed = true;
        State = ArtNetState.Stopped;
    }

    public void SetState(ArtNetState state)
    {
        State = state;
        StateChanged?.Invoke();
    }

    public void Send(byte[] levels, string from = "2.0.0.2", int universe = 99) =>
        Packet?.Invoke(new ArtNetArrival(new ArtDmxPacket(0, 0, universe, levels), IPAddress.Parse(from)));
}

/// <summary>Targets that only remember what ran.</summary>
internal sealed class RecordingTargets : IControlTargets
{
    private readonly object _gate = new();
    private readonly List<ControlCommand> _ran = [];

    public Dictionary<ControlAction, double?> Levels { get; } = [];

    public IReadOnlyList<ControlCommand> Ran
    {
        get
        {
            lock (_gate)
            {
                return _ran.ToList();
            }
        }
    }

    public ControlResult Run(ControlCommand command)
    {
        lock (_gate)
        {
            _ran.Add(command);
        }

        return ControlResult.Ok;
    }

    public double? GetLevel(ControlAction action) => Levels.TryGetValue(action, out var level) ? level : null;
}

/// <summary>Real settings, the real service and engine over a fake socket and recording targets, and the tab's view-model.</summary>
internal sealed class ControlRig : IDisposable
{
    public ControlRig(AppSettings? initial = null)
    {
        Chrome = new SettingsRig(initial);
        Service = new ControlService(Chrome.Settings, Targets, () => Socket, clock: Chrome.Clock);
        Service.Start();
        Vm = new ControlViewModel(Chrome.Settings, Service, (title, text) =>
        {
            Asked.Add(title);
            return Answer;
        });
    }

    public SettingsRig Chrome { get; }

    public FakeArtNet Socket { get; } = new();

    public RecordingTargets Targets { get; } = new();

    public ControlService Service { get; }

    public ControlViewModel Vm { get; }

    public List<string> Asked { get; } = [];

    public bool Answer { get; set; } = true;

    public AppSettings Settings => Chrome.Settings.Current;

    public ControlSettings Control => Chrome.Settings.Current.Control;

    public void Update(Func<ControlSettings, ControlSettings> change) => Chrome.Settings.Update(s => s with { Control = change(s.Control) });

    public void TurnOn() => Update(c => c with { Enabled = true });

    public static byte[] Levels(params (int Channel, byte Value)[] values)
    {
        var data = new byte[512];
        foreach (var (channel, value) in values)
        {
            data[channel - 1] = value;
        }

        return data;
    }

    public void Dispose()
    {
        Vm.Dispose();
        Service.Dispose();
        Chrome.Dispose();
    }
}
