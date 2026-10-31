using System.Net;
using AlertaIBT.Core.Diagnostics;
using AlertaIBT.Core.Settings;
using Microsoft.Extensions.Logging;

namespace AlertaIBT.Core.Control;

/// <summary>What happened when an action ran.</summary>
/// <param name="Success">Whether it worked.</param>
/// <param name="Message">Why it did not (pt-BR), or null.</param>
public readonly record struct ControlResult(bool Success, string? Message = null)
{
    /// <summary>It worked.</summary>
    public static ControlResult Ok { get; } = new(true);

    /// <summary>It did not, with the reason.</summary>
    public static ControlResult Fail(string message) => new(false, message);
}

/// <summary>The app side of the control: what an action really does. Called from the service's own worker thread, never from the receive loop.</summary>
public interface IControlTargets
{
    /// <summary>Runs a command through the same controllers and services the buttons and the API use (a target that needs the UI thread hands over itself).</summary>
    ControlResult Run(ControlCommand command);

    /// <summary>The app's own current value (0 to 100) of a fader action, or null when it has none now.</summary>
    double? GetLevel(ControlAction action);
}

/// <summary>The state of the whole control, as the header chip shows it.</summary>
public enum ControlSignal
{
    /// <summary>The master switch is off (the socket is closed).</summary>
    Off,

    /// <summary>Listening, no packet of the chosen universe for 3 seconds (or none yet).</summary>
    NoSignal,

    /// <summary>Packets of the chosen universe are arriving.</summary>
    Ok,

    /// <summary>Port 6454 is taken by another program; the app keeps trying.</summary>
    PortInUse,

    /// <summary>The socket could not be opened.</summary>
    Error,
}

/// <summary>What the header of the tab shows.</summary>
/// <param name="Signal">The state.</param>
/// <param name="SinceLastPacket">How long ago the last accepted packet came; null when none yet.</param>
/// <param name="LastSource">The sender of the last accepted packet; null when none yet.</param>
/// <param name="Ignored">How many packets were dropped by the universe or the source filter since the switch was turned on.</param>
public sealed record ControlStatus(ControlSignal Signal, TimeSpan? SinceLastPacket, string? LastSource, long Ignored);

/// <summary>
/// Joins the Art-Net receiver, the mapping engine and the app: while <see cref="ControlSettings.Enabled"/> is on it listens for the chosen universe
/// (optionally only from one address), feeds the engine, and runs the commands one after another on its own worker thread so the receive loop never waits
/// for the player, the NDI engine or Windows audio. Each command is wrapped in try/catch and leaves a breadcrumb; a failure is a log line (not more than
/// one per action every 5 seconds). Faders waiting in the queue are replaced by their newest value. Nothing is ever sent on the network.
/// </summary>
public sealed class ControlService : IDisposable
{
    /// <summary>How often the engine is asked for held-back fader values and a lost signal.</summary>
    public static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(20);

    private readonly SettingsService _settings;
    private readonly IControlTargets _targets;
    private readonly Func<IArtNetSource> _sourceFactory;
    private readonly ILogger? _logger;
    private readonly TimeProvider _clock;
    private readonly ControlEngine _engine;
    private readonly object _gate = new();
    private readonly object _queueGate = new();
    private readonly List<ControlCommand> _queue = [];
    private readonly Dictionary<ControlAction, long> _lastWarn = [];
    private readonly SemaphoreSlim _wake = new(0);
    private IArtNetSource? _source;
    private ITimer? _timer;
    private Thread? _worker;
    private bool _started;
    private bool _disposed;
    private ControlSettings _applied = new() { Enabled = false };
    private string? _lastSource;
    private long _ignored;
    private long _lastAccepted = long.MinValue;
    private bool _seen;
    private byte _lastSequence;
    private int _outOfOrder;
    private IPAddress? _sequenceSource;

    /// <param name="settings">Where the "Controle" settings live.</param>
    /// <param name="targets">What the commands do.</param>
    /// <param name="sourceFactory">Makes the Art-Net source (the real receiver in the app, a fake in tests); called each time the switch turns on.</param>
    /// <param name="logger">Receives the log lines.</param>
    /// <param name="clock">Dates the packets.</param>
    public ControlService(SettingsService settings, IControlTargets targets, Func<IArtNetSource>? sourceFactory = null, ILogger? logger = null, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(targets);

        _settings = settings;
        _targets = targets;
        _sourceFactory = sourceFactory ?? (() => new ArtNetReceiver(ControlSettings.ArtNetPort, logger: logger));
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
        _engine = new ControlEngine(targets.GetLevel, _clock);
    }

    /// <summary>Raised (from any thread) when the status may have changed.</summary>
    public event Action? Changed;

    /// <summary>The engine, for the screen (levels, pickup states).</summary>
    public ControlEngine Engine => _engine;

    /// <summary>The settings the service is running with now.</summary>
    public ControlSettings Applied
    {
        get
        {
            lock (_gate)
            {
                return _applied;
            }
        }
    }

    /// <summary>Starts watching the settings and, when the switch is on, listening.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_started || _disposed)
            {
                return;
            }

            _started = true;
            _worker = new Thread(WorkLoop) { IsBackground = true, Name = "ControlService" };
            _worker.Start();
            _timer = _clock.CreateTimer(_ => OnTick(), null, TickInterval, TickInterval);
        }

        _settings.Changed += OnSettingsChanged;
        Apply(_settings.Current.Control);
    }

    /// <summary>What the header shows now.</summary>
    public ControlStatus GetStatus()
    {
        lock (_gate)
        {
            var signal = ControlSignal.Off;
            if (_applied.Enabled && _source is not null)
            {
                signal = _source.State switch
                {
                    ArtNetState.PortInUse => ControlSignal.PortInUse,
                    ArtNetState.Error => ControlSignal.Error,
                    ArtNetState.Listening => _engine.HasSignal ? ControlSignal.Ok : ControlSignal.NoSignal,
                    _ => ControlSignal.NoSignal,
                };
            }

            return new ControlStatus(signal, _seen && _lastAccepted != long.MinValue ? _clock.GetElapsedTime(_lastAccepted) : null, _lastSource, Interlocked.Read(ref _ignored));
        }
    }

    /// <summary>
    /// Handles one packet as if the receiver had delivered it (the receiver calls this; so do the tests). Drops it when it is for another universe or from
    /// another address, or when it is older than one already seen.
    /// </summary>
    public void Feed(ArtNetArrival arrival)
    {
        try
        {
            ControlSettings settings;
            lock (_gate)
            {
                if (!_applied.Enabled || _disposed)
                {
                    return;
                }

                settings = _applied;
            }

            var packet = arrival.Packet;
            if (packet.Universe != settings.ArtNetUniverse)
            {
                return; // some other universe on the same network: not ours, not even worth counting
            }

            if (settings.SourceIp.Length > 0 && !string.Equals(arrival.Source.MapToIPv4().ToString(), settings.SourceIp, StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _ignored);
                return;
            }

            if (!AcceptSequence(arrival))
            {
                return;
            }

            var hadSignal = _engine.HasSignal;
            lock (_gate)
            {
                _seen = true;
                _lastAccepted = _clock.GetTimestamp();
                _lastSource = arrival.Source.MapToIPv4().ToString();
            }

            var commands = _engine.Process(packet.Data);
            Enqueue(commands);
            if (!hadSignal)
            {
                Breadcrumbs.Add("controle: sinal recebido");
                _logger?.LogInformation("Controle: sinal da MA2 recebido de {Source}", _lastSource);
                RaiseChanged();
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Controle: erro ao tratar um pacote");
        }
    }

    /// <summary>Runs one row's action once, as the "Testar" button does (goes through the same queue and the same targets).</summary>
    /// <param name="row">The row.</param>
    public void Test(ControlRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        var command = row.Mode switch
        {
            ControlMode.Select => new ControlCommand(row.Channel, row.Action, ControlCommandKind.Select, Parameter: row.Parameter),
            _ => new ControlCommand(row.Channel, row.Action, ControlCommandKind.Trigger),
        };
        Enqueue([command]);
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
        }

        _settings.Changed -= OnSettingsChanged;
        _timer?.Dispose();
        CloseSource();
        _wake.Release();
    }

    private void OnSettingsChanged(AppSettings settings) => Apply(settings.Control);

    private void Apply(ControlSettings next)
    {
        var open = false;
        var close = false;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            var previous = _applied;
            _applied = next;
            if (!next.Enabled)
            {
                close = _source is not null;
            }
            else if (_source is null)
            {
                open = true;
            }
            else if (previous.ArtNetUniverse != next.ArtNetUniverse || previous.SourceIp != next.SourceIp)
            {
                _engine.Reset(); // another universe or sender: the first packet of it is a new baseline
                _seen = false;
                _sequenceSource = null;
            }
        }

        _engine.SetRows(next.Rows);
        if (close)
        {
            CloseSource();
            Breadcrumbs.Add("controle: desligado");
            _logger?.LogInformation("Controle pela MA2 desligado");
            RaiseChanged();
        }
        else if (open)
        {
            OpenSource();
            Breadcrumbs.Add("controle: ligado");
            _logger?.LogInformation("Controle pela MA2 ligado (universo Art-Net {Universe})", next.ArtNetUniverse);
            RaiseChanged();
        }
    }

    private void OpenSource()
    {
        IArtNetSource source;
        lock (_gate)
        {
            if (_source is not null || _disposed)
            {
                return;
            }

            _engine.Reset();
            _seen = false;
            _ignored = 0;
            _lastSource = null;
            _lastAccepted = long.MinValue;
            _sequenceSource = null;
            source = _sourceFactory();
            _source = source;
        }

        source.Packet += Feed;
        source.StateChanged += RaiseChanged;
        source.Start();
    }

    private void CloseSource()
    {
        IArtNetSource? source;
        lock (_gate)
        {
            source = _source;
            _source = null;
        }

        if (source is null)
        {
            return;
        }

        source.Packet -= Feed;
        source.StateChanged -= RaiseChanged;
        source.Dispose();
        _engine.Reset();
    }

    // Art-Net numbers a sender's packets 1..255 (0 = not numbered). An older packet (reordered on the wire) is dropped; if many in a row look older the
    // sender restarted, so they are taken again.
    private bool AcceptSequence(ArtNetArrival arrival)
    {
        var sequence = arrival.Packet.Sequence;
        lock (_gate)
        {
            if (sequence == 0 || !Equals(_sequenceSource, arrival.Source) || _lastSequence == 0)
            {
                _sequenceSource = arrival.Source;
                _lastSequence = sequence;
                _outOfOrder = 0;
                return true;
            }

            var ahead = (byte)(sequence - _lastSequence);
            if (ahead > 128 && ++_outOfOrder < 8)
            {
                return false;
            }

            _outOfOrder = 0;
            _lastSequence = sequence;
            return true;
        }
    }

    private void OnTick()
    {
        try
        {
            var hadSignal = _engine.HasSignal;
            Enqueue(_engine.Tick());
            if (hadSignal && !_engine.HasSignal)
            {
                Breadcrumbs.Add("controle: sem sinal");
                _logger?.LogWarning("Controle: sem sinal da MA2 h\U000000E1 3 s; o app mant\U000000E9m tudo como est\U000000E1");
                RaiseChanged();
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Controle: erro no rel\U000000F3gio");
        }
    }

    private void Enqueue(IReadOnlyList<ControlCommand> commands)
    {
        if (commands.Count == 0)
        {
            return;
        }

        lock (_queueGate)
        {
            foreach (var command in commands)
            {
                if (command.Kind == ControlCommandKind.SetLevel)
                {
                    // A newer value of the same fader replaces the one still waiting: a slow target never builds a backlog.
                    var index = _queue.FindIndex(item => item.Kind == ControlCommandKind.SetLevel && item.Action == command.Action && item.Channel == command.Channel);
                    if (index >= 0)
                    {
                        _queue[index] = command;
                        continue;
                    }
                }

                _queue.Add(command);
                _wake.Release();
            }
        }
    }

    private void WorkLoop()
    {
        while (true)
        {
            _wake.Wait();
            if (_disposed)
            {
                return;
            }

            ControlCommand command;
            lock (_queueGate)
            {
                if (_queue.Count == 0)
                {
                    continue;
                }

                command = _queue[0];
                _queue.RemoveAt(0);
            }

            RunOne(command);
        }
    }

    private void RunOne(ControlCommand command)
    {
        var name = ControlCatalog.ToText(command.Action);
        if (command.Kind != ControlCommandKind.SetLevel)
        {
            Breadcrumbs.Add("controle: " + name + " " + command.Kind);
        }

        try
        {
            var result = _targets.Run(command);
            if (!result.Success)
            {
                Warn(command.Action, "Controle: " + name + " n\U000000E3o foi executado (" + (result.Message ?? "sem motivo") + ")");
            }
            else if (command.Kind != ControlCommandKind.SetLevel)
            {
                _logger?.LogInformation("Controle: {Action} ({Kind}, canal {Channel})", name, command.Kind, command.Channel);
            }
        }
        catch (Exception ex)
        {
            Breadcrumbs.Add("controle: erro em " + name);
            Warn(command.Action, "Controle: erro ao executar " + name + " (" + ex.GetType().Name + ")");
        }
    }

    private void Warn(ControlAction action, string text)
    {
        var now = _clock.GetTimestamp();
        lock (_queueGate)
        {
            if (_lastWarn.TryGetValue(action, out var before) && _clock.GetElapsedTime(before, now) < TimeSpan.FromSeconds(5))
            {
                return;
            }

            _lastWarn[action] = now;
        }

        _logger?.LogWarning("{Text}", text);
    }

    private void RaiseChanged()
    {
        try
        {
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Controle: ouvinte falhou");
        }
    }
}
