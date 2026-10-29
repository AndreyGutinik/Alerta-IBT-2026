using AlertaIBT.Core.Screens;
using AlertaIBT.Core.Settings;
using Microsoft.Extensions.Logging;

namespace AlertaIBT.Core.Ndi;

/// <summary>The whole NDI state: the runtime plus the switcher.</summary>
/// <param name="RuntimeOk">Whether the NDI runtime was found and works.</param>
/// <param name="RuntimeText">The version when it works, else the reason with the download link.</param>
/// <param name="Switcher">The switcher state.</param>
public sealed record NdiStatus(bool RuntimeOk, string RuntimeText, NdiSwitcherStatus Switcher);

/// <summary>What the local API and the hotkeys drive.</summary>
public interface INdiControl
{
    /// <summary>Puts the telao layer ON or OFF.</summary>
    NdiResult SetLayer(bool on);

    /// <summary>Cuts Preview to Program (does not choose cameras).</summary>
    NdiResult Take();

    /// <summary>Puts camera <paramref name="slot"/> (0-based) in Preview.</summary>
    NdiResult SelectPreview(int slot);

    /// <summary>The state now.</summary>
    NdiStatus GetStatus();
}

/// <summary>
/// The public face of the NDI -> telao engine, the one the NDI tab, the hotkeys and the local API use. It owns the switcher (two always-connected
/// receivers), the source finder and the settings glue. Receive only, video only. Everything returns at once; events come from any thread, so the
/// UI marshals itself. Slots are 0-based here (show them 1-based).
/// </summary>
public sealed class NdiService : INdiControl, IDisposable
{
    private readonly INdiRuntime _runtime;
    private readonly SettingsService _settings;
    private readonly ILogger? _logger;
    private readonly object _gate = new();
    private readonly NdiSwitcher _switcher;
    private IReadOnlyList<NdiSourceInfo> _sources = [];
    private Thread? _finderThread;
    private CancellationTokenSource? _finderStop;
    private IReadOnlyList<string> _finderIps = [];
    private int _finderRequests;
    private bool _disposed;

    /// <param name="runtime">The NDI runtime (real or fake).</param>
    /// <param name="settings">The settings (the <c>Ndi</c> section is read live).</param>
    /// <param name="clock">The clock.</param>
    /// <param name="logger">Log.</param>
    /// <param name="receiverFactory">Creates a receiver by name; the real <see cref="NdiReceiver"/> over <paramref name="runtime"/> when null (tests pass fakes).</param>
    /// <param name="holdTime">Freeze time of a silent on-air source (5 s by default).</param>
    public NdiService(
        INdiRuntime runtime,
        SettingsService settings,
        TimeProvider? clock = null,
        ILogger? logger = null,
        Func<string, ICameraReceiver>? receiverFactory = null,
        TimeSpan? holdTime = null)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(settings);

        _runtime = runtime;
        _settings = settings;
        _logger = logger;
        var time = clock ?? TimeProvider.System;
        _switcher = new NdiSwitcher(
            receiverFactory ?? (name => new NdiReceiver(runtime, name, time, logger)),
            () => settings.Current.Ndi,
            time,
            logger,
            Enrich,
            holdTime);
        _switcher.Changed += () => Changed?.Invoke();
        _switcher.Error += message => Error?.Invoke(message);
        _switcher.WindowVisibleChanged += visible => WindowVisibleChanged?.Invoke(visible);
        _switcher.ProgramFrame += OnProgramFrame;
        _switcher.ProgramPositionChanged += position => ProgramPositionChanged?.Invoke(position);
        _switcher.ThumbnailUpdated += slot => ThumbnailUpdated?.Invoke(slot);
        _settings.Changed += OnSettingsChanged;
    }

    /// <summary>Something the UI shows changed; read <see cref="GetStatus"/> again. Any thread.</summary>
    public event Action? Changed;

    /// <summary>A message for the user (a failed Take, a source that cannot be reached). Any thread.</summary>
    public event Action<string>? Error;

    /// <summary>The camera window must show or hide. Any thread.</summary>
    public event Action<bool>? WindowVisibleChanged;

    /// <summary>A frame of the on-air camera, on the capture thread; the window copies it and returns at once.</summary>
    public event NdiProgramFrameHandler? ProgramFrame;

    /// <summary>The on-air camera's vertical position changed in the settings: repaint with the stored picture.</summary>
    public event Action<double>? ProgramPositionChanged;

    /// <summary>The thumbnail of the camera in this slot changed (<c>-1</c> for a lane without a slot). At most 20 per second per lane. Any thread.</summary>
    public event Action<int>? ThumbnailUpdated;

    /// <summary>The finder's list changed. Any thread.</summary>
    public event Action? SourcesChanged;

    /// <summary>The switcher (for the window and for tests).</summary>
    public NdiSwitcher Switcher => _switcher;

    /// <summary>Whether the NDI runtime works.</summary>
    public bool RuntimeAvailable => _runtime.IsAvailable;

    /// <summary>The runtime version when it works, else the reason (with <see cref="NdiConstants.DownloadUrl"/>).</summary>
    public string RuntimeText => _runtime.IsAvailable ? _runtime.Version : _runtime.UnavailableReason ?? NdiConstants.RuntimeMissingText;

    /// <summary>The trademark notice plus the NDI link, for About and diagnostics.</summary>
    public static string AboutText => NdiConstants.AboutText;

    /// <summary>The sources the finder knows (empty until it runs and finds some).</summary>
    public IReadOnlyList<NdiSourceInfo> Sources
    {
        get
        {
            lock (_gate)
            {
                return _sources;
            }
        }
    }

    /// <summary>The telao layer switch (false at start).</summary>
    public bool LayerOn => _switcher.LayerOn;

    /// <summary>The settings' <c>Ndi</c> part.</summary>
    public NdiSettings Settings => _settings.Current.Ndi;

    /// <summary>Starts looking for sources (call when the NDI tab opens and while a name-based camera is used). Calls nest: each Start needs a Stop.</summary>
    public void StartDiscovery()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _finderRequests++;
            EnsureFinderLocked();
        }
    }

    /// <summary>Gives back one <see cref="StartDiscovery"/>; the finder stops when none is left.</summary>
    public void StopDiscovery()
    {
        lock (_gate)
        {
            if (_finderRequests > 0)
            {
                _finderRequests--;
            }

            if (_finderRequests == 0)
            {
                StopFinderLocked();
            }
        }
    }

    /// <inheritdoc />
    public NdiResult SetLayer(bool on) => _switcher.SetLayer(on);

    /// <summary>Flips the layer; returns the new state.</summary>
    public NdiResult ToggleLayer() => _switcher.SetLayer(!_switcher.LayerOn);

    /// <summary>Shows or hides the color bars.</summary>
    public NdiResult SetTestPattern(bool on) => _switcher.SetTestPattern(on);

    /// <inheritdoc />
    public NdiResult Take() => _switcher.Take();

    /// <inheritdoc />
    public NdiResult SelectPreview(int slot)
    {
        var result = _switcher.SelectPreview(slot);
        if (result.Success)
        {
            StartNameDiscoveryIfNeeded();
        }

        return result;
    }

    /// <summary>Changes a camera's vertical position (0 top .. 100 bottom); the picture moves at once and the value is saved.</summary>
    public NdiResult SetVerticalPosition(int slot, int position) =>
        UpdateCamera(slot, camera => camera with { VerticalPosition = Math.Clamp(position, 0, 100) });

    /// <summary>Sets a camera's name and source (a finder name or a direct address) and saves it.</summary>
    public NdiResult SetCamera(int slot, string name, string source) =>
        UpdateCamera(slot, camera => camera with { Name = string.IsNullOrWhiteSpace(name) ? camera.Name : name.Trim(), Source = source?.Trim() ?? string.Empty });

    /// <inheritdoc />
    public NdiStatus GetStatus() => new(_runtime.IsAvailable, RuntimeText, _switcher.GetStatus());

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
            StopFinderLocked();
        }

        _settings.Changed -= OnSettingsChanged;
        _switcher.Dispose();
    }

    private NdiResult UpdateCamera(int slot, Func<NdiCameraSlot, NdiCameraSlot> change)
    {
        if (slot is < 0 or >= NdiSettings.SlotCount)
        {
            return NdiResult.Fail("C\U000000E2mera inexistente");
        }

        _settings.Update(settings =>
        {
            var cameras = settings.Ndi.Cameras.ToList();
            cameras[slot] = change(cameras[slot]);
            return settings with { Ndi = settings.Ndi with { Cameras = cameras } };
        });
        return NdiResult.Ok;
    }

    private void OnProgramFrame(in NdiFrameView frame, double position) => ProgramFrame?.Invoke(frame, position);

    private void OnSettingsChanged(AppSettings changed)
    {
        _switcher.ApplySettings();
        lock (_gate)
        {
            if (_finderThread is not null && !_finderIps.SequenceEqual(changed.Ndi.FixedIps))
            {
                StopFinderLocked();
                EnsureFinderLocked();
            }
        }
    }

    // Adds the address the finder reported to a name, so the receiver connects by url (the finder's extra ips are not known to the receiver).
    private NdiSourceRef Enrich(NdiSourceRef source)
    {
        if (source.Url is not null || source.Name is null)
        {
            return source;
        }

        var found = Sources.FirstOrDefault(s => string.Equals(s.Name, source.Name, StringComparison.OrdinalIgnoreCase));
        return found is { Address.Length: > 0 } ? source with { Url = found.Address } : source;
    }

    private void StartNameDiscoveryIfNeeded()
    {
        // A camera chosen by name needs the finder so its address is known; the finder stays on while the app runs (it is cheap).
        lock (_gate)
        {
            if (_finderThread is null && !_disposed && _runtime.IsAvailable)
            {
                _finderRequests++;
                EnsureFinderLocked();
            }
        }
    }

    private void EnsureFinderLocked()
    {
        if (_finderThread is not null || !_runtime.IsAvailable)
        {
            return;
        }

        _finderStop = new CancellationTokenSource();
        var token = _finderStop.Token;
        _finderIps = _settings.Current.Ndi.FixedIps.ToList();
        var ips = _finderIps;
        _finderThread = new Thread(() => RunFinder(ips, token)) { IsBackground = true, Name = "NDI finder" };
        _finderThread.Start();
    }

    private void StopFinderLocked()
    {
        var thread = _finderThread;
        _finderStop?.Cancel();
        _finderThread = null;
        _finderStop = null;
        if (thread is not null)
        {
            // Not joined under the lock: the thread may be waiting for it to report a change.
            ThreadPool.QueueUserWorkItem(_ => thread.Join(TimeSpan.FromSeconds(3)));
        }
    }

    private void RunFinder(IReadOnlyList<string> extraIps, CancellationToken token)
    {
        INdiFinder? finder = null;
        try
        {
            finder = _runtime.CreateFinder(extraIps);
            while (!token.IsCancellationRequested)
            {
                finder.WaitForChange(1000);
                var list = finder.GetSources().OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
                bool changed;
                lock (_gate)
                {
                    changed = !_sources.SequenceEqual(list);
                    if (changed)
                    {
                        _sources = list;
                    }
                }

                if (changed)
                {
                    _logger?.LogInformation("NDI: {Count} fonte(s) encontrada(s)", list.Count);
                    SourcesChanged?.Invoke();
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger?.LogWarning(ex, "O localizador NDI parou");
        }
        finally
        {
            finder?.Dispose();
        }
    }

    /// <summary>The monitor the camera goes to (see <see cref="NdiMonitorChooser"/>).</summary>
    /// <param name="screens">The connected monitors.</param>
    /// <param name="led">The LED as the alert bar found it (it also handles <c>--test-led</c>).</param>
    public ScreenInfo? ChooseMonitor(IReadOnlyList<ScreenInfo> screens, ScreenInfo? led) =>
        NdiMonitorChooser.Choose(screens, led, _settings.Current.Ndi);
}

/// <summary>Picks the monitor of the camera layer.</summary>
public static class NdiMonitorChooser
{
    /// <summary>
    /// Locked (default): the same 2560x768 monitor the alert bar uses (<paramref name="led"/>). Unlocked: the monitor with the chosen display name,
    /// and when it is not connected any more, the LED again. <c>null</c> when there is no monitor to use (the layer stays hidden).
    /// </summary>
    public static ScreenInfo? Choose(IReadOnlyList<ScreenInfo> screens, ScreenInfo? led, NdiSettings settings)
    {
        ArgumentNullException.ThrowIfNull(screens);
        ArgumentNullException.ThrowIfNull(settings);

        if (!settings.MonitorLocked && !string.IsNullOrWhiteSpace(settings.MonitorDeviceName))
        {
            var chosen = screens.FirstOrDefault(s => string.Equals(s.DeviceName, settings.MonitorDeviceName, StringComparison.OrdinalIgnoreCase));
            if (chosen is not null)
            {
                return chosen;
            }
        }

        return led;
    }
}
