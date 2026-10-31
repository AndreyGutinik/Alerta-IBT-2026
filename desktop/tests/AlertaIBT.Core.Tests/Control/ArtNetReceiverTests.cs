using System.Net;
using System.Net.Sockets;
using AlertaIBT.Core.Control;

namespace AlertaIBT.Core.Tests.Control;

// The real UDP socket on localhost: a packet sent by the test arrives; garbage does not; a taken port is reported and retried.
public sealed class ArtNetReceiverTests
{
    private static int FreePort()
    {
        using var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
    }

    private static void Send(int port, byte[] bytes)
    {
        using var client = new UdpClient();
        client.Send(bytes, bytes.Length, new IPEndPoint(IPAddress.Loopback, port));
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

            Thread.Sleep(10);
        }

        return condition();
    }

    [Fact]
    public void A_packet_sent_to_the_port_arrives_with_its_universe_and_source()
    {
        var port = FreePort();
        using var receiver = new ArtNetReceiver(port);
        var arrivals = new List<ArtNetArrival>();
        receiver.Packet += a =>
        {
            lock (arrivals)
            {
                arrivals.Add(a);
            }
        };
        receiver.Start();
        Assert.Equal(ArtNetState.Listening, receiver.State);

        var data = new byte[512];
        data[7] = 255;
        Send(port, ArtNetParser.BuildDmx(99, data, 5));

        Assert.True(WaitFor(() => { lock (arrivals) { return arrivals.Count == 1; } }));
        lock (arrivals)
        {
            Assert.Equal(99, arrivals[0].Packet.Universe);
            Assert.Equal(255, arrivals[0].Packet.Data[7]);
            Assert.Equal(IPAddress.Loopback, arrivals[0].Source);
        }
    }

    [Fact]
    public void Garbage_and_other_opcodes_do_not_arrive_and_do_not_stop_the_receiver()
    {
        var port = FreePort();
        using var receiver = new ArtNetReceiver(port);
        var count = 0;
        receiver.Packet += _ => Interlocked.Increment(ref count);
        receiver.Start();

        Send(port, [1, 2, 3]);
        Send(port, "Art-Net\0"u8.ToArray());
        var poll = ArtNetParser.BuildDmx(1, new byte[4]);
        poll[9] = 0x20;
        Send(port, poll);
        Send(port, ArtNetParser.BuildDmx(1, new byte[4]));

        Assert.True(WaitFor(() => Volatile.Read(ref count) == 1));
        Thread.Sleep(100);
        Assert.Equal(1, Volatile.Read(ref count));
        Assert.Equal(ArtNetState.Listening, receiver.State);
    }

    [Fact]
    public void A_taken_port_is_reported_and_the_receiver_binds_when_it_is_freed()
    {
        var port = FreePort();
        var holder = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { ExclusiveAddressUse = true };
        holder.Bind(new IPEndPoint(IPAddress.Any, port));
        using var receiver = new ArtNetReceiver(port, TimeSpan.FromMilliseconds(80));
        var arrived = 0;
        receiver.Packet += _ => Interlocked.Increment(ref arrived);

        receiver.Start(); // must not throw

        Assert.Equal(ArtNetState.PortInUse, receiver.State);
        holder.Dispose();
        Assert.True(WaitFor(() => receiver.State == ArtNetState.Listening));
        Send(port, ArtNetParser.BuildDmx(0, new byte[4]));
        Assert.True(WaitFor(() => Volatile.Read(ref arrived) == 1));
    }

    [Fact]
    public void It_coexists_with_another_listener_that_also_allows_reuse()
    {
        var port = FreePort();
        using var other = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        other.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        other.ExclusiveAddressUse = false;
        other.Bind(new IPEndPoint(IPAddress.Any, port));
        using var receiver = new ArtNetReceiver(port);

        receiver.Start();

        Assert.Equal(ArtNetState.Listening, receiver.State);
    }

    [Fact]
    public void Stop_closes_the_socket_so_the_port_is_free_again()
    {
        var port = FreePort();
        using var receiver = new ArtNetReceiver(port);
        receiver.Start();
        receiver.Stop();

        Assert.Equal(ArtNetState.Stopped, receiver.State);
        using var taker = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { ExclusiveAddressUse = true };
        taker.Bind(new IPEndPoint(IPAddress.Any, port)); // would throw if the receiver still held it
    }

    [Fact]
    public void Start_after_stop_listens_again_and_dispose_is_safe_twice()
    {
        var port = FreePort();
        var receiver = new ArtNetReceiver(port);
        var count = 0;
        receiver.Packet += _ => Interlocked.Increment(ref count);

        receiver.Start();
        receiver.Stop();
        receiver.Start();
        Send(port, ArtNetParser.BuildDmx(0, new byte[2]));

        Assert.True(WaitFor(() => Volatile.Read(ref count) == 1));
        receiver.Dispose();
        receiver.Dispose();
        Assert.Equal(ArtNetState.Stopped, receiver.State);
    }
}
