using System.Net;
using AlertaIBT.Core.Control;
using AlertaIBT.Core.Settings;

namespace AlertaIBT.Core.Tests.Control;

internal sealed class FakeArtNetSource : IArtNetSource
{
    public ArtNetState State { get; set; } = ArtNetState.Stopped;

    public bool Disposed { get; private set; }

    public int Starts { get; private set; }

    public event Action<ArtNetArrival>? Packet;

    public event Action? StateChanged;

    public void Start()
    {
        Starts++;
        State = ArtNetState.Listening;
        StateChanged?.Invoke();
    }

    public void Stop()
    {
        State = ArtNetState.Stopped;
        StateChanged?.Invoke();
    }

    public void Dispose()
    {
        Disposed = true;
        State = ArtNetState.Stopped;
    }

    public void Raise(ArtNetArrival arrival) => Packet?.Invoke(arrival);

    public void SetState(ArtNetState state)
    {
        State = state;
        StateChanged?.Invoke();
    }
}

internal sealed class FakeControlTargets : IControlTargets
{
    private readonly object _gate = new();
    private readonly List<ControlCommand> _ran = [];

    public Dictionary<ControlAction, double?> Levels { get; } = [];

    public Func<ControlCommand, ControlResult>? Behavior { get; set; }

    public ManualResetEventSlim? Block { get; set; }

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
        Block?.Wait(5000);
        lock (_gate)
        {
            _ran.Add(command);
        }

        return Behavior?.Invoke(command) ?? ControlResult.Ok;
    }

    public double? GetLevel(ControlAction action) => Levels.TryGetValue(action, out var level) ? level : null;
}

// The service: master switch, the universe and source filters, the worker (never the receive thread), failures contained, nothing sent.
public sealed class ControlServiceTests : IDisposable
{
    private static readonly IPAddress Ma2 = IPAddress.Parse("2.0.0.2");

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ibt-control-" + Guid.NewGuid().ToString("N"));
    private readonly SettingsService _settings;
    private readonly FakeControlTargets _targets = new();
    private readonly List<FakeArtNetSource> _sources = [];
    private readonly ControlService _service;

    public ControlServiceTests()
    {
        Directory.CreateDirectory(_dir);
        _settings = new SettingsService(new SettingsStore(Path.Combine(_dir, "settings.json")));
        _service = new ControlService(_settings, _targets, () =>
        {
            var source = new FakeArtNetSource();
            _sources.Add(source);
            return source;
        });
    }

    public void Dispose()
    {
        _service.Dispose();
        _settings.Dispose();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // a late write of the settings
        }
    }

    private void Set(Func<ControlSettings, ControlSettings> change) => _settings.Update(s => s with { Control = change(s.Control) });

    private FakeArtNetSource TurnOn(Func<ControlSettings, ControlSettings>? change = null)
    {
        _service.Start();
        Set(c => (change?.Invoke(c) ?? c) with { Enabled = true });
        return Assert.Single(_sources);
    }

    private static ArtNetArrival Packet(int universe, byte[] levels, IPAddress? from = null, byte sequence = 0) =>
        new(new ArtDmxPacket(sequence, 0, universe, levels), from ?? Ma2);

    private static byte[] Levels(params (int Channel, byte Value)[] values)
    {
        var data = new byte[512];
        foreach (var (channel, value) in values)
        {
            data[channel - 1] = value;
        }

        return data;
    }

    private static bool WaitFor(Func<bool> condition, int ms = 3000)
    {
        var until = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < until)
        {
            if (condition())
            {
                return true;
            }

            Thread.Sleep(5);
        }

        return condition();
    }

    [Fact]
    public void It_is_off_at_install_and_opens_no_socket()
    {
        _service.Start();

        Assert.Empty(_sources);
        Assert.Equal(ControlSignal.Off, _service.GetStatus().Signal);
    }

    [Fact]
    public void The_master_switch_opens_and_closes_the_socket()
    {
        var source = TurnOn();

        Assert.Equal(1, source.Starts);
        Assert.Equal(ControlSignal.NoSignal, _service.GetStatus().Signal);

        Set(c => c with { Enabled = false });

        Assert.True(source.Disposed);
        Assert.Equal(ControlSignal.Off, _service.GetStatus().Signal);

        Set(c => c with { Enabled = true });
        Assert.Equal(2, _sources.Count); // a new socket
    }

    [Fact]
    public void A_port_in_use_is_shown_and_never_throws()
    {
        var source = TurnOn();

        source.SetState(ArtNetState.PortInUse);

        Assert.Equal(ControlSignal.PortInUse, _service.GetStatus().Signal);
    }

    [Fact]
    public void The_first_packet_is_a_baseline_and_a_later_change_runs_the_action_on_the_worker()
    {
        var source = TurnOn();
        var main = Environment.CurrentManagedThreadId;
        int? ranOn = null;
        _targets.Behavior = _ =>
        {
            ranOn = Environment.CurrentManagedThreadId;
            return ControlResult.Ok;
        };

        source.Raise(Packet(99, Levels((8, 255)))); // Cortar already held ON by the console
        Assert.Equal(ControlSignal.Ok, _service.GetStatus().Signal);
        Thread.Sleep(50);
        Assert.Empty(_targets.Ran);

        source.Raise(Packet(99, Levels()));
        Assert.True(WaitFor(() => _targets.Ran.Count == 1));
        Assert.Equal(new ControlCommand(8, ControlAction.Take, ControlCommandKind.Trigger), _targets.Ran[0]);
        Assert.NotEqual(main, ranOn);
    }

    [Fact]
    public void The_universe_shown_on_the_console_is_one_more_than_the_art_net_one()
    {
        var source = TurnOn(); // universe 100, offset on => art-net 99

        source.Raise(Packet(100, Levels())); // another universe: ignored
        Assert.Equal(ControlSignal.NoSignal, _service.GetStatus().Signal);
        source.Raise(Packet(99, Levels()));

        Assert.Equal(ControlSignal.Ok, _service.GetStatus().Signal);
    }

    [Fact]
    public void Without_the_offset_the_universe_is_taken_as_typed()
    {
        var source = TurnOn(c => c with { Universe = 100, UniverseOffset = false });

        source.Raise(Packet(99, Levels()));
        Assert.Equal(ControlSignal.NoSignal, _service.GetStatus().Signal);
        source.Raise(Packet(100, Levels()));

        Assert.Equal(ControlSignal.Ok, _service.GetStatus().Signal);
    }

    [Fact]
    public void The_source_filter_drops_other_senders_and_keeps_the_chosen_one()
    {
        var source = TurnOn(c => c with { SourceIp = "2.0.0.2" });

        source.Raise(Packet(99, Levels(), IPAddress.Parse("2.0.0.9")));
        var status = _service.GetStatus();
        Assert.Equal(ControlSignal.NoSignal, status.Signal);
        Assert.Equal(1, status.Ignored);

        source.Raise(Packet(99, Levels(), Ma2));

        status = _service.GetStatus();
        Assert.Equal(ControlSignal.Ok, status.Signal);
        Assert.Equal("2.0.0.2", status.LastSource);
    }

    [Fact]
    public void An_empty_source_filter_accepts_any_sender()
    {
        var source = TurnOn();

        source.Raise(Packet(99, Levels(), IPAddress.Parse("10.1.2.3")));

        Assert.Equal("10.1.2.3", _service.GetStatus().LastSource);
    }

    [Fact]
    public void An_older_packet_is_dropped_but_a_restarted_sender_is_taken_again()
    {
        var source = TurnOn();
        source.Raise(Packet(99, Levels(), sequence: 100));
        source.Raise(Packet(99, Levels(), sequence: 101));
        source.Raise(Packet(99, Levels((8, 255)), sequence: 99)); // reordered on the wire: dropped
        Thread.Sleep(50);
        Assert.Empty(_targets.Ran);

        // 8 older packets in a row: the sender restarted, so they count again.
        for (byte i = 1; i <= 8; i++)
        {
            source.Raise(Packet(99, Levels(), sequence: i));
        }

        source.Raise(Packet(99, Levels((8, 255)), sequence: 9));
        Assert.True(WaitFor(() => _targets.Ran.Count == 1));
    }

    [Fact]
    public void A_failing_action_is_contained_and_the_next_one_still_runs()
    {
        var source = TurnOn();
        source.Raise(Packet(99, Levels()));
        _targets.Behavior = command => command.Action == ControlAction.Take ? throw new InvalidOperationException("boom") : ControlResult.Ok;

        source.Raise(Packet(99, Levels((8, 255), (10, 255))));

        Assert.True(WaitFor(() => _targets.Ran.Count == 2));
        Assert.Equal([ControlAction.Take, ControlAction.Stop], _targets.Ran.Select(c => c.Action));
    }

    [Fact]
    public void A_slow_target_never_blocks_the_receive_thread_and_faders_do_not_pile_up()
    {
        var source = TurnOn(c => c with { Rows = c.Rows.Select(r => r.Channel == 21 ? r with { Pickup = false } : r).ToList() });
        source.Raise(Packet(99, Levels()));
        using var gate = new ManualResetEventSlim(false);
        _targets.Block = gate;
        source.Raise(Packet(99, Levels((8, 255)))); // the worker takes this and blocks

        var watch = System.Diagnostics.Stopwatch.StartNew();
        for (byte value = 10; value < 200; value += 10)
        {
            source.Raise(Packet(99, Levels((8, 255), (21, value))));
            Thread.Sleep(40);
        }

        watch.Stop();
        Assert.True(watch.ElapsedMilliseconds < 1500, "the receive thread waited for the target");

        gate.Set();
        Assert.True(WaitFor(() => _targets.Ran.Count(c => c.Kind == ControlCommandKind.SetLevel) >= 1));
        Thread.Sleep(100);

        // Only the newest value of the fader was left in the queue (plus at most the one the worker already held).
        var levels = _targets.Ran.Where(c => c.Kind == ControlCommandKind.SetLevel).ToList();
        Assert.True(levels.Count <= 3, "faders piled up: " + levels.Count);
        Assert.Equal(190 * 100.0 / 255.0, levels[^1].Value, 1);
    }

    [Fact]
    public void Test_runs_the_row_once_through_the_same_queue()
    {
        _service.Start();

        _service.Test(new ControlRow(3, ControlAction.SelectCamera, ControlMode.Select, Parameter: 3));
        _service.Test(new ControlRow(8, ControlAction.Take, ControlMode.Button));

        Assert.True(WaitFor(() => _targets.Ran.Count == 2));
        Assert.Equal(new ControlCommand(3, ControlAction.SelectCamera, ControlCommandKind.Select, Parameter: 3), _targets.Ran[0]);
        Assert.Equal(new ControlCommand(8, ControlAction.Take, ControlCommandKind.Trigger), _targets.Ran[1]);
    }

    [Fact]
    public void Signal_loss_keeps_everything_and_a_returning_signal_does_not_fire_from_the_held_values()
    {
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var own = new FakeArtNetSource();
        using var service = new ControlService(_settings, _targets, () => own, clock: clock);
        service.Start();
        Set(c => c with { Enabled = true });
        own.Raise(Packet(99, Levels()));
        Assert.Equal(ControlSignal.Ok, service.GetStatus().Signal);

        clock.Advance(TimeSpan.FromSeconds(4)); // no packet for the chosen universe: "sem sinal"

        Assert.Equal(ControlSignal.NoSignal, service.GetStatus().Signal);
        Assert.True(service.GetStatus().SinceLastPacket >= TimeSpan.FromSeconds(3));
        own.Raise(Packet(99, Levels((8, 255)))); // the console comes back holding Cortar ON
        Assert.Equal(ControlSignal.Ok, service.GetStatus().Signal);
        Thread.Sleep(50);
        Assert.Empty(_targets.Ran);
    }

    [Fact]
    public void Changing_the_universe_makes_the_next_packet_a_new_baseline()
    {
        var source = TurnOn();
        source.Raise(Packet(99, Levels()));

        Set(c => c with { Universe = 5 });
        source.Raise(Packet(4, Levels((8, 255))));
        Thread.Sleep(50);

        Assert.Empty(_targets.Ran);
    }

    [Fact]
    public void Garbage_rows_in_the_map_do_not_stop_the_service()
    {
        var source = TurnOn(c => c with { Rows = [new ControlRow(8, ControlAction.Take, ControlMode.Button)] });
        source.Raise(Packet(99, Levels()));

        source.Raise(Packet(99, Levels((8, 255))));

        Assert.True(WaitFor(() => _targets.Ran.Count == 1));
    }
}
