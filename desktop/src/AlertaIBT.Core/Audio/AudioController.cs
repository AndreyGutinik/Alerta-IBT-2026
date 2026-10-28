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
/// <param name="Known">Whether the outputs were looked at already (false only for <see cref="Empty"/>).</param>
/// <param name="HolyricsText">What the Holyrics output is doing (<c>Holyrics toca em WING-USB (Out 1-2)</c>, or that the device is not there and the program is left alone); <c>null</c> when the switch is off.</param>
public sealed record AudioStatus(
    IReadOnlyList<AudioDevice> Devices,
    string? CurrentOutputName,
    string? SelectedName,
    bool SelectedPresent,
    bool LockOn,
    DateTimeOffset? LastAppliedAt,
    string AppliedSummary,
    IReadOnlyList<string> Waiting,
    bool Known = true,
    string? HolyricsText = null)
{
    /// <summary>No information yet (before the first look at the outputs).</summary>
    public static AudioStatus Empty { get; } = new([], null, null, false, false, null, string.Empty, [], Known: false);

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
                parts.Add("Ainda nada aplicado nesta abertura do app");
            }

            parts.AddRange(Waiting);
            return string.Join(". ", parts) + ".";
        }
    }
}

/// <summary>
/// The rules of the "Som" tab, against <see cref="IAudioSystem"/>: when the volumes are set (once at start after a short delay, when the chosen
/// output appears again, every time a program's audio session appears, and by "Aplicar agora"), the one thing that is enforced all the time
/// (the Windows default output stays on the chosen device), and the Windows per-app output of Holyrics (set when a Holyrics process makes sound
/// and by "Aplicar agora"; never touched with its switch off). It never raises a volume again after setting it, so the owner can still move
/// the sliders by hand, and it never forces a device that is not there.
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

    /// <summary>The most corrections of the default output in <see cref="CorrectionWindow"/> (3): more means something keeps undoing them.</summary>
    public const int MaxCorrections = 3;

    /// <summary>The span in which <see cref="MaxCorrections"/> counts (1 minute).</summary>
    public static readonly TimeSpan CorrectionWindow = TimeSpan.FromMinutes(1);

    private const string MasterName = "Volume geral";
    private const string SystemSoundsName = "Sons do Windows";
    private const string HolyricsName = "Holyrics";

    private readonly IAudioSystem _system;
    private readonly SettingsService _settings;
    private readonly IAppOutputPolicy? _appOutput;
    private readonly TimeProvider _clock;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private readonly Queue<DateTimeOffset> _corrections = new();
    private readonly HashSet<string> _appliedSessions = [];
    private readonly HashSet<string> _openedApps = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _applied = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _routed = [];
    private readonly HashSet<string> _routeFailed = [];
    private ITimer? _debounceTimer;
    private ITimer? _startTimer;
    private DateTimeOffset? _lastAppliedAt;
    private bool _started;
    private bool _disposed;
    private bool _startDue;
    private bool _masterPending = true;
    private bool _gaveUp;
    private string _watchedKey = string.Empty;
    private AudioDevice? _holyricsDevice;
    private int _holyricsRunning;
    private bool _holyricsReportedAbsent;
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
    /// <param name="appOutput">The Windows per-app output (for Holyrics); without it the Holyrics routing does nothing.</param>
    public AudioController(IAudioSystem system, SettingsService settings, TimeProvider? clock = null, ILogger? logger = null, IAppOutputPolicy? appOutput = null)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(settings);

        _system = system;
        _settings = settings;
        _appOutput = appOutput;
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
            _debounceTimer = _clock.CreateTimer(_ => Reconcile(applyNow: false), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _startTimer = _clock.CreateTimer(_ => OnStartDue(), null, StartDelay, Timeout.InfiniteTimeSpan);
            _system.DevicesChanged += Kick;
            _system.DefaultDeviceChanged += Kick;
            _system.SessionCreated += Kick;
            _settings.Changed += OnSettingsChanged;
            _debounceTimer.Change(Debounce, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>"Aplicar agora": sets everything now (volumes, and the Holyrics output when its switch is on), on the chosen output and on the sessions that exist, even if "Ajustar o som ao iniciar" is off. Never throws.</summary>
    public void ApplyNow() => Reconcile(applyNow: true);

    /// <summary>"Aplicar agora" without holding up the caller (the screen).</summary>
    public Task ApplyNowAsync() => Task.Run(ApplyNow);

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
            _system.WatchSessions([]);
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

        // The output of Holyrics, then the sessions worth watching: new ones only matter while a switch that reacts to them is on.
        var holyrics = ResolveHolyrics(audio, devices);
        var watch = new List<string>();
        if (target is not null && (audio.Enabled || audio.HolyricsRoute))
        {
            watch.Add(target);
        }

        if (holyrics is not null && !watch.Contains(holyrics.Id, StringComparer.OrdinalIgnoreCase))
        {
            watch.Add(holyrics.Id);
        }

        UpdateWatch(watch);
        if (audio.HolyricsRoute)
        {
            RouteHolyrics(audio, devices, holyrics, applyNow);
        }

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

        // The output in use and, when Holyrics plays elsewhere, that one too: the program's volume is set wherever its session appears.
        var places = new List<string> { target };
        if (holyrics is not null && !places.Contains(holyrics.Id, StringComparer.OrdinalIgnoreCase))
        {
            places.Add(holyrics.Id);
        }

        ApplySessions(audio, places, applyNow);
    }

    // Every NEW session (one the program opened since the last look) gets the volume of its program once, however long the app has been
    // running; "Aplicar agora" sets the ones that exist again.
    private void ApplySessions(AudioSettings audio, IReadOnlyList<string> devices, bool force)
    {
        var seen = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var current = new HashSet<string>();
        foreach (var device in devices)
        {
            foreach (var session in _system.GetSessions(device))
            {
                if (session.ProcessName is { Length: > 0 } process)
                {
                    seen.Add(process);
                }

                var key = device + "|" + session.Key;
                current.Add(key);
                if (session.IsSystemSounds)
                {
                    if (force || _appliedSessions.Add(key))
                    {
                        _appliedSessions.Add(key);
                        SetSession(device, session, SystemSoundsName, audio.SystemSoundsVolume, audio.SystemSoundsMuted);
                    }

                    continue;
                }

                var entry = audio.Apps.FirstOrDefault(app => AudioNames.MatchesProcess(app.ProcessNames, session.ProcessName));
                if (entry is null || (!force && _appliedSessions.Contains(key)))
                {
                    continue;
                }

                _appliedSessions.Add(key);
                if (SetSession(device, session, entry.Name, entry.Volume, entry.Muted))
                {
                    _openedApps.Add(entry.Name);
                }
            }
        }

        // A session that is gone is forgotten: the program opening again is a new session and gets its volume again.
        _appliedSessions.RemoveWhere(key => !current.Contains(key));

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
        var found = OutputResolver.Find(devices, audio.OutputDeviceId, new DeviceMatchRule(audio.OutputMatch)).Device;
        if (found is null)
        {
            return null;
        }

        if (!string.Equals(found.Id, audio.OutputDeviceId, StringComparison.OrdinalIgnoreCase) || found.Name != audio.OutputDeviceName)
        {
            // The first time, or the same device under another id or name (another USB port): learn it.
            if (string.IsNullOrWhiteSpace(audio.OutputDeviceId) && string.IsNullOrWhiteSpace(audio.OutputDeviceName))
            {
                _logger.LogInformation("Som: saida escolhida: {Device}", found.ShortName);
            }

            Remember(found);
        }

        return found;
    }

    // The Holyrics output: by id, then by the name rule, else none (and the program is left alone). Learns the id and name of what it finds.
    private AudioDevice? ResolveHolyrics(AudioSettings audio, IReadOnlyList<AudioDevice> devices)
    {
        if (!audio.HolyricsRoute)
        {
            _holyricsDevice = null;
            return null;
        }

        var found = OutputResolver.Find(devices, audio.HolyricsDeviceId, new DeviceMatchRule(audio.HolyricsMatch)).Device;
        _holyricsDevice = found;
        if (found is not null
            && (!string.Equals(found.Id, audio.HolyricsDeviceId, StringComparison.OrdinalIgnoreCase) || found.Name != audio.HolyricsDeviceName))
        {
            _settings.Update(s => s with { Audio = s.Audio with { HolyricsDeviceId = found.Id, HolyricsDeviceName = found.Name } });
        }

        return found;
    }

    // Sets the Windows per-app output of every Holyrics process that makes sound, once per process and output ("Aplicar agora" does it again).
    private void RouteHolyrics(AudioSettings audio, IReadOnlyList<AudioDevice> devices, AudioDevice? holyrics, bool force)
    {
        if (holyrics is null)
        {
            if (!_holyricsReportedAbsent)
            {
                _holyricsReportedAbsent = true;
                _logger.LogInformation("Som: a saida do Holyrics nao esta ativa; o Holyrics fica como esta");
            }

            _holyricsRunning = 0;
            return;
        }

        _holyricsReportedAbsent = false;
        if (_appOutput is null)
        {
            return;
        }

        var names = audio.Apps.FirstOrDefault(app => string.Equals(app.Name, HolyricsName, StringComparison.OrdinalIgnoreCase))?.ProcessNames
            ?? ["Holyrics", "javaw"];
        var processes = new SortedSet<int>();
        foreach (var device in devices)
        {
            foreach (var session in _system.GetSessions(device.Id))
            {
                if (session.ProcessId > 0 && !session.IsSystemSounds && AudioNames.MatchesProcess(names, session.ProcessName))
                {
                    processes.Add(session.ProcessId);
                }
            }
        }

        _holyricsRunning = processes.Count;
        var live = new HashSet<string>(processes.Select(pid => $"{pid}|{holyrics.Id}"));
        _routed.RemoveWhere(key => !live.Contains(key));
        _routeFailed.RemoveWhere(key => !live.Contains(key));
        foreach (var pid in processes)
        {
            var key = $"{pid}|{holyrics.Id}";
            if (!force && (_routed.Contains(key) || _routeFailed.Contains(key)))
            {
                continue;
            }

            var current = _appOutput.GetPersistedEndpoint(pid);
            if (current is not null && string.Equals(current, holyrics.Id, StringComparison.OrdinalIgnoreCase))
            {
                _routed.Add(key);
                continue;
            }

            if (_appOutput.SetPersistedEndpoint(pid, holyrics.Id))
            {
                _routed.Add(key);
                _routeFailed.Remove(key);
                _logger.LogInformation("Som: Holyrics (processo {Process}) agora toca em {Device}", pid, holyrics.ShortName);
            }
            else
            {
                _routeFailed.Add(key);
                _logger.LogWarning("Som: nao foi possivel mudar a saida do Holyrics para {Device}", holyrics.ShortName);
            }
        }
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

    private void UpdateWatch(IReadOnlyList<string> deviceIds)
    {
        var key = string.Join('\n', deviceIds);
        if (string.Equals(key, _watchedKey, StringComparison.Ordinal))
        {
            return;
        }

        _watchedKey = key;
        _system.WatchSessions(deviceIds);
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
        var waiting = audio.Enabled
            ? audio.Apps
                .Where(app => !AudioSettings.HiddenApps.Contains(app.Name, StringComparer.OrdinalIgnoreCase) && !_openedApps.Contains(app.Name))
                .Select(app => $"{app.Name}: aguardando abrir")
                .ToList()
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
            waiting,
            HolyricsText: HolyricsText(audio));
    }

    private string? HolyricsText(AudioSettings audio)
    {
        if (!audio.HolyricsRoute)
        {
            return null;
        }

        if (_holyricsDevice is null)
        {
            var wished = string.IsNullOrWhiteSpace(audio.HolyricsDeviceName) ? new DeviceMatchRule(audio.HolyricsMatch).Text : AudioNames.Short(audio.HolyricsDeviceName);
            return $"{wished} \U00002014 desconectado, deixando o Holyrics como est\U000000E1";
        }

        var name = _holyricsDevice.ShortName;
        if (_appOutput is null)
        {
            return $"{name} \U00002014 sem acesso \U000000E0 sa\U000000EDda por programa neste Windows";
        }

        if (_holyricsRunning == 0)
        {
            return $"{name} \U00002014 aguardando o Holyrics tocar";
        }

        return _routeFailed.Count > 0 && _routed.Count == 0
            ? $"{name} \U00002014 n\U000000E3o foi poss\U000000EDvel mudar a sa\U000000EDda do Holyrics"
            : $"Holyrics toca em {name}";
    }

    private static bool EqualStatus(AudioStatus a, AudioStatus b) =>
        a.CurrentOutputName == b.CurrentOutputName
        && a.SelectedName == b.SelectedName
        && a.SelectedPresent == b.SelectedPresent
        && a.LockOn == b.LockOn
        && a.LastAppliedAt == b.LastAppliedAt
        && a.AppliedSummary == b.AppliedSummary
        && a.Waiting.SequenceEqual(b.Waiting)
        && a.HolyricsText == b.HolyricsText
        && a.Devices.SequenceEqual(b.Devices);
}
