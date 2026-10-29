using AlertaIBT.Core.Settings;
using Microsoft.Extensions.Logging;

namespace AlertaIBT.Core.Audio;

// The live volumes: a slider, a number or a mute icon of the "Volumes" card reaches the real audio at once (the saved copy is debounced elsewhere),
// optionally ramped over AudioSettings.VolumeRampSeconds. It never waits for the 500 ms look of the controller and never enumerates sessions per
// change: the sessions are cached until Windows says one appeared or went.
public sealed partial class AudioController
{
    /// <summary>The key of the Windows master volume in <see cref="SetLive"/>.</summary>
    public const string LiveMaster = "master";

    /// <summary>The key of "Sons do Windows" in <see cref="SetLive"/>.</summary>
    public const string LiveSystemSounds = "system";

    /// <summary>How often a ramp moves the volume (25 ms).</summary>
    public static readonly TimeSpan RampStep = TimeSpan.FromMilliseconds(25);

    private readonly object _liveGate = new();
    private readonly Action<Action> _livePost;
    private readonly Dictionary<string, (int Volume, bool Muted)> _liveWishes = [];
    private readonly Dictionary<string, LiveState> _live = new(StringComparer.Ordinal);
    private bool _liveScheduled;
    private ITimer? _rampTimer;
    private List<(string Device, AudioSessionInfo Session)>? _liveSessions;
    private volatile string? _liveTargetId;
    private volatile string? _liveHolyricsId;

    /// <summary>The key of a program (its name in the settings, such as <c>Holyrics</c>) in <see cref="SetLive"/>.</summary>
    public static string LiveApp(string name) => "app:" + name;

    /// <summary>The smooth curve of a ramp: 0 to 1 over <paramref name="progress"/> 0 to 1 (smoothstep: gentle start, gentle end).</summary>
    public static double RampCurve(double progress)
    {
        var t = Math.Clamp(progress, 0, 1);
        return t * t * (3 - (2 * t));
    }

    /// <summary>
    /// Sets one volume now: <paramref name="key"/> is <see cref="LiveMaster"/>, <see cref="LiveSystemSounds"/> or <see cref="LiveApp"/>. Returns at once (the
    /// work runs on another thread); with a ramp set the volume moves to the target over <see cref="AudioSettings.VolumeRampSeconds"/>, and a newer
    /// change retargets a ramp that is running. Never throws.
    /// </summary>
    /// <param name="key">What to change.</param>
    /// <param name="volume">0 to 100.</param>
    /// <param name="muted">Whether it is muted.</param>
    public void SetLive(string key, int volume, bool muted)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        lock (_liveGate)
        {
            _liveWishes[key] = (Math.Clamp(volume, AudioSettings.MinVolume, AudioSettings.MaxVolume), muted);
            if (_liveScheduled)
            {
                return;
            }

            _liveScheduled = true;
        }

        _livePost(DrainLive);
    }

    // Takes the latest wish of every key and acts on it.
    private void DrainLive()
    {
        while (true)
        {
            KeyValuePair<string, (int Volume, bool Muted)>[] wishes;
            lock (_liveGate)
            {
                if (_liveWishes.Count == 0)
                {
                    _liveScheduled = false;
                    return;
                }

                wishes = [.. _liveWishes];
                _liveWishes.Clear();
            }

            foreach (var wish in wishes)
            {
                try
                {
                    lock (_liveGate)
                    {
                        Retarget(wish.Key, wish.Value.Volume, wish.Value.Muted);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Som: o volume ao vivo falhou ({Error})", ex.GetType().Name);
                }
            }
        }
    }

    // Caller holds _liveGate.
    private void Retarget(string key, int volume, bool muted)
    {
        var state = StateOf(key);
        var seconds = _settings.Current.Audio.VolumeRampSeconds;
        var wanted = muted ? 0 : volume;
        if (!(seconds > 0))
        {
            state.Ramp = null;
            Apply(key, volume, muted);
            state.Level = wanted;
            state.Muted = muted;
            return;
        }

        if (state.Ramp is null && Math.Abs(state.Level - wanted) < 0.5 && state.Muted == muted)
        {
            return; // already there
        }

        if (state.Muted && !muted)
        {
            Apply(key, 0, false); // unmute at silence, then rise
            state.Muted = false;
            state.Level = 0;
        }

        state.Ramp = new RampPlan(state.Level, wanted, volume, muted, _clock.GetTimestamp(), seconds);
        _rampTimer ??= _clock.CreateTimer(_ => RampTick(), null, RampStep, RampStep);
        _rampTimer.Change(RampStep, RampStep);
    }

    private void RampTick()
    {
        lock (_liveGate)
        {
            var running = false;
            foreach (var (key, state) in _live)
            {
                if (state.Ramp is not { } ramp)
                {
                    continue;
                }

                try
                {
                    var progress = _clock.GetElapsedTime(ramp.Start).TotalSeconds / ramp.Seconds;
                    if (progress >= 1)
                    {
                        state.Ramp = null;
                        if (ramp.FinalMuted)
                        {
                            Apply(key, 0, true);
                            state.Level = 0;
                            state.Muted = true;
                        }
                        else
                        {
                            Apply(key, ramp.FinalVolume, false);
                            state.Level = ramp.FinalVolume;
                            state.Muted = false;
                        }

                        continue;
                    }

                    state.Level = ramp.From + ((ramp.To - ramp.From) * RampCurve(progress));
                    Apply(key, (int)Math.Round(state.Level), false);
                    running = true;
                }
                catch (Exception ex)
                {
                    state.Ramp = null;
                    _logger.LogWarning("Som: a rampa de volume falhou ({Error})", ex.GetType().Name);
                }
            }

            if (!running)
            {
                _rampTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            }
        }
    }

    // Caller holds _liveGate. The state of a key; the first time it starts from what the settings say (the slider has not been saved yet).
    private LiveState StateOf(string key)
    {
        if (!_live.TryGetValue(key, out var state))
        {
            _live[key] = state = new LiveState();
        }

        if (!state.Known)
        {
            state.Known = true;
            var audio = _settings.Current.Audio;
            var (volume, muted) = key switch
            {
                LiveMaster => (audio.MasterVolume, audio.MasterMuted),
                LiveSystemSounds => (audio.SystemSoundsVolume, audio.SystemSoundsMuted),
                _ => audio.Apps.FirstOrDefault(a => string.Equals(LiveApp(a.Name), key, StringComparison.Ordinal)) is { } app ? (app.Volume, app.Muted) : (100, false),
            };
            state.Level = muted ? 0 : volume;
            state.Muted = muted;
        }

        return state;
    }

    // Caller holds _liveGate. Writes to the real audio: the master of the output in use, or every session of the program.
    private void Apply(string key, int volume, bool muted)
    {
        if (key == LiveMaster)
        {
            var device = _liveTargetId ?? _system.GetDefaultDevices().Console;
            if (device is not null && !_system.SetMasterVolume(device, volume, muted))
            {
                _logger.LogWarning("Som: nao foi possivel ajustar o {Name}", MasterName);
            }

            return;
        }

        var failed = false;
        foreach (var (device, session) in MatchingSessions(key))
        {
            failed |= !_system.SetSessionVolume(device, session.Key, volume, muted);
        }

        if (failed)
        {
            // A session went away since the list was made: one fresh look, once.
            _liveSessions = null;
            foreach (var (device, session) in MatchingSessions(key))
            {
                _system.SetSessionVolume(device, session.Key, volume, muted);
            }
        }
    }

    private IEnumerable<(string Device, AudioSessionInfo Session)> MatchingSessions(string key)
    {
        var sessions = _liveSessions ??= ListSessions();
        if (key == LiveSystemSounds)
        {
            return sessions.Where(s => s.Session.IsSystemSounds);
        }

        var entry = _settings.Current.Audio.Apps.FirstOrDefault(a => string.Equals(LiveApp(a.Name), key, StringComparison.Ordinal));
        return entry is null
            ? []
            : sessions.Where(s => !s.Session.IsSystemSounds && AudioNames.MatchesProcess(entry.ProcessNames, s.Session.ProcessName));
    }

    private List<(string Device, AudioSessionInfo Session)> ListSessions()
    {
        var devices = new List<string>();
        if ((_liveTargetId ?? _system.GetDefaultDevices().Console) is { } target)
        {
            devices.Add(target);
        }

        if (_liveHolyricsId is { } holyrics && !devices.Contains(holyrics, StringComparer.OrdinalIgnoreCase))
        {
            devices.Add(holyrics);
        }

        var list = new List<(string Device, AudioSessionInfo Session)>();
        foreach (var device in devices)
        {
            foreach (var session in _system.GetSessions(device))
            {
                list.Add((device, session));
            }
        }

        return list;
    }

    private void InvalidateLiveSessions()
    {
        lock (_liveGate)
        {
            _liveSessions = null;
        }
    }

    private sealed class LiveState
    {
        public double Level { get; set; }

        public bool Muted { get; set; }

        public bool Known { get; set; }

        public RampPlan? Ramp { get; set; }
    }

    private sealed class RampPlan(double from, double to, int finalVolume, bool finalMuted, long start, double seconds)
    {
        public double From { get; } = from;

        public double To { get; } = to;

        public int FinalVolume { get; } = finalVolume;

        public bool FinalMuted { get; } = finalMuted;

        public long Start { get; } = start;

        public double Seconds { get; } = seconds;
    }
}
