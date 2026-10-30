using AlertaIBT.Core.Input;
using AlertaIBT.Core.Settings;
using Microsoft.Extensions.Logging;

namespace AlertaIBT.App.Platform;

/// <summary>
/// Joins the mouse lock to the rest of the app, on the UI thread: it follows <see cref="MouseLockSettings"/> (the lock, its
/// shortcut, the Ctrl x3 recenter), toggles the lock when the shortcut or the tray item says so (and saves that, so the lock comes
/// back after a restart), and lets go of the cursor, the keyboard hook and the shortcut on the way out. The ONE passive keyboard hook is
/// shared: it also feeds the media keys of the music player (<see cref="MediaKeyRouter"/>), and is installed while either Ctrl x3 or
/// the media keys are wanted. Nothing happens before
/// <see cref="Start"/>, so building the object graph (tests included) never touches the mouse or the keyboard.
/// </summary>
public sealed class MouseLockController : IDisposable
{
    private readonly SettingsService _settings;
    private readonly MouseLockService _service;
    private readonly IKeyboardHook _hook;
    private readonly Action<Action> _post;
    private readonly ILogger? _logger;
    private readonly MediaKeyRouter? _media;
    private readonly TripleCtrlDetector _detector = new();
    private readonly KeyObserver _observer;
    private QuickPanelHotkey? _hotkey;
    private bool _tripleCtrlOn;
    private bool _started;
    private bool _disposed;

    /// <param name="settings">The settings (the lock is saved there).</param>
    /// <param name="service">The lock itself.</param>
    /// <param name="hook">The keyboard hook that sees Ctrl x3.</param>
    /// <param name="post">Runs an action on the UI thread.</param>
    /// <param name="logger">Receives what happens (never a key).</param>
    /// <param name="media">The media keys of the music player; they ride on the same hook. Optional.</param>
    public MouseLockController(
        SettingsService settings,
        MouseLockService service,
        IKeyboardHook hook,
        Action<Action> post,
        ILogger? logger = null,
        MediaKeyRouter? media = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(hook);
        ArgumentNullException.ThrowIfNull(post);

        _settings = settings;
        _service = service;
        _hook = hook;
        _post = post;
        _logger = logger;
        _media = media;
        _observer = OnKey;
        _service.LockedChanged += OnLockedChanged;
        _service.OutOfStepContext = ForegroundProgram;
    }

    // The program that has the focus now (its exe name only): a clue to who released the confinement.
    private static string? ForegroundProgram()
    {
        var window = NativeMethods.GetForegroundWindow();
        if (window == IntPtr.Zero || NativeMethods.GetWindowThreadProcessId(window, out var pid) == 0)
        {
            return null;
        }

        try
        {
            using var process = System.Diagnostics.Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    /// <summary>The shortcut that toggles the lock, as the text "modifiers+key" (the default of <see cref="MouseLockSettings"/> when the saved one is unusable).</summary>
    public static string DefaultHotkey => MouseLockSettings.DefaultHotkey;

    /// <summary>Whether the lock is on.</summary>
    public bool IsLocked => _service.IsLocked;

    /// <summary>Raised when the lock turned on or off (on the UI thread).</summary>
    public event Action<bool>? LockedChanged;

    /// <summary>Whether the lock shortcut works and, if not, why (see <see cref="HotkeyStatus"/>).</summary>
    public HotkeyStatus HotkeyStatus => _hotkey?.Status ?? HotkeyStatus.None;

    /// <summary>The shortcut was applied again (start, or the setting changed).</summary>
    public event Action<HotkeyStatus>? HotkeyStatusChanged;

    /// <summary>Whether the keyboard hook is installed (Ctrl x3 and/or the media keys are wanted).</summary>
    public bool TripleCtrlActive => _hook.IsInstalled;

    /// <summary>
    /// Registers the shortcut and applies the settings: the lock comes back on when it was on, the keyboard hook is installed when
    /// Ctrl x3 is on. Call on the UI thread, once.
    /// </summary>
    /// <param name="registrar">Registers the lock shortcut with the system.</param>
    public void Start(IHotkeyRegistrar registrar)
    {
        ArgumentNullException.ThrowIfNull(registrar);

        if (_started || _disposed)
        {
            return;
        }

        _started = true;
        _hotkey = new QuickPanelHotkey(
            registrar,
            _logger,
            MouseLockSettings.DefaultHotkey,
            "do bloqueio do mouse",
            display => $"O atalho {display} (travar o mouse) j\U000000E1 \U000000E9 usado por outro programa. Escolha outro em Configura\U000000E7\U000000F5es.");
        _hotkey.StatusChanged += OnHotkeyStatusChanged;
        ApplySettings(_settings.Current.MouseLock);
    }

    /// <summary>
    /// Makes the lock, its shortcut and the Ctrl x3 hook match <paramref name="mouseLock"/>. Safe to call on every change of any
    /// setting: whatever already matches is left alone.
    /// </summary>
    public void ApplySettings(MouseLockSettings mouseLock)
    {
        ArgumentNullException.ThrowIfNull(mouseLock);

        if (!_started || _disposed)
        {
            return;
        }

        _hotkey?.Apply(mouseLock.Hotkey);
        ApplyTripleCtrl(mouseLock.TripleCtrl);
        if (mouseLock.Enabled)
        {
            _service.Lock();
        }
        else
        {
            _service.Unlock();
        }
    }

    /// <summary>The shortcut or the tray item: turns the lock on or off and saves it, so it is restored at the next start.</summary>
    public void Toggle()
    {
        if (_disposed || !_started)
        {
            return;
        }

        var on = _service.Toggle();
        if (_settings.Current.MouseLock.Enabled != on)
        {
            _settings.Update(s => s with { MouseLock = s.MouseLock with { Enabled = on } });
        }
    }

    /// <summary>Puts the confinement back if something undid it (display or session change, the 1 s watchdog).</summary>
    public void Verify()
    {
        if (!_disposed && _started)
        {
            _service.Verify();
        }
    }

    /// <summary>Sends the cursor to the center of the primary monitor, lock on or off.</summary>
    public void Recenter()
    {
        if (_disposed || !_started)
        {
            return;
        }

        if (_service.Recenter())
        {
            _logger?.LogInformation("Ctrl x3: mouse centralizado");
        }
        else
        {
            _logger?.LogWarning("Ctrl x3: nao foi possivel centralizar o mouse");
        }
    }

    /// <summary>
    /// Lets go of the cursor now, without touching the saved setting (the lock comes back at the next start): on the way out, before an
    /// update restarts the app, and after an unhandled error. Never throws.
    /// </summary>
    public void ReleaseNow()
    {
        try
        {
            _hook.Uninstall();
            _service.Unlock();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Soltar o mouse falhou");
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _service.LockedChanged -= OnLockedChanged;
        if (_hotkey is not null)
        {
            _hotkey.StatusChanged -= OnHotkeyStatusChanged;
        }

        ReleaseNow();
        _hook.Dispose();
    }

    // One hook for both: installed while Ctrl x3 or the media keys are wanted, removed when neither is.
    private void ApplyTripleCtrl(bool on)
    {
        _tripleCtrlOn = on;
        var media = _media?.Wanted ?? false;
        if (!on && !media)
        {
            _hook.Uninstall();
            return;
        }

        if (_hook.IsInstalled)
        {
            return;
        }

        _detector.Reset();
        _media?.Reset();
        if (_hook.Install(_observer))
        {
            _logger?.LogInformation("Gancho do teclado ligado (Ctrl x3: {TripleCtrl}, teclas de midia: {Media})", on, media);
        }
        else
        {
            _logger?.LogWarning("O Windows recusou o gancho do teclado (Ctrl x3 e teclas de midia)");
        }
    }

    // Runs inside the keyboard hook: only the timestamp state machine. The move itself is posted, off the hook.
    private void OnKey(int virtualKey, bool keyDown, bool altDown, uint timeMs)
    {
        if (_tripleCtrlOn && _detector.Feed(virtualKey, keyDown, altDown, timeMs))
        {
            _post(Recenter);
        }

        _media?.Feed(virtualKey, keyDown);
    }

    private void OnLockedChanged(bool locked) => LockedChanged?.Invoke(locked);

    private void OnHotkeyStatusChanged(HotkeyStatus status)
    {
        if (status.Registered)
        {
            _logger?.LogInformation("Atalho do bloqueio do mouse: {Hotkey}", status.Text);
        }
        else
        {
            _logger?.LogWarning("Atalho do bloqueio do mouse indisponivel: {Hotkey} ({Reason})", status.Text, status.Short);
        }

        HotkeyStatusChanged?.Invoke(status);

        // A saved value that could not be used was replaced by the default: save that back, so the tab shows what really works.
        if (status.FellBackFrom is not null)
        {
            _settings.Update(s => s with { MouseLock = s.MouseLock with { Hotkey = status.Text } });
        }
    }
}
