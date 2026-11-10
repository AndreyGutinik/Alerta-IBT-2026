using System.Globalization;
using System.IO;

namespace AlertaIBT.Core.Diagnostics;

/// <summary>What the marker file said about the run before this one.</summary>
/// <param name="Existed">Whether a marker was found (false on the first run after an installation).</param>
/// <param name="EndedCleanly">Whether that run wrote its end (a normal exit, an update restart, Windows ending the session).</param>
/// <param name="StartedAt">When it started, when known.</param>
/// <param name="Version">The version that ran, when known.</param>
/// <param name="EndReason">Why it ended, when it ended cleanly.</param>
public sealed record PreviousRun(bool Existed, bool EndedCleanly, DateTimeOffset? StartedAt, string? Version, string? EndReason)
{
    /// <summary>The last heartbeat the run wrote (every 30 s while it lived): the last sign of life.</summary>
    public DateTimeOffset? LastHeartbeat { get; init; }

    /// <summary>The state in its last line: running, suspending, session-ending, ended or crash.</summary>
    public string? State { get; init; }

    /// <summary>When the PC last started to suspend while this run lived.</summary>
    public DateTimeOffset? SuspendedAt { get; init; }

    /// <summary>When the PC last came back from suspension while this run lived.</summary>
    public DateTimeOffset? ResumedAt { get; init; }

    /// <summary>When Windows asked the app to end (shutdown, restart, logoff).</summary>
    public DateTimeOffset? SessionEndingAt { get; init; }

    /// <summary>The type name of the unhandled exception that killed the run (never its message), when there was one.</summary>
    public string? CrashType { get; init; }

    /// <summary>When that exception happened.</summary>
    public DateTimeOffset? CrashedAt { get; init; }

    /// <summary>The previous run left no end: a crash, a kill from the Task Manager, a power cut.</summary>
    public bool EndedAbnormally => Existed && !EndedCleanly;

    /// <summary>The last time the run is known to have been alive: the last heartbeat, else when it started.</summary>
    public DateTimeOffset? LastSignOfLife => LastHeartbeat ?? SessionEndingAt ?? StartedAt;
}

/// <summary>
/// A tiny file (<c>session.txt</c> in the data folder) that tells the NEXT start how this run ended. It holds: when the run started, a heartbeat every 30 s
/// (so the last sign of life is known to the half minute), the power events it saw (<c>suspending</c> / <c>resumed</c>), that Windows asked it to end (shutdown or
/// restart: written at once, because Windows can kill the process before <c>OnExit</c> runs, which is exactly what made three normal shutdowns look like crashes), a
/// clean end with its reason, and a crash record (the type of the exception, never its text). The whole file is rewritten atomically each time (a temporary file
/// and a replace), so it is never half written. It never throws: a marker that cannot be written only means the next start learns less.
/// </summary>
public sealed class SessionMarker : IDisposable
{
    /// <summary>How often the heartbeat is written.</summary>
    public static readonly TimeSpan HeartbeatEvery = TimeSpan.FromSeconds(30);

    // A shutdown that was asked for and then cancelled (another program refused): after this long without the process dying, the app is running again.
    private static readonly TimeSpan EndingForgottenAfter = TimeSpan.FromSeconds(20);

    private readonly string _file;
    private readonly TimeProvider _clock;
    private readonly object _gate = new();
    private ITimer? _timer;
    private DateTimeOffset _started;
    private string _version = string.Empty;
    private DateTimeOffset _heartbeat;
    private string _state = "running";
    private DateTimeOffset? _suspended;
    private DateTimeOffset? _resumed;
    private DateTimeOffset? _sessionEnding;
    private DateTimeOffset? _ended;
    private string? _reason;
    private string? _crash;
    private DateTimeOffset? _crashedAt;
    private bool _begun;
    private bool _terminal;

    /// <param name="file">The marker file.</param>
    /// <param name="clock">Stamps the file; a fake in tests.</param>
    public SessionMarker(string file, TimeProvider? clock = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(file);

        _file = file;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>Reads what the previous run left and starts the marker of this one.</summary>
    /// <param name="version">The running version, written for the next start's log line.</param>
    public PreviousRun Begin(string version)
    {
        var previous = ReadPrevious();
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            _started = now;
            _heartbeat = now;
            _version = version;
            _state = "running";
            _begun = true;
            _terminal = false;
            Write();
        }

        return previous;
    }

    /// <summary>Starts the heartbeat (a line every <see cref="HeartbeatEvery"/>).</summary>
    public void StartHeartbeat()
    {
        lock (_gate)
        {
            _timer ??= _clock.CreateTimer(_ => Beat(), null, HeartbeatEvery, HeartbeatEvery);
        }
    }

    /// <summary>Writes one heartbeat now (the timer calls it; a test calls it by advancing the fake clock).</summary>
    public void Beat()
    {
        lock (_gate)
        {
            if (!_begun || _terminal)
            {
                return;
            }

            var now = _clock.GetUtcNow();
            _heartbeat = now;
            if (_state == "session-ending" && _sessionEnding is { } at && now - at > EndingForgottenAfter)
            {
                _state = "running"; // the shutdown was cancelled: the app lives on
                _sessionEnding = null;
            }

            Write();
        }
    }

    /// <summary>The PC is about to suspend.</summary>
    public void Suspending() => Update(now =>
    {
        _suspended = now;
        _state = "suspending";
    });

    /// <summary>The PC came back from suspension.</summary>
    public void Resumed() => Update(now =>
    {
        _resumed = now;
        if (_state == "suspending")
        {
            _state = "running";
        }
    });

    /// <summary>Windows asked the app to end (shutdown, restart, logoff); written at once, because the process may be killed before <c>OnExit</c> runs.</summary>
    public void SessionEnding(string reason) => Update(now =>
    {
        _sessionEnding = now;
        _reason = reason;
        _state = "session-ending";
    });

    /// <summary>Records that this run is ending normally, with the <paramref name="reason"/> (a short text).</summary>
    public void End(string reason)
    {
        lock (_gate)
        {
            if (!_begun || _crash is not null)
            {
                return;
            }

            _ended = _clock.GetUtcNow();
            _reason = reason;
            _state = "ended";
            _terminal = true;
            Write();
        }

        StopTimer();
    }

    /// <summary>An unhandled exception is killing the process: records its TYPE (never its message) and goes terminal.</summary>
    public void Crash(string exceptionType)
    {
        lock (_gate)
        {
            if (!_begun)
            {
                return;
            }

            _crash = string.IsNullOrWhiteSpace(exceptionType) ? "(desconhecida)" : exceptionType;
            _crashedAt = _clock.GetUtcNow();
            _state = "crash";
            _terminal = true;
            Write();
        }

        StopTimer();
    }

    /// <inheritdoc />
    public void Dispose() => StopTimer();

    private void Update(Action<DateTimeOffset> change)
    {
        lock (_gate)
        {
            if (!_begun || _terminal)
            {
                return;
            }

            var now = _clock.GetUtcNow();
            change(now);
            _heartbeat = now;
            Write();
        }
    }

    private void StopTimer()
    {
        ITimer? timer;
        lock (_gate)
        {
            timer = _timer;
            _timer = null;
        }

        timer?.Dispose();
    }

    // The whole file, atomically: a temporary file next to it, then a replace.
    private void Write()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_file))!);
            var text = new System.Text.StringBuilder();
            Line(text, "started", _started);
            text.Append("version=").Append(_version).Append('\n');
            text.Append("pid=").Append(Environment.ProcessId.ToString(CultureInfo.InvariantCulture)).Append('\n');
            Line(text, "heartbeat", _heartbeat);
            text.Append("state=").Append(_state).Append('\n');
            Line(text, "suspended", _suspended);
            Line(text, "resumed", _resumed);
            Line(text, "endingsession", _sessionEnding);
            Line(text, "ended", _ended);
            if (_reason is not null)
            {
                text.Append("reason=").Append(_reason.Replace('\n', ' ').Replace('\r', ' ')).Append('\n');
            }

            if (_crash is not null)
            {
                text.Append("crash=").Append(_crash).Append('\n');
                Line(text, "crashed", _crashedAt);
            }

            var temporary = _file + ".tmp";
            File.WriteAllText(temporary, text.ToString());
            File.Move(temporary, _file, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The next start will know less; nothing else is lost.
        }
    }

    private static void Line(System.Text.StringBuilder text, string key, DateTimeOffset? value)
    {
        if (value is { } at)
        {
            text.Append(key).Append('=').Append(at.ToString("O", CultureInfo.InvariantCulture)).Append('\n');
        }
    }

    private PreviousRun ReadPrevious()
    {
        try
        {
            if (!File.Exists(_file))
            {
                return new PreviousRun(false, true, null, null, null);
            }

            DateTimeOffset? started = null;
            DateTimeOffset? heartbeat = null;
            DateTimeOffset? suspended = null;
            DateTimeOffset? resumed = null;
            DateTimeOffset? ending = null;
            DateTimeOffset? crashed = null;
            string? version = null;
            string? reason = null;
            string? state = null;
            string? crash = null;
            var ended = false;
            foreach (var line in File.ReadAllLines(_file))
            {
                var split = line.IndexOf('=', StringComparison.Ordinal);
                if (split <= 0)
                {
                    continue;
                }

                var key = line[..split];
                var value = line[(split + 1)..];
                switch (key)
                {
                    case "started":
                        started = Time(value);
                        break;
                    case "heartbeat":
                        heartbeat = Time(value);
                        break;
                    case "suspended":
                        suspended = Time(value);
                        break;
                    case "resumed":
                        resumed = Time(value);
                        break;
                    case "endingsession":
                        ending = Time(value);
                        break;
                    case "crashed":
                        crashed = Time(value);
                        break;
                    case "version":
                        version = value;
                        break;
                    case "state":
                        state = value;
                        break;
                    case "crash":
                        crash = value;
                        break;
                    case "ended":
                        ended = true;
                        break;
                    case "reason":
                        reason = value;
                        break;
                }
            }

            // Windows asking the app to end is an end too: the process may have been killed before it could write the last line.
            var clean = (ended || state == "session-ending") && crash is null;
            return new PreviousRun(true, clean, started, version, reason)
            {
                LastHeartbeat = heartbeat,
                State = state,
                SuspendedAt = suspended,
                ResumedAt = resumed,
                SessionEndingAt = ending,
                CrashType = crash,
                CrashedAt = crashed,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new PreviousRun(false, true, null, null, null);
        }
    }

    private static DateTimeOffset? Time(string value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at) ? at : null;
}
