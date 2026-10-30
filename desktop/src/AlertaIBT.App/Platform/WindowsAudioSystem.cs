using System.Diagnostics;
using System.Runtime.InteropServices;
using AlertaIBT.Core.Audio;
using NAudio.CoreAudioApi;

namespace AlertaIBT.App.Platform;

/// <summary>
/// The Windows audio through Core Audio (NAudio for the devices, the endpoint volume and the sessions) and the undocumented but long-stable
/// <c>IPolicyConfig</c> for choosing the default output. Nothing is created or read before the first call, so building the app's object graph
/// (tests included) never touches the audio. Tests never use this class: they use a fake <see cref="IAudioSystem"/>.
/// </summary>
public sealed class WindowsAudioSystem : IAudioSystem
{
    private readonly object _gate = new();
    private MMDeviceEnumerator? _enumerator;
    private MMDeviceNotificationClient? _notifications;
    private readonly List<(MMDevice Device, AudioSessionManager Manager)> _watched = [];
    private readonly Dictionary<string, (MMDevice Device, AudioEndpointVolume Volume)> _endpoints = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AudioSessionControl> _sessionControls = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MMDevice> _sessionDevices = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    /// <inheritdoc />
    public event Action? DevicesChanged;

    /// <inheritdoc />
    public event Action? DefaultDeviceChanged;

    /// <inheritdoc />
    public event Action? SessionCreated;

    /// <inheritdoc />
    public IReadOnlyList<AudioDevice> ActiveRenderDevices()
    {
        lock (_gate)
        {
            var devices = new List<AudioDevice>();
            foreach (var device in Enumerator().EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                try
                {
                    devices.Add(new AudioDevice(device.ID, device.FriendlyName));
                }
                catch (Exception ex) when (ex is COMException or InvalidOperationException)
                {
                    // The device went away while it was being read: it is simply not active any more.
                }
                finally
                {
                    device.Dispose();
                }
            }

            return devices;
        }
    }

    /// <inheritdoc />
    public DefaultDevices GetDefaultDevices()
    {
        lock (_gate)
        {
            return new DefaultDevices(DefaultId(Role.Console), DefaultId(Role.Multimedia), DefaultId(Role.Communications));
        }
    }

    /// <inheritdoc />
    public bool SetDefaultRenderDevice(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return false;
        }

        lock (_gate)
        {
            IPolicyConfig? policy = null;
            try
            {
                policy = (IPolicyConfig)new PolicyConfigClient();
                var ok = true;
                foreach (var role in new[] { Role.Console, Role.Multimedia, Role.Communications })
                {
                    ok &= policy.SetDefaultEndpoint(deviceId, (int)role) >= 0;
                }

                return ok;
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException or InvalidOperationException)
            {
                return false;
            }
            finally
            {
                if (policy is not null)
                {
                    Marshal.ReleaseComObject(policy);
                }
            }
        }
    }

    /// <inheritdoc />
    public bool SetMasterVolume(string deviceId, int percent, bool muted)
    {
        lock (_gate)
        {
            // The endpoint volume of a device is kept between calls (activating it costs a few milliseconds); a stale one is replaced once.
            for (var attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    if (!_endpoints.TryGetValue(deviceId, out var cached))
                    {
                        var device = Enumerator().GetDevice(deviceId);
                        cached = (device, device.AudioEndpointVolume);
                        _endpoints[deviceId] = cached;
                    }

                    cached.Volume.MasterVolumeLevelScalar = Scalar(percent);
                    cached.Volume.Mute = muted;
                    return true;
                }
                catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException)
                {
                    DropEndpoint(deviceId);
                }
            }

            return false;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<AudioSessionInfo> GetSessions(string deviceId)
    {
        lock (_gate)
        {
            var result = new List<AudioSessionInfo>();
            try
            {
                // The device and its session controls stay alive in the cache so a volume change later needs no enumeration.
                var device = Enumerator().GetDevice(deviceId);
                if (_sessionDevices.Remove(deviceId, out var previous))
                {
                    previous.Dispose();
                }

                _sessionDevices[deviceId] = device;
                foreach (var stale in _sessionControls.Keys.Where(k => k.StartsWith(deviceId + "|", StringComparison.OrdinalIgnoreCase)).ToList())
                {
                    _sessionControls.Remove(stale);
                }

                var manager = device.AudioSessionManager;
                manager.RefreshSessions();
                var sessions = manager.Sessions;
                for (var i = 0; i < sessions.Count; i++)
                {
                    var session = sessions[i];
                    var system = session.IsSystemSoundsSession;
                    var processId = system ? 0 : (int)session.GetProcessID;
                    var key = session.GetSessionInstanceIdentifier;
                    _sessionControls[deviceId + "|" + key] = session;
                    result.Add(new AudioSessionInfo(key, system ? null : ProcessName(session.GetProcessID), system, processId));
                }
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException)
            {
                // The output vanished: no sessions.
            }

            return result;
        }
    }

    /// <inheritdoc />
    public bool SetSessionVolume(string deviceId, string sessionKey, int percent, bool muted)
    {
        lock (_gate)
        {
            // The control found by the last enumeration is used as it is; only when it is gone (the program closed) is the device looked at again.
            if (_sessionControls.TryGetValue(deviceId + "|" + sessionKey, out var cached))
            {
                try
                {
                    cached.SimpleAudioVolume.Volume = Scalar(percent);
                    cached.SimpleAudioVolume.Mute = muted;
                    return true;
                }
                catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException)
                {
                    _sessionControls.Remove(deviceId + "|" + sessionKey);
                }
            }

            try
            {
                using var device = Enumerator().GetDevice(deviceId);
                var manager = device.AudioSessionManager;
                manager.RefreshSessions();
                var sessions = manager.Sessions;
                for (var i = 0; i < sessions.Count; i++)
                {
                    var session = sessions[i];
                    if (!string.Equals(session.GetSessionInstanceIdentifier, sessionKey, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    session.SimpleAudioVolume.Volume = Scalar(percent);
                    session.SimpleAudioVolume.Mute = muted;
                    return true;
                }

                return false;
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException)
            {
                return false;
            }
        }
    }

    /// <inheritdoc />
    public void WatchSessions(IReadOnlyList<string> deviceIds)
    {
        lock (_gate)
        {
            StopWatching();
            if (_disposed)
            {
                return;
            }

            foreach (var deviceId in deviceIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                MMDevice? device = null;
                try
                {
                    device = Enumerator().GetDevice(deviceId);
                    var manager = device.AudioSessionManager;
                    manager.OnSessionCreated += OnSessionCreated;
                    _watched.Add((device, manager));
                }
                catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException)
                {
                    device?.Dispose();
                }
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        MMDeviceNotificationClient? notifications;
        MMDeviceEnumerator? enumerator;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            StopWatching();
            ForgetCaches();
            notifications = _notifications;
            enumerator = _enumerator;
            _notifications = null;
            _enumerator = null;
        }

        // Outside the lock: removing the notification client waits for a callback that is running, and that callback takes the lock (ForgetCaches),
        // so doing it inside would freeze the exit whenever a device event arrives at that moment.
        try
        {
            if (notifications is not null)
            {
                notifications.DeviceStateChanged -= OnDevicesChanged;
                notifications.DeviceAdded -= OnDevicesChanged;
                notifications.DeviceRemoved -= OnDevicesChanged;
                notifications.DefaultDeviceChanged -= OnDefaultChanged;
                notifications.Dispose();
            }

            enumerator?.Dispose();
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            // The audio service is gone: nothing is left to unregister.
        }
    }

    private static float Scalar(int percent) => Math.Clamp(percent, 0, 100) / 100f;

    // A session whose program already closed has no process: no name, so no setting is applied to it.
    private static string? ProcessName(uint processId)
    {
        if (processId == 0)
        {
            return null;
        }

        try
        {
            using var process = Process.GetProcessById((int)processId);
            return process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    private string? DefaultId(Role role)
    {
        if (!Enumerator().TryGetDefaultAudioEndpoint(DataFlow.Render, role, out var device) || device is null)
        {
            return null;
        }

        try
        {
            return device.ID;
        }
        finally
        {
            device.Dispose();
        }
    }

    // Made on first use, with the change notifications (raised on a Windows thread: the listener only schedules work).
    private MMDeviceEnumerator Enumerator()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_enumerator is not null)
        {
            return _enumerator;
        }

        _enumerator = new MMDeviceEnumerator();
        _notifications = _enumerator.CreateNotificationClient(useSynchronizationContext: false);
        _notifications.DeviceStateChanged += OnDevicesChanged;
        _notifications.DeviceAdded += OnDevicesChanged;
        _notifications.DeviceRemoved += OnDevicesChanged;
        _notifications.DefaultDeviceChanged += OnDefaultChanged;
        return _enumerator;
    }

    private void StopWatching()
    {
        foreach (var (device, manager) in _watched)
        {
            try
            {
                manager.OnSessionCreated -= OnSessionCreated;
                manager.Dispose();
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException)
            {
                // Closing a manager of a device that is gone can fail: nothing is left to close.
            }

            device.Dispose();
        }

        _watched.Clear();
    }

    private void OnSessionCreated(object? sender, AudioSessionControl newSession) => Raise(SessionCreated);

    private void DropEndpoint(string deviceId)
    {
        if (_endpoints.Remove(deviceId, out var old))
        {
            try
            {
                old.Device.Dispose();
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException)
            {
                // Already gone.
            }
        }
    }

    // The outputs changed: what was kept about devices and sessions may be stale.
    private void ForgetCaches()
    {
        lock (_gate)
        {
            foreach (var id in _endpoints.Keys.ToList())
            {
                DropEndpoint(id);
            }

            _sessionControls.Clear();
            foreach (var device in _sessionDevices.Values)
            {
                try
                {
                    device.Dispose();
                }
                catch (Exception ex) when (ex is COMException or InvalidOperationException)
                {
                    // Already gone.
                }
            }

            _sessionDevices.Clear();
        }
    }

    private void OnDevicesChanged(object? sender, EventArgs e)
    {
        ForgetCaches();
        Raise(DevicesChanged);
    }

    private void OnDefaultChanged(object? sender, DefaultDeviceChangedEventArgs e)
    {
        if (e.Flow == DataFlow.Render)
        {
            Raise(DefaultDeviceChanged);
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
            // A listener's failure must never travel back into a Windows callback thread.
        }
    }

    // The undocumented policy-config interface (stable since Windows Vista): only SetDefaultEndpoint is used, the other slots keep the vtable layout.
    [ComImport]
    [Guid("F8679F50-850A-41CF-9C72-430F290290C8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat();

        [PreserveSig] int GetDeviceFormat();

        [PreserveSig] int ResetDeviceFormat();

        [PreserveSig] int SetDeviceFormat();

        [PreserveSig] int GetProcessingPeriod();

        [PreserveSig] int SetProcessingPeriod();

        [PreserveSig] int GetShareMode();

        [PreserveSig] int SetShareMode();

        [PreserveSig] int GetPropertyValue();

        [PreserveSig] int SetPropertyValue();

        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int role);

        [PreserveSig] int SetEndpointVisibility();
    }

    [ComImport]
    [Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
    private class PolicyConfigClient;
}
