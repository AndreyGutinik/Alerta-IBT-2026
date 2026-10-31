using AlertaIBT.Core.Settings;

namespace AlertaIBT.Core.Control;

/// <summary>What the engine asks the app to do.</summary>
public enum ControlCommandKind
{
    /// <summary>Run the action once (a button; for a toggle action such as the layer or the logo it flips it).</summary>
    Trigger,

    /// <summary>Select a camera (<see cref="ControlCommand.Parameter"/>, 1 to 6) into Preview.</summary>
    Select,

    /// <summary>Put the action ON or OFF (<see cref="ControlCommand.On"/>).</summary>
    SetState,

    /// <summary>Move a fader-like value to <see cref="ControlCommand.Value"/> (0 to 100 percent).</summary>
    SetLevel,
}

/// <summary>One thing the console asked the app to do.</summary>
/// <param name="Channel">The channel that caused it.</param>
/// <param name="Action">What to do.</param>
/// <param name="Kind">How.</param>
/// <param name="Parameter">The camera of a <see cref="ControlCommandKind.Select"/>.</param>
/// <param name="On">The state of a <see cref="ControlCommandKind.SetState"/>.</param>
/// <param name="Value">The percent of a <see cref="ControlCommandKind.SetLevel"/>.</param>
public readonly record struct ControlCommand(int Channel, ControlAction Action, ControlCommandKind Kind, int Parameter = 0, bool On = false, double Value = 0);

/// <summary>Where the soft pickup of a fader stands.</summary>
public enum FaderPickup
{
    /// <summary>Not a fader, or its pickup is off and it never waits.</summary>
    None,

    /// <summary>The fader has not reached the app's value yet: it is ignored.</summary>
    Waiting,

    /// <summary>The fader crossed the app's value: it drives the app.</summary>
    Following,
}

/// <summary>What the screen shows of one row.</summary>
/// <param name="Level">The value 0 to 255 of the channel now.</param>
/// <param name="On">Whether a digital reading of the channel is ON (128 or more).</param>
/// <param name="Pickup">The pickup of a fader row.</param>
public readonly record struct ControlRowState(byte Level, bool On, FaderPickup Pickup);

/// <summary>
/// The brain of the "Controle" tab, with no sockets and no UI: it takes the 512 levels of every packet, remembers the baseline, and answers with the
/// commands the map asks for. Rules: the first packet after start or after a loss only records the baseline (an already held ON never fires); a
/// digital channel is ON at 128 or more; a button fires on any change or only on the rising edge, never twice within 100 ms; a select picks a camera on
/// the rising edge; a state row follows the channel; a fader goes 0..255 to 0..100 percent through a soft pickup (ignored until it crosses the app's own
/// value, back to waiting if the app's value is changed from elsewhere) and its updates are limited to about 30 per second per row (the newest wins).
/// Signal: none when no packet arrived for 3 seconds (<see cref="Tick"/>), and the app keeps everything as it is meanwhile. Thread safe.
/// </summary>
public sealed class ControlEngine
{
    /// <summary>A channel at or above this value is ON.</summary>
    public const int OnThreshold = 128;

    /// <summary>A button does not fire again within this time.</summary>
    public static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(100);

    /// <summary>A fader row sends at most one value in this time (about 30 Hz).</summary>
    public static readonly TimeSpan FaderInterval = TimeSpan.FromMilliseconds(33);

    /// <summary>No packet for this long is "sem sinal".</summary>
    public static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(3);

    /// <summary>A fader within this many percent of the app's value has picked it up.</summary>
    public const double PickupTolerance = 2.0;

    /// <summary>The app's value moved this many percent away from what the fader last sent: it was changed elsewhere.</summary>
    public const double ExternalChange = 3.0;

    private readonly object _gate = new();
    private readonly Func<ControlAction, double?> _appLevel;
    private readonly TimeProvider _clock;
    private readonly byte[] _levels = new byte[ControlSettings.ChannelCount];
    private readonly bool[] _known = new bool[ControlSettings.ChannelCount];
    private RowState[] _rows = [];
    private IReadOnlyList<ControlRow> _map = [];
    private bool _baseline;
    private bool _signal;
    private long _lastPacket = long.MinValue;

    /// <param name="appLevel">The app's own current value (0 to 100) of a fader action, or null when it has none now (no camera in Preview).</param>
    /// <param name="clock">Dates the packets.</param>
    public ControlEngine(Func<ControlAction, double?> appLevel, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(appLevel);

        _appLevel = appLevel;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>Whether a packet of the chosen universe arrived in the last 3 seconds.</summary>
    public bool HasSignal
    {
        get
        {
            lock (_gate)
            {
                return _signal;
            }
        }
    }

    /// <summary>How long ago the last packet came, or null when none has.</summary>
    public TimeSpan? SinceLastPacket
    {
        get
        {
            lock (_gate)
            {
                return _lastPacket == long.MinValue ? null : _clock.GetElapsedTime(_lastPacket);
            }
        }
    }

    /// <summary>Whether the baseline was taken (the next packet will already act).</summary>
    public bool HasBaseline
    {
        get
        {
            lock (_gate)
            {
                return _baseline;
            }
        }
    }

    /// <summary>Sets the map. A row that stays the same keeps its state; a new row starts from the levels now (it never fires from them).</summary>
    public void SetRows(IReadOnlyList<ControlRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        lock (_gate)
        {
            var next = new RowState[rows.Count];
            for (var i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                var previous = i < _map.Count && _map[i].Channel == row.Channel && _map[i].Action == row.Action && _map[i].Mode == row.Mode ? _rows[i] : null;
                next[i] = previous ?? NewState(row);
                if (previous is not null && _map[i].Pickup && !row.Pickup)
                {
                    next[i].Following = true;
                }
                else if (previous is not null && !_map[i].Pickup && row.Pickup && IsFader(row))
                {
                    next[i].Following = false;
                }
            }

            _map = rows;
            _rows = next;
        }
    }

    /// <summary>
    /// Takes one packet (<paramref name="channels"/>[0] is channel 1) and returns what to do. The first packet after start or a loss is the baseline: it
    /// returns nothing.
    /// </summary>
    public IReadOnlyList<ControlCommand> Process(ReadOnlySpan<byte> channels)
    {
        lock (_gate)
        {
            var now = _clock.GetTimestamp();
            var commands = new List<ControlCommand>();
            var count = Math.Min(channels.Length, ControlSettings.ChannelCount);
            for (var i = 0; i < count; i++)
            {
                _levels[i] = channels[i];
                _known[i] = true;
            }

            _lastPacket = now;
            _signal = true;

            if (!_baseline)
            {
                _baseline = true;
                for (var i = 0; i < _rows.Length; i++)
                {
                    Rebase(i);
                }

                return commands;
            }

            for (var i = 0; i < _rows.Length; i++)
            {
                var row = _map[i];
                var state = _rows[i];
                var index = row.Channel - 1;
                if (index >= count)
                {
                    continue; // this packet is shorter than the channel: nothing new to read
                }

                var value = _levels[index];
                if (IsFader(row))
                {
                    ProcessFader(row, state, value, now, commands);
                }
                else
                {
                    ProcessDigital(row, state, value >= OnThreshold, now, commands);
                }
            }

            return commands;
        }
    }

    /// <summary>
    /// Call about 30 times a second: sends the fader values that were held back by the rate limit and notices a lost signal (which keeps the app as it is
    /// and makes the next packet a new baseline).
    /// </summary>
    public IReadOnlyList<ControlCommand> Tick()
    {
        lock (_gate)
        {
            var now = _clock.GetTimestamp();
            if (_signal && _clock.GetElapsedTime(_lastPacket, now) >= SignalTimeout)
            {
                LoseSignalLocked();
                return [];
            }

            if (!_signal)
            {
                return [];
            }

            List<ControlCommand>? commands = null;
            for (var i = 0; i < _rows.Length; i++)
            {
                var state = _rows[i];
                if (state.Pending is { } pending && _clock.GetElapsedTime(state.LastSend, now) >= FaderInterval)
                {
                    state.Pending = null;
                    state.LastSend = now;
                    (commands ??= []).Add(new ControlCommand(_map[i].Channel, _map[i].Action, ControlCommandKind.SetLevel, Value: pending));
                }
            }

            return commands ?? [];
        }
    }

    /// <summary>The signal is gone for sure (the receiver was stopped or the settings changed): forget the baseline.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            LoseSignalLocked();
            _lastPacket = long.MinValue;
            Array.Clear(_known);
            Array.Clear(_levels);
        }
    }

    /// <summary>The state of every row of the map, in the order of the map.</summary>
    public IReadOnlyList<ControlRowState> GetRowStates()
    {
        lock (_gate)
        {
            var list = new ControlRowState[_rows.Length];
            for (var i = 0; i < _rows.Length; i++)
            {
                var row = _map[i];
                var level = _levels[row.Channel - 1];
                var pickup = !IsFader(row) || !row.Pickup
                    ? FaderPickup.None
                    : _rows[i].Following ? FaderPickup.Following : FaderPickup.Waiting;
                list[i] = new ControlRowState(level, level >= OnThreshold, pickup);
            }

            return list;
        }
    }

    /// <summary>The 512 levels now (a copy).</summary>
    public byte[] GetLevels()
    {
        lock (_gate)
        {
            return (byte[])_levels.Clone();
        }
    }

    private static bool IsFader(ControlRow row) => row.Mode == ControlMode.Fader;

    private RowState NewState(ControlRow row)
    {
        var state = new RowState();
        if (_baseline)
        {
            Seed(row, state);
        }

        return state;
    }

    private void Rebase(int index) => Seed(_map[index], _rows[index]);

    // A row learns the level of its channel without acting: a digital one its ON/OFF, a fader its position and (pickup off) that it follows.
    private void Seed(ControlRow row, RowState state)
    {
        var channelIndex = row.Channel - 1;
        var value = _levels[channelIndex];
        state.On = value >= OnThreshold;
        state.LastFire = long.MinValue;
        state.LastSend = long.MinValue;
        state.Pending = null;
        state.PreviousPercent = Percent(value);
        state.LastSentPercent = null;
        state.Following = false;
        if (IsFader(row))
        {
            if (!row.Pickup)
            {
                state.Following = true;
            }
            else if (_appLevel(row.Action) is { } app && Math.Abs(state.PreviousPercent - app) <= PickupTolerance)
            {
                state.Following = true;
                state.LastSentPercent = app;
            }
        }
    }

    private static double Percent(byte value) => value * 100.0 / 255.0;

    private void ProcessDigital(ControlRow row, RowState state, bool on, long now, List<ControlCommand> commands)
    {
        if (on == state.On)
        {
            return;
        }

        state.On = on;
        if (!row.Enabled || !ControlCatalog.Get(row.Action).Available)
        {
            return;
        }

        switch (row.Mode)
        {
            case ControlMode.State:
                commands.Add(new ControlCommand(row.Channel, row.Action, ControlCommandKind.SetState, On: on));
                break;

            case ControlMode.Select:
                if (on && DebouncePasses(state, now))
                {
                    commands.Add(new ControlCommand(row.Channel, row.Action, ControlCommandKind.Select, Parameter: row.Parameter));
                }

                break;

            default:
                if ((on || row.Trigger == ControlTrigger.AnyChange) && DebouncePasses(state, now))
                {
                    commands.Add(new ControlCommand(row.Channel, row.Action, ControlCommandKind.Trigger));
                }

                break;
        }
    }

    private bool DebouncePasses(RowState state, long now)
    {
        if (state.LastFire != long.MinValue && _clock.GetElapsedTime(state.LastFire, now) < Debounce)
        {
            return false;
        }

        state.LastFire = now;
        return true;
    }

    private void ProcessFader(ControlRow row, RowState state, byte value, long now, List<ControlCommand> commands)
    {
        var percent = Percent(value);
        var previous = state.PreviousPercent;
        state.PreviousPercent = percent;
        if (!row.Enabled || !ControlCatalog.Get(row.Action).Available)
        {
            return;
        }

        var engagedNow = false;
        if (row.Pickup)
        {
            var app = _appLevel(row.Action);
            if (state.Following && app is { } current && state.LastSentPercent is { } sent && Math.Abs(current - sent) > ExternalChange)
            {
                // The app's value was changed from elsewhere (the slider on the screen): the fader must cross it again before it takes over.
                state.Following = false;
                state.Pending = null;
            }

            if (!state.Following)
            {
                if (app is not { } target)
                {
                    return;
                }

                var crossed = Math.Abs(percent - target) <= PickupTolerance || (previous - target) * (percent - target) <= 0;
                if (!crossed)
                {
                    return;
                }

                state.Following = true;
                engagedNow = true;
            }
        }

        var changed = Math.Abs(percent - previous) >= 0.01;
        if (!engagedNow && !changed)
        {
            return;
        }

        if (!engagedNow && state.LastSentPercent is { } last && Math.Abs(last - percent) < 0.01)
        {
            return;
        }

        state.LastSentPercent = percent;
        if (state.LastSend == long.MinValue || _clock.GetElapsedTime(state.LastSend, now) >= FaderInterval)
        {
            // Quiet row: send at once. Otherwise the newest value waits for Tick().
            state.LastSend = now;
            state.Pending = null;
            commands.Add(new ControlCommand(row.Channel, row.Action, ControlCommandKind.SetLevel, Value: percent));
        }
        else
        {
            state.Pending = percent;
        }
    }

    private void LoseSignalLocked()
    {
        _signal = false;
        _baseline = false;
        foreach (var state in _rows)
        {
            state.Pending = null;
            state.Following = false;
        }
    }

    private sealed class RowState
    {
        public bool On { get; set; }

        public long LastFire { get; set; } = long.MinValue;

        public long LastSend { get; set; } = long.MinValue;

        public double PreviousPercent { get; set; }

        public double? LastSentPercent { get; set; }

        public double? Pending { get; set; }

        public bool Following { get; set; }
    }
}
