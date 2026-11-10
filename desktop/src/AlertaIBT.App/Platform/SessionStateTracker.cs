using AlertaIBT.Core.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace AlertaIBT.App.Platform;

/// <summary>
/// Feeds <see cref="SessionState"/> and the session marker with what Windows says about the session: locked or unlocked, remote connect and disconnect, the
/// PC suspending and resuming, the display going off and on, and monitors changing. It only listens; it moves no window and changes no setting. Each change is
/// one information line in the log (a lock, a resume or a display change is the usual company of a window-system error).
/// </summary>
public sealed class SessionStateTracker : IDisposable
{
    private readonly SessionState _state;
    private readonly SessionMarker? _marker;
    private readonly ILogger? _logger;
    private MessageWindow? _window;
    private bool _started;

    /// <param name="state">The state to keep up to date.</param>
    /// <param name="marker">Told when the PC suspends and resumes (the next start says "suspended and never came back").</param>
    /// <param name="logger">Receives the one-line notes.</param>
    public SessionStateTracker(SessionState state, SessionMarker? marker = null, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(state);

        _state = state;
        _marker = marker;
        _logger = logger;
    }

    /// <summary>Starts listening (once). <paramref name="window"/> brings the display on/off notification; null in tests.</summary>
    public void Start(MessageWindow? window = null)
    {
        if (_started)
        {
            return;
        }

        _started = true;
        _state.SetRemote(NativeMethods.GetSystemMetrics(NativeMethods.SM_REMOTESESSION) != 0);
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        if (window is not null)
        {
            _window = window;
            window.DisplayPowerChanged += OnDisplayPower;
            if (!window.WatchDisplayPower())
            {
                _logger?.LogInformation("Sessao: o Windows nao aceitou o aviso de tela ligada/desligada; o estado da tela fica desconhecido");
            }
        }

        _logger?.LogInformation("Sessao: {State}", _state.Describe());
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (!_started)
        {
            return;
        }

        _started = false;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        if (_window is not null)
        {
            _window.DisplayPowerChanged -= OnDisplayPower;
            _window = null;
        }
    }

    // What the session switch means for the windows: a lock or a disconnect hides the desktop; an unlock or a reconnect brings it back.
    internal void Apply(SessionSwitchReason reason)
    {
        switch (reason)
        {
            case SessionSwitchReason.SessionLock:
                _state.SetLocked(true);
                _logger?.LogInformation("Sessao bloqueada ({State})", _state.Describe());
                break;
            case SessionSwitchReason.SessionUnlock:
                _state.SetLocked(false);
                _logger?.LogInformation("Sessao desbloqueada ({State})", _state.Describe());
                break;
            case SessionSwitchReason.RemoteDisconnect:
            case SessionSwitchReason.ConsoleDisconnect:
                _state.SetDisconnected(true);
                _logger?.LogInformation("Sessao desconectada: {Reason} ({State})", reason, _state.Describe());
                break;
            case SessionSwitchReason.RemoteConnect:
            case SessionSwitchReason.ConsoleConnect:
                _state.SetRemote(NativeMethods.GetSystemMetrics(NativeMethods.SM_REMOTESESSION) != 0);
                _state.SetDisconnected(false);
                _logger?.LogInformation("Sessao conectada: {Reason} ({State})", reason, _state.Describe());
                break;
        }
    }

    internal void Apply(PowerModes mode)
    {
        switch (mode)
        {
            case PowerModes.Suspend:
                _state.Suspending();
                _marker?.Suspending();
                _logger?.LogInformation("O PC vai suspender ({State})", _state.Describe());
                break;
            case PowerModes.Resume:
                _state.Resumed();
                _marker?.Resumed();
                _logger?.LogInformation("O PC voltou da suspensao ({State})", _state.Describe());
                break;
        }
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e) => Safe(() => Apply(e.Reason));

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e) => Safe(() => Apply(e.Mode));

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) => Safe(() =>
    {
        _state.DisplayTopologyChanged();
        _logger?.LogInformation("Os monitores mudaram ({State})", _state.Describe());
    });

    private void OnDisplayPower(DisplayPower power) => Safe(() =>
    {
        _state.SetDisplay(power);
        _logger?.LogInformation("Tela {Power} ({State})", power, _state.Describe());
    });

    // SystemEvents handlers run on its own thread: nothing here may throw into it.
    private static void Safe(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A note about the session is never worth an exception.
        }
    }
}
