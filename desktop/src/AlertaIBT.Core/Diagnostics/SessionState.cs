using System.Globalization;
using System.Text;

namespace AlertaIBT.Core.Diagnostics;

/// <summary>Whether the monitors are lit (Windows tells the app with the console display state notification).</summary>
public enum DisplayPower
{
    /// <summary>Nothing was heard yet.</summary>
    Unknown,

    /// <summary>The display is on.</summary>
    On,

    /// <summary>The display is off (power saving, lid, "turn off the screen").</summary>
    Off,

    /// <summary>The display is dimmed (still on).</summary>
    Dimmed,
}

/// <summary>
/// What the Windows session is doing right now: locked or not, display on or off, the PC suspended, a remote (RDP / Parsec) session, when it last came back from
/// sleep. Two readers: every error line of the log carries <see cref="Describe"/> (a window-system error means something different in a locked session than in an
/// unlocked one), and the code that moves or repaints windows asks <see cref="ShouldPauseWindowWork"/> so it does not hammer windows that nobody can see and that
/// the system may refuse to update. Thread-safe; the writer is <c>SessionStateTracker</c> in the app, tests write it directly.
/// </summary>
public sealed class SessionState
{
    private readonly object _gate = new();
    private readonly TimeProvider _clock;
    private bool _locked;
    private bool _disconnected;
    private bool _suspended;
    private bool _remote;
    private DisplayPower _display = DisplayPower.Unknown;
    private DateTimeOffset? _lastResume;
    private DateTimeOffset? _lastUnlock;
    private DateTimeOffset? _lastDisplayChange;
    private bool _wasPaused;

    /// <param name="clock">Stamps the events; the system clock by default.</param>
    public SessionState(TimeProvider? clock = null) => _clock = clock ?? TimeProvider.System;

    /// <summary>The state of the running app.</summary>
    public static SessionState Default { get; } = new();

    /// <summary>Raised (on the thread that changed the state, outside the lock) when the state changed in any way.</summary>
    public event Action? Changed;

    /// <summary>Raised once each time windows may be touched again after a pause (unlock, resume, display back on, reconnect): re-apply positions and repaint now.</summary>
    public event Action? WorkResumed;

    /// <summary>The session is locked.</summary>
    public bool Locked
    {
        get
        {
            lock (_gate)
            {
                return _locked;
            }
        }
    }

    /// <summary>The display is off (not merely dimmed).</summary>
    public bool DisplayOff
    {
        get
        {
            lock (_gate)
            {
                return _display == DisplayPower.Off;
            }
        }
    }

    /// <summary>The app runs in a remote session (RDP, Parsec's virtual display session...).</summary>
    public bool Remote
    {
        get
        {
            lock (_gate)
            {
                return _remote;
            }
        }
    }

    /// <summary>
    /// True while nobody can see the windows or the system will not draw them: the session is locked or disconnected, the display is off or the PC is going to sleep.
    /// Position, z-order and repaint work waits (and is done once on <see cref="WorkResumed"/>); alerts and the queue are not affected.
    /// </summary>
    public bool ShouldPauseWindowWork
    {
        get
        {
            lock (_gate)
            {
                return PausedLocked();
            }
        }
    }

    /// <summary>The session was locked or unlocked.</summary>
    public void SetLocked(bool locked) => Change(() =>
    {
        if (_locked && !locked)
        {
            _lastUnlock = _clock.GetUtcNow();
        }

        _locked = locked;
    });

    /// <summary>A remote session was disconnected (the desktop is gone) or the console session came back.</summary>
    public void SetDisconnected(bool disconnected) => Change(() => _disconnected = disconnected);

    /// <summary>The display went on, off or dim.</summary>
    public void SetDisplay(DisplayPower display) => Change(() =>
    {
        _display = display;
        _lastDisplayChange = _clock.GetUtcNow();
    });

    /// <summary>The app runs in a remote session (or not).</summary>
    public void SetRemote(bool remote) => Change(() => _remote = remote);

    /// <summary>Windows is about to suspend the PC.</summary>
    public void Suspending() => Change(() => _suspended = true);

    /// <summary>The PC came back from suspension.</summary>
    public void Resumed() => Change(() =>
    {
        _suspended = false;
        _lastResume = _clock.GetUtcNow();
    });

    /// <summary>The monitors changed (resolution, a monitor added or removed).</summary>
    public void DisplayTopologyChanged() => Change(() => _lastDisplayChange = _clock.GetUtcNow());

    /// <summary>
    /// A short line for the log, for example <c>sessao: desbloqueada, tela ligada, local, ultimo retorno da suspensao 14:03:12 (ha 95 s)</c>. Never throws, no secrets.
    /// </summary>
    public string Describe()
    {
        var now = _clock.GetUtcNow();
        lock (_gate)
        {
            var text = new StringBuilder("sessao: ");
            text.Append(_locked ? "bloqueada" : "desbloqueada");
            if (_disconnected)
            {
                text.Append(", desconectada");
            }

            text.Append(", tela ").Append(_display switch
            {
                DisplayPower.On => "ligada",
                DisplayPower.Off => "desligada",
                DisplayPower.Dimmed => "escurecida",
                _ => "?",
            });
            if (_suspended)
            {
                text.Append(", PC suspendendo");
            }

            text.Append(_remote ? ", remota" : ", local");
            Append(text, "ultimo retorno da suspensao", _lastResume, now);
            Append(text, "ultimo desbloqueio", _lastUnlock, now);
            Append(text, "ultima mudanca de tela", _lastDisplayChange, now);
            return text.ToString();
        }
    }

    private static void Append(StringBuilder text, string label, DateTimeOffset? at, DateTimeOffset now)
    {
        if (at is { } moment)
        {
            text.Append(", ").Append(label).Append(' ')
                .Append(moment.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture))
                .Append(" (ha ").Append(Math.Max(0, (int)(now - moment).TotalSeconds).ToString(CultureInfo.InvariantCulture)).Append(" s)");
        }
    }

    private bool PausedLocked() => _locked || _disconnected || _suspended || _display == DisplayPower.Off;

    private void Change(Action mutate)
    {
        bool resumed;
        lock (_gate)
        {
            mutate();
            var paused = PausedLocked();
            resumed = _wasPaused && !paused;
            _wasPaused = paused;
        }

        Raise(Changed);
        if (resumed)
        {
            Raise(WorkResumed);
        }
    }

    private static void Raise(Action? handler)
    {
        try
        {
            handler?.Invoke();
        }
        catch (Exception)
        {
            // A listener never breaks the thread that reports the session (SystemEvents) or the log.
        }
    }
}
