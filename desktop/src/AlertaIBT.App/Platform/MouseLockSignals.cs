using System.Windows.Threading;
using Microsoft.Win32;

namespace AlertaIBT.App.Platform;

/// <summary>
/// The system events that make the mouse lock check itself: display changes (a monitor plugged or unplugged, a new primary one),
/// session changes (unlock, remote desktop in or out) and a 1 s watchdog that runs only while the lock is on. All of them only call
/// <see cref="MouseLockController.Verify"/>, which acts when Windows holds a different confinement than the wanted one. The events of
/// <see cref="SystemEvents"/> are static, so <see cref="Dispose"/> must run (the shell does it) or they would keep this object alive.
/// </summary>
public sealed class MouseLockSignals : IDisposable
{
    private readonly MouseLockController _controller;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _watchdog;
    private bool _disposed;

    /// <param name="controller">The lock to keep.</param>
    /// <param name="dispatcher">The UI thread's dispatcher (the system events arrive on another thread).</param>
    public MouseLockSignals(MouseLockController controller, Dispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(controller);
        ArgumentNullException.ThrowIfNull(dispatcher);

        _controller = controller;
        _dispatcher = dispatcher;
        _watchdog = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromSeconds(1) };
        _watchdog.Tick += OnWatchdog;
        SystemEvents.DisplaySettingsChanged += OnSystemChange;
        SystemEvents.SessionSwitch += OnSessionSwitch;
        _controller.LockedChanged += OnLockedChanged;
        OnLockedChanged(_controller.IsLocked);
    }

    /// <summary>Whether the watchdog is running (only while the lock is on).</summary>
    public bool WatchdogRunning => _watchdog.IsEnabled;

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        SystemEvents.DisplaySettingsChanged -= OnSystemChange;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        _controller.LockedChanged -= OnLockedChanged;
        _watchdog.Stop();
        _watchdog.Tick -= OnWatchdog;
    }

    private void OnLockedChanged(bool locked)
    {
        if (locked)
        {
            _watchdog.Start();
        }
        else
        {
            _watchdog.Stop();
        }
    }

    private void OnWatchdog(object? sender, EventArgs e) => _controller.Verify();

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e) => OnSystemChange(sender, e);

    // SystemEvents raises on its own thread.
    private void OnSystemChange(object? sender, EventArgs e) => _dispatcher.BeginInvoke(_controller.Verify);
}
