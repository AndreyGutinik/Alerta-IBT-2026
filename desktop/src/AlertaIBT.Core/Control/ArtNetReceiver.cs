using System.Net;
using System.Net.Sockets;
using AlertaIBT.Core.Diagnostics;
using Microsoft.Extensions.Logging;

namespace AlertaIBT.Core.Control;

/// <summary>Where the Art-Net socket stands.</summary>
public enum ArtNetState
{
    /// <summary>Not listening (the master switch is off or the receiver was stopped).</summary>
    Stopped,

    /// <summary>Bound and receiving.</summary>
    Listening,

    /// <summary>The port is taken by another program: the receiver keeps trying.</summary>
    PortInUse,

    /// <summary>The socket could not be opened for another reason: the receiver keeps trying.</summary>
    Error,
}

/// <summary>A valid ArtDmx packet and where it came from.</summary>
/// <param name="Packet">The packet.</param>
/// <param name="Source">The address of the sender.</param>
public readonly record struct ArtNetArrival(ArtDmxPacket Packet, IPAddress Source);

/// <summary>Where Art-Net packets come from (the real socket, or a fake in tests).</summary>
public interface IArtNetSource : IDisposable
{
    /// <summary>The state of the socket.</summary>
    ArtNetState State { get; }

    /// <summary>Raised from the receive thread for every valid ArtDmx packet.</summary>
    event Action<ArtNetArrival>? Packet;

    /// <summary>Raised (from any thread) when <see cref="State"/> changes.</summary>
    event Action? StateChanged;

    /// <summary>Opens the socket (and keeps retrying while the port is taken). Returns at once.</summary>
    void Start();

    /// <summary>Closes the socket.</summary>
    void Stop();
}

/// <summary>
/// Receives Art-Net on UDP on every interface (<c>0.0.0.0</c>), only listening: it never sends anything. The socket allows address reuse, so other listeners
/// of the port (a DMX node program, a second instance of a viewer) can coexist; when the bind fails the state is <see cref="ArtNetState.PortInUse"/> and it
/// tries again every few seconds. A packet that is not a valid ArtDmx is dropped without a sound; nothing here ever throws to the caller.
/// </summary>
public sealed class ArtNetReceiver : IArtNetSource
{
    /// <summary>How long it waits before trying to bind again.</summary>
    public static readonly TimeSpan DefaultRetry = TimeSpan.FromSeconds(3);

    private readonly object _gate = new();
    private readonly int _port;
    private readonly TimeSpan _retry;
    private readonly ILogger? _logger;
    private Socket? _socket;
    private Timer? _retryTimer;
    private ArtNetState _state = ArtNetState.Stopped;
    private bool _wanted;
    private bool _disposed;
    private long _generation;

    /// <param name="port">The UDP port (6454 for Art-Net; a free one in tests).</param>
    /// <param name="retry">How long to wait between bind attempts; <see cref="DefaultRetry"/> when null.</param>
    /// <param name="logger">Gets one line per state change.</param>
    public ArtNetReceiver(int port = 6454, TimeSpan? retry = null, ILogger? logger = null)
    {
        _port = port;
        _retry = retry ?? DefaultRetry;
        _logger = logger;
    }

    /// <inheritdoc />
    public event Action<ArtNetArrival>? Packet;

    /// <inheritdoc />
    public event Action? StateChanged;

    /// <inheritdoc />
    public ArtNetState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    /// <summary>The port the socket is bound to (useful when the port given was 0); 0 when not listening.</summary>
    public int BoundPort
    {
        get
        {
            lock (_gate)
            {
                return (_socket?.LocalEndPoint as IPEndPoint)?.Port ?? 0;
            }
        }
    }

    /// <inheritdoc />
    public void Start()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _wanted = true;
            if (_socket is null)
            {
                TryBindLocked();
            }
        }

        RaiseStateChanged();
    }

    /// <inheritdoc />
    public void Stop()
    {
        lock (_gate)
        {
            _wanted = false;
            CloseLocked();
            SetStateLocked(ArtNetState.Stopped);
        }

        RaiseStateChanged();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _wanted = false;
            CloseLocked();
            _state = ArtNetState.Stopped;
        }
    }

    private void TryBindLocked()
    {
        _retryTimer?.Dispose();
        _retryTimer = null;
        Socket? socket = null;
        try
        {
            socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.ExclusiveAddressUse = false;
            socket.ReceiveBufferSize = 256 * 1024;
            socket.Bind(new IPEndPoint(IPAddress.Any, _port));
            _socket = socket;
            var generation = ++_generation;
            var thread = new Thread(() => ReceiveLoop(socket, generation)) { IsBackground = true, Name = "ArtNetReceiver" };
            thread.Start();
            SetStateLocked(ArtNetState.Listening);
        }
        catch (Exception ex)
        {
            socket?.Dispose();
            _socket = null;
            var inUse = ex is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse or SocketError.AccessDenied };
            SetStateLocked(inUse ? ArtNetState.PortInUse : ArtNetState.Error);
            Breadcrumbs.Add(inUse ? "artnet: porta em uso" : "artnet: erro ao abrir a porta");
            _logger?.LogWarning("Art-Net: n\U000000E3o foi poss\U000000EDvel abrir a porta {Port} ({Kind}); tenta de novo em {Seconds:0} s", _port, ex.GetType().Name, _retry.TotalSeconds);
            _retryTimer = new Timer(_ => Retry(), null, _retry, Timeout.InfiniteTimeSpan);
        }
    }

    private void Retry()
    {
        lock (_gate)
        {
            if (_disposed || !_wanted || _socket is not null)
            {
                return;
            }

            TryBindLocked();
        }

        RaiseStateChanged();
    }

    private void CloseLocked()
    {
        _retryTimer?.Dispose();
        _retryTimer = null;
        _generation++;
        try
        {
            _socket?.Dispose();
        }
        catch (Exception)
        {
            // closing never needs to report
        }

        _socket = null;
    }

    private void SetStateLocked(ArtNetState state)
    {
        if (_state == state)
        {
            return;
        }

        _state = state;
        _logger?.LogInformation("Art-Net: {State}", state);
    }

    private void RaiseStateChanged()
    {
        try
        {
            StateChanged?.Invoke();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Art-Net: ouvinte de estado falhou");
        }
    }

    private void ReceiveLoop(Socket socket, long generation)
    {
        var buffer = new byte[1024];
        EndPoint from = new IPEndPoint(IPAddress.Any, 0);
        while (true)
        {
            int count;
            try
            {
                count = socket.ReceiveFrom(buffer, ref from);
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
            {
                continue; // an ICMP "port unreachable" caused by somebody else's send: not a failure
            }
            catch (Exception)
            {
                break; // the socket was closed (Stop/Dispose) or failed
            }

            try
            {
                if (count > 0 && ArtNetParser.TryParseDmx(buffer.AsSpan(0, count), out var packet) && packet is not null && from is IPEndPoint remote)
                {
                    Packet?.Invoke(new ArtNetArrival(packet, remote.Address));
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Art-Net: erro ao tratar um pacote");
            }
        }

        // The loop ended without a Stop: the socket died, so start over (unless this very socket was already replaced).
        lock (_gate)
        {
            if (_disposed || !_wanted || generation != _generation)
            {
                return;
            }

            CloseLocked();
            SetStateLocked(ArtNetState.Error);
            _retryTimer = new Timer(_ => Retry(), null, _retry, Timeout.InfiniteTimeSpan);
        }

        RaiseStateChanged();
    }
}
