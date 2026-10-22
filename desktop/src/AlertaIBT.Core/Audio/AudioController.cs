using System.Globalization;
using AlertaIBT.Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AlertaIBT.Core.Audio;

/// <summary>What the "Som" tab shows: the outputs, the lock, and what was applied or is still waiting for a program to open.</summary>
/// <param name="Devices">The active output devices.</param>
/// <param name="CurrentOutputName">The short name of the current default output, or <c>null</c> when there is none.</param>
/// <param name="SelectedName">The short name of the chosen output, or <c>null</c> when none was found yet.</param>
/// <param name="SelectedPresent">Whether the chosen output is active now.</param>
/// <param name="LockOn">Whether the lock is on.</param>
/// <param name="LastAppliedAt">When volumes were last set (local time), or <c>null</c> when none were.</param>
/// <param name="AppliedSummary">What was set, such as <c>Volume geral 80%, Spotify 100%</c>; empty when nothing was.</param>
/// <param name="Waiting">The programs whose session has not appeared yet, such as <c>Spotify: aguardando abrir</c>.</param>
public sealed record AudioStatus(
    IReadOnlyList<AudioDevice> Devices,
    string? CurrentOutputName,
    string? SelectedName,
    bool SelectedPresent,
    bool LockOn,
    DateTimeOffset? LastAppliedAt,
    string AppliedSummary,
    IReadOnlyList<string> Waiting)
{
    /// <summary>No information yet (before the first look at the outputs).</summary>
    public static AudioStatus Empty { get; } = new([], null, null, false, false, null, string.Empty, []);

    /// <summary>The line under the output card: <c>Saida atual: WING USB</c>, or that the chosen output is not connected and the app waits.</summary>
    public string OutputLine
    {
        get
        {
            if (LockOn && SelectedName is not null && !SelectedPresent)
            {
                return $"{SelectedName} desconectado: aguardando";
            }

            return CurrentOutputName is null ? "Sa\U000000EDda atual: nenhuma" : $"Sa\U000000EDda atual: {CurrentOutputName}";
        }
    }

    /// <summary>The line under "Aplicar agora": when and what was set, then what waits for a program to open.</summary>
    public string AppliedLine
    {
        get
        {
            var parts = new List<string>();
            if (LastAppliedAt is { } at)
            {
                parts.Add($"Aplicado \U000000E0s {at.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture)}: {AppliedSummary}");
            }
            else
            {
                parts.Add("Ainda nada aplicado nesta abertura do app.");
            }

            parts.AddRange(Waiting);
            return string.Join(". ", parts);
        }
    }
}

/// <summary>
/// The rules of the "Som" tab, against <see cref="IAudioSystem"/>: when the volumes are set (once at start after a short delay, when the chosen
/// output appears again, when a program's session appears in its first 30 minutes, and by "Aplicar agora"), and the one thing that is enforced
/// all the time: the Windows default output stays on the chosen device. It never raises a volume again after setting it, so the owner can
/// still move the sliders by hand, and it never forces a device that is not there.
/// </summary>
/// <remarks>
/// Everything runs from timers (never on the caller's thread, never on the UI thread), serialized by one lock; the events of
/// <see cref="IAudioSystem"/> only start a 500 ms debounce and the work is a full, idempotent "look at everything" pass. Log lines hold
/// only device names, process names and percentages.
/// </remarks>
public sealed class AudioController : IDisposable
{
    /// <summary>How long after <see cref="Start"/> the volumes are first set (6 s: the audio devices of a booting PC are still arriving).</summary>
    public static readonly TimeSpan StartDelay = TimeSpan.FromSeconds(6);

    /// <summary>How long events are gathered before one look at the audio (500 ms).</summary>
    public static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(500);

    /// <summary>How long after <see cref="Start"/> a program's new session still gets its volume (30 minutes).</summary>
    public static readonly TimeSpan SessionWindow = TimeSpan.FromMinutes(30);

    /// <summary>The most corrections of the default output in <see cref="CorrectionWindow"/> (3): more means something keeps undoing them.</summary>
    public const int MaxCorrections = 3;

    /// <summary>The span in which <see cref="MaxCorrections"/> counts (1 minute).</summary>
    public static readonly TimeSpan CorrectionWindow = TimeSpan.FromMinutes(1);

    private const string MasterName = "Volume geral";
    private const string SystemSoundsName = "Sons do Windows";

    private readonly IAudioSystem _system;
    private readonly SettingsService _settings;
    private readonly TimeProvider _clock;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private readonly Queue<DateTimeOffset> _corrections = new();
    private readonly HashSet<string> _appliedSessions = [];
    private readonly HashSet<string> _openedApps = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _applied = new(StringComparer.OrdinalIgnoreCase);
    private ITimer? _debounceTimer;
    private ITimer? _startTimer;
    private DateTimeOffset _startedAt;
    private DateTimeOffset? _lastAppliedAt;
    private bool _started;
    private bool _disposed;
    private bool _startDue;
    private bool _masterPending = true;
    private bool _gaveUp;
    private string? _watchedId;
    private string _lastSeen = string.Empty;
    private IReadOnlyList<AudioDevice> _devices = [];
    private DefaultDevices? _defaults;
    private AudioDevice? _chosen;
    private bool _wasAbsent = true;
    private AudioStatus _status = AudioStatus.Empty;

    /// <param name="system">The Windows audio.</param>
    /// <param name="settings">The settings (the chosen output is saved there when it is found).</param>
    /// <param name="clock">Drives the delays and dates what happened.</param>
    /// <param name="logger">Receives what happens (device names, process names and percentages only).</param>
    public AudioController(IAudioSystem system, SettingsService settings, TimeProvider? clock = null, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(settings);

        _system = system;
        _settings = settings;
        _clock = clock ?? TimeProvider.System;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>Raised when <see cref="Status"/> changed. On a timer thread: the listener posts to the UI thread.</summary>
    public event Action<AudioStatus>? StatusChanged;

    /// <summary>What the tab shows now.</summary>
    public AudioStatus Status
    {
        get
        {
            lock (_gate)
            {
                return _status;
            }
        }
    }

    /// <summary>
    /// Starts listening and schedules the first look and the first volume setting. Nothing is read or written before this, and it returns at once
    /// (the work runs on timers). Call once.
    /// </summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_started || _disposed)
            {
                return;
            }

            _started = true;
            _startedAt = _clock.GetUtcNow();
            _debounceTimer = _clock.CreateTimer(_ => Reconcile(applyNow: false), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _startTimer = _clock.CreateTimer(_ => OnStartDue(), null, StartDelay, Timeout.InfiniteTimeSpan);
            _system.DevicesChanged += Kick;
            _system.DefaultDeviceChanged += Kick;
            _system.SessionCreated += Kick;
            _settings.Changed += OnSettingsChanged;
            _debounceTimer.Change(Debounce, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>"Aplicar agora": sets everything now, on the chosen output and on the sessions that exist (even if the switch is off or the 30 minutes passed). Never throws.</summary>
    public void ApplyNow() => Reconcile(applyNow: true);

    /// <summary>"Aplicar agora" without holding up the caller (the screen).</summary>
    public Task ApplyNowAsync() => Task.Run(ApplyNow);

    /// <summary>The tray item: turns the lock on or off and saves it.</summary>
    public void ToggleLock() => _settings.Update(s => s with { Audio = s.Audio with { LockOutput = !s.Audio.LockOutput } });

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
            if (_started)
            {
                _settings.Changed -= OnSettingsChanged;
                _system.DevicesChanged -= Kick;
                _system.DefaultDeviceChanged -= Kick;
                _system.SessionCreated -= Kick;
            }

            _debounceTimer?.Dispose();
            _startTimer?.Dispose();
        }

        try
        {
            _system.WatchSessions(null);
            _system.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Som: falha ao fechar o audio ({Error})", ex.GetType().Name);
        }
    }

    // Any change of the audio world or of a setting: one look, after the events settle.
    private void Kick()
    {
        lock (_gate)
        {
            if (_started && !_disposed)
            {
                _debounceTimer?.Change(Debounce, Timeout.InfiniteTimeSpan);
            }
        }
    }

    private void OnSettingsChanged(AppSettings settings) => Kick();

    private void OnStartDue()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _startDue = true;
        }

        Reconcile(applyNow: false);
    }

    private void Reconcile(bool applyNow)
    {
        AudioStatus? changed = null;
        lock (_gate)
        {
            if (!_started || _disposed)
            {
                return;
            }

            try
            {
                Pass(applyNow);
            }
            catch (Exception ex)
            {
                // A missing or broken audio service must never reach the UI; the status line says what is known.
                _logger.LogWarning("Som: o audio do Windows nao respondeu ({Error})", ex.GetType().Name);
            }

            var status = BuildStatus();
            if (!EqualStatus(status, _status))
            {
                _status = status;
                changed = status;
            }
        }

        if (changed is not null)
        {
            try
            {
                StatusChanged?.Invoke(changed);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Som: um ouvinte do estado falhou ({Error})", ex.GetType().Name);
            }
        }
    }

    // One look at everything. Idempotent: it only writes when a rule says so.
    private void Pass(bool applyNow)
    {
        var audio = _settings.Current.Audio;
        var devices = _system.ActiveRenderDevices();
        _devices = devices;
        var defaults = _system.GetDefaultDevices();
        _defaults = defaults;

        var chosen = ResolveChosen(audio, devices);
        _chosen = chosen;
        string? target;
        if (audio.LockOutput)
        {
            if (chosen is null)
            {
                _masterPending = true; // when it comes back, its volume is set again
                target = null;
            }
            else
            {
                if (!defaults.AllEqual(chosen.Id))
                {
                    Correct(chosen, reappeared: _wasAbsent);
                    defaults = _system.GetDefaultDevices();
                    _defaults = defaults;
                }
                else
                {
                    _gaveUp = false;
                }

                target = chosen.Id;
            }

            _wasAbsent = chosen is null;
        }
        else
        {
            _wasAbsent = false;
            target = defaults.Console;
        }

        // New sessions are only watched while the adjustment is on.
        UpdateWatch(audio.Enabled ? target : null);
        var apply = applyNow || (audio.Enabled && _startDue);
        if (target is null)
        {
            if (applyNow)
            {
                _logger.LogWarning("Som: Aplicar agora sem saida de audio disponivel");
            }

            return;
        }

        if (!apply)
        {
            return;
        }

        if (applyNow || _masterPending)
        {
            if (_system.SetMasterVolume(target, audio.MasterVolume, audio.MasterMuted))
            {
                _masterPending = false;
                Record(MasterName, audio.MasterVolume, audio.MasterMuted);
                _logger.LogInformation("Som: {Name} {Value}", MasterName, Describe(audio.MasterVolume, audio.MasterMuted));
            }
            else
            {
                _logger.LogWarning("Som: nao foi possivel ajustar o {Name}", MasterName);
            }
        }

        ApplySessions(audio, target, applyNow);
    }

    private void ApplySessions(AudioSettings audio, string target, bool force)
    {
        var sessions = _system.GetSessions(target);
        var inWindow = _clock.GetUtcNow() - _startedAt <= SessionWindow;
        var seen = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var session in sessions)
        {
            if (session.ProcessName is { Length: > 0 } process)
            {
                seen.Add(process);
            }

            var key = target + "|" + session.Key;
            if (session.IsSystemSounds)
            {
                if (force || _appliedSessions.Add(key))
                {
                    _appliedSessions.Add(key);
                    SetSession(target, session, SystemSoundsName, audio.SystemSoundsVolume, audio.SystemSoundsMuted);
                }

                continue;
            }

            var entry = audio.Apps.FirstOrDefault(app => AudioNames.MatchesProcess(app.ProcessNames, session.ProcessName));
            if (entry is null || (!force && (!inWindow || _appliedSessions.Contains(key))))
            {
                continue;
            }

            _appliedSessions.Add(key);
            if (SetSession(target, session, entry.Name, entry.Volume, entry.Muted))
            {
                _openedApps.Add(entry.Name);
            }
        }

        var names = string.Join(", ", seen);
        if (names.Length > 0 && names != _lastSeen)
        {
            _logger.LogInformation("Som: programas com audio agora: {Processes}", names);
        }

        _lastSeen = names;
    }

    private bool SetSession(string target, AudioSessionInfo session, string name, int volume, bool muted)
    {
        if (!_system.SetSessionVolume(target, session.Key, volume, muted))
        {
            _logger.LogWarning("Som: nao foi possivel ajustar {Name}", name);
            return false;
        }

        Record(name, volume, muted);
        _logger.LogInformation("Som: {Name} {Value}", name, Describe(volume, muted));
        return true;
    }

    // The chosen output: by id, then by name; with none chosen yet, the first one whose name has "WING" (saved, so the choice is kept).
    private AudioDevice? ResolveChosen(AudioSettings audio, IReadOnlyList<AudioDevice> devices)
    {
        if (string.IsNullOrWhiteSpace(audio.OutputDeviceId) && string.IsNullOrWhiteSpace(audio.OutputDeviceName))
        {
            var wing = AudioNames.FindContaining(devices, AudioSettings.DefaultOutputMatch);
            if (wing is not null)
            {
                Remember(wing);
                _logger.LogInformation("Som: saida escolhida: {Device}", wing.ShortName);
            }

            return wing;
        }

        var found = AudioNames.Resolve(devices, audio.OutputDeviceId, audio.OutputDeviceName);
        if (found is not null && (!string.Equals(found.Id, audio.OutputDeviceId, StringComparison.OrdinalIgnoreCase) || found.Name != audio.OutputDeviceName))
        {
            Remember(found); // the same device under another id or name (another USB port): learn it
        }

        return found;
    }

    private void Remember(AudioDevice device) =>
        _settings.Update(s => s with { Audio = s.Audio with { OutputDeviceId = device.Id, OutputDeviceName = device.Name } });

    // Puts the default output back on the chosen device, at most MaxCorrections times per minute.
    private void Correct(AudioDevice chosen, bool reappeared)
    {
        var now = _clock.GetUtcNow();
        while (_corrections.Count > 0 && now - _corrections.Peek() >= CorrectionWindow)
        {
            _corrections.Dequeue();
        }

        if (!reappeared && _corrections.Count >= MaxCorrections)
        {
            if (!_gaveUp)
            {
                _gaveUp = true;
                _logger.LogWarning("Saida de audio: o Windows continua trocando; parei de corrigir por 1 minuto ({Device})", chosen.ShortName);
            }

            return;
        }

        if (_system.SetDefaultRenderDevice(chosen.Id))
        {
            if (!reappeared)
            {
                _corrections.Enqueue(now);
            }

            _gaveUp = false;
            _logger.LogInformation(
                reappeared ? "Saida de audio: {Device} voltou, selecionada" : "Saida de audio: voltou para {Device}",
                chosen.ShortName);
        }
        else
        {
            _corrections.Enqueue(now);
            _logger.LogWarning("Saida de audio: nao foi possivel voltar para {Device}", chosen.ShortName);
        }
    }

    private void UpdateWatch(string? deviceId)
    {
        if (string.Equals(deviceId, _watchedId, StringComparison.Ordinal))
        {
            return;
        }

        _watchedId = deviceId;
        _system.WatchSessions(deviceId);
    }

    private void Record(string name, int volume, bool muted)
    {
        _applied[name] = muted ? $"{name} {Describe(volume, muted)}" : $"{name} {volume}%";
        _lastAppliedAt = _clock.GetUtcNow();
    }

    private static string Describe(int volume, bool muted) => muted ? $"{volume}% (mudo)" : $"{volume}%";

    private AudioStatus BuildStatus()
    {
        var audio = _settings.Current.Audio;
        var current = _defaults?.Console is { } id ? _devices.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase)) : null;
        var selectedName = _chosen?.ShortName ?? (string.IsNullOrWhiteSpace(audio.OutputDeviceName) ? null : AudioNames.Short(audio.OutputDeviceName));
        var inWindow = _clock.GetUtcNow() - _startedAt <= SessionWindow;
        var waiting = audio.Enabled && inWindow
            ? audio.Apps.Where(app => !_openedApps.Contains(app.Name)).Select(app => $"{app.Name}: aguardando abrir").ToList()
            : [];
        var order = new[] { MasterName }.Concat(audio.Apps.Select(a => a.Name)).Append(SystemSoundsName).ToList();
        var summary = string.Join(", ", order.Where(_applied.ContainsKey).Select(n => _applied[n]));
        return new AudioStatus(
            _devices,
            current?.ShortName,
            selectedName,
            _chosen is not null,
            audio.LockOutput,
            _lastAppliedAt,
            summary,
            waiting);
    }

    private static bool EqualStatus(AudioStatus a, AudioStatus b) =>
        a.CurrentOutputName == b.CurrentOutputName
        && a.SelectedName == b.SelectedName
        && a.SelectedPresent == b.SelectedPresent
        && a.LockOn == b.LockOn
        && a.LastAppliedAt == b.LastAppliedAt
        && a.AppliedSummary == b.AppliedSummary
        && a.Waiting.SequenceEqual(b.Waiting)
        && a.Devices.SequenceEqual(b.Devices);
}
