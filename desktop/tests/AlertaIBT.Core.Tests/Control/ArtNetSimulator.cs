using System.Globalization;
using System.Net;
using System.Net.Sockets;
using AlertaIBT.Core.Control;
using AlertaIBT.Core.Settings;

namespace AlertaIBT.Core.Tests.Control;

/// <summary>
/// A stand-in for the grandMA2 for end-to-end tests against the real app (see docs/CONTROLE-MA2.md, "Testar sem a MA2"). It sends ArtDmx for one
/// universe at a steady rate while a small script changes the channels. Script lines (blank lines and <c>#</c> comments are skipped):
/// <code>
/// target 127.0.0.1 6454     where to send (default 127.0.0.1 6454; use the app PC address, or 255.255.255.255 to broadcast)
/// universe 99               the universe ON THE WIRE (the app's default screen universe 100 is 99 here)
/// rate 40                   packets per second (default 40)
/// set 8 255                 channel 8 to 255 (ON)
/// pulse 8 300               channel 8 ON for 300 ms, then OFF
/// ramp 21 0 255 3000        channel 21 from 0 to 255 over 3000 ms
/// wait 1500                 let time pass
/// silence 4000              stop sending for 4000 ms (the app must say "sem sinal")
/// </code>
/// </summary>
public sealed class ArtNetSimulator : IDisposable
{
    private readonly UdpClient _client = new();
    private readonly byte[] _levels = new byte[512];
    private readonly object _gate = new();
    private IPEndPoint _target = new(IPAddress.Loopback, ControlSettings.ArtNetPort);
    private int _universe = 99;
    private int _rate = 40;
    private bool _silent;
    private byte _sequence;

    /// <summary>How many packets were sent.</summary>
    public long Sent { get; private set; }

    /// <summary>Sends to <paramref name="address"/> and <paramref name="port"/>.</summary>
    public void SetTarget(IPAddress address, int port)
    {
        _target = new IPEndPoint(address, port);
        _client.EnableBroadcast = true;
    }

    /// <summary>Sets the universe on the wire.</summary>
    public void SetUniverse(int universe) => _universe = universe;

    /// <summary>Sets a channel (1 to 512).</summary>
    public void Set(int channel, byte value)
    {
        lock (_gate)
        {
            _levels[channel - 1] = value;
        }
    }

    /// <summary>Sends the current levels once.</summary>
    public void SendFrame()
    {
        byte[] packet;
        lock (_gate)
        {
            _sequence = (byte)(_sequence == 255 ? 1 : _sequence + 1);
            packet = ArtNetParser.BuildDmx(_universe, _levels, _sequence);
        }

        _client.Send(packet, packet.Length, _target);
        Sent++;
    }

    /// <summary>Runs a script; sends frames at the script's rate the whole time.</summary>
    public async Task RunAsync(string script, CancellationToken cancel = default)
    {
        var lines = script.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).ToList();
        using var pump = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        var pumping = Task.Run(() => PumpAsync(pump.Token), CancellationToken.None);
        try
        {
            foreach (var line in lines)
            {
                await ExecuteAsync(line, cancel);
            }
        }
        finally
        {
            await pump.CancelAsync();
            await pumping;
        }
    }

    /// <inheritdoc />
    public void Dispose() => _client.Dispose();

    private async Task PumpAsync(CancellationToken cancel)
    {
        try
        {
            while (!cancel.IsCancellationRequested)
            {
                if (!_silent)
                {
                    SendFrame();
                }

                await Task.Delay(Math.Max(1, 1000 / Math.Max(1, _rate)), cancel);
            }
        }
        catch (OperationCanceledException)
        {
            // the script ended
        }
    }

    private async Task ExecuteAsync(string line, CancellationToken cancel)
    {
        var part = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        int Number(int i) => int.Parse(part[i], CultureInfo.InvariantCulture);
        switch (part[0].ToLowerInvariant())
        {
            case "target":
                SetTarget(IPAddress.Parse(part[1]), part.Length > 2 ? Number(2) : ControlSettings.ArtNetPort);
                break;
            case "universe":
                SetUniverse(Number(1));
                break;
            case "rate":
                _rate = Number(1);
                break;
            case "set":
                Set(Number(1), (byte)Math.Clamp(Number(2), 0, 255));
                break;
            case "pulse":
                Set(Number(1), 255);
                await Task.Delay(Number(2), cancel);
                Set(Number(1), 0);
                break;
            case "ramp":
                await RampAsync(Number(1), Number(2), Number(3), Number(4), cancel);
                break;
            case "wait":
                await Task.Delay(Number(1), cancel);
                break;
            case "silence":
                _silent = true;
                await Task.Delay(Number(1), cancel);
                _silent = false;
                break;
            default:
                throw new FormatException("linha do roteiro desconhecida: " + line);
        }
    }

    private async Task RampAsync(int channel, int from, int to, int ms, CancellationToken cancel)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < ms)
        {
            var t = watch.ElapsedMilliseconds / (double)ms;
            Set(channel, (byte)Math.Round(from + ((to - from) * t)));
            await Task.Delay(10, cancel);
        }

        Set(channel, (byte)to);
    }
}

// The simulator itself, and the way to run it by hand:
//   $env:IBT_ARTNET_SCRIPT = "C:\caminho\roteiro.txt"   (the script, see ArtNetSimulator)
//   dotnet test --project desktop/tests/AlertaIBT.Core.Tests --filter-class "*ArtNetSimulatorTool*"
// Without the variable the tool test is skipped.
public sealed class ArtNetSimulatorTool
{
    [Fact]
    public async Task Replays_the_script_named_by_IBT_ARTNET_SCRIPT()
    {
        var path = Environment.GetEnvironmentVariable("IBT_ARTNET_SCRIPT");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(path) || !File.Exists(path), "defina IBT_ARTNET_SCRIPT com o caminho de um roteiro para rodar o simulador");

        using var simulator = new ArtNetSimulator();
        await simulator.RunAsync(await File.ReadAllTextAsync(path!, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);

        Assert.True(simulator.Sent > 0);
    }
}

public sealed class ArtNetSimulatorTests
{
    [Fact]
    public async Task A_script_reaches_a_real_receiver_with_the_channels_it_sets()
    {
        using var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
        probe.Dispose();
        using var receiver = new ArtNetReceiver(port);
        var seen = new List<ArtDmxPacket>();
        receiver.Packet += a =>
        {
            lock (seen)
            {
                seen.Add(a.Packet);
            }
        };
        receiver.Start();
        using var simulator = new ArtNetSimulator();

        await simulator.RunAsync($"target 127.0.0.1 {port}\nuniverse 99\nrate 100\nset 8 200\nwait 150\npulse 9 100\nwait 100\nramp 21 0 255 200\n", TestContext.Current.CancellationToken);

        Assert.True(simulator.Sent >= 10);
        lock (seen)
        {
            Assert.NotEmpty(seen);
            Assert.All(seen, p => Assert.Equal(99, p.Universe));
            Assert.Contains(seen, p => p.Data[7] == 200);
            Assert.Contains(seen, p => p.Data[8] == 255);
            Assert.Contains(seen, p => p.Data[8] == 0 && p.Data[7] == 200);
            Assert.True(seen[^1].Data[20] >= 200);
        }
    }

    [Fact]
    public async Task An_unknown_line_is_reported()
    {
        using var simulator = new ArtNetSimulator();

        await Assert.ThrowsAsync<FormatException>(() => simulator.RunAsync("jump 3", TestContext.Current.CancellationToken));
    }
}
