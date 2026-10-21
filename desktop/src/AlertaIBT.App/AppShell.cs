using System.Windows;
using AlertaIBT.App.Hosting;
using AlertaIBT.App.Overlay;
using AlertaIBT.App.Platform;
using AlertaIBT.App.Services;
using AlertaIBT.App.Tray;
using AlertaIBT.Core.Input;
using AlertaIBT.Core.Services;
using AlertaIBT.Core.Settings;
using Microsoft.Extensions.Logging;

namespace AlertaIBT.App;

/// <summary>
/// The UI-thread coordinator: the tray icon, the hidden message window, the quick-panel hotkey, "start with Windows" and
/// exit. It owns no window of its own. It turns a tray click, the hotkey or a second launch into the events
/// <see cref="QuickPanelRequested"/>, <see cref="MainWindowRequested"/> and <see cref="PauseToggleRequested"/>, which the
/// quick panel, the main window and the alert service listen to as those parts are built.
/// </summary>
public sealed class AppShell : IDisposable
{
    private readonly ILogger<AppShell> _logger;
    private readonly SettingsService _settings;
    private readonly TrayService _tray;
    private readonly StartupRegistration _startup;
    private readonly StartupArguments _arguments;
    private readonly LedWatcher _led;
    private readonly OverlayController _overlay;
    private readonly AlertRuntime _runtime;

    private MessageWindow? _messages;
    private HotkeyService? _hotkeys;
    private QuickPanelHotkey? _hotkey;
    private bool _exitDialogOpen;
    private bool _exiting;

    /// <summary>Wires nothing yet; <see cref="Start"/> does.</summary>
    public AppShell(
        ILogger<AppShell> logger,
        SettingsService settings,
        TrayService tray,
        StartupRegistration startup,
        StartupArguments arguments,
        LedWatcher led,
        OverlayController overlay,
        AlertRuntime runtime)
    {
        _logger = logger;
        _settings = settings;
        _tray = tray;
        _startup = startup;
        _arguments = arguments;
        _led = led;
        _overlay = overlay;
        _runtime = runtime;
    }

    /// <summary>The user wants the quick panel (tray click or the hotkey). Raised on the UI thread.</summary>
    public event Action? QuickPanelRequested;

    /// <summary>The user wants the main window (tray double click, menu Abrir, a second launch, or opening the app by hand). Raised on the UI thread.</summary>
    public event Action? MainWindowRequested;

    /// <summary>
    /// The user asked, from the tray menu, to pause or resume, and the shell has already done it (see <see cref="AlertRuntime.TogglePause"/>):
    /// a listener must not toggle again. Raised on the UI thread.
    /// </summary>
    public event Action? PauseToggleRequested;

    /// <summary>The tray icon, so other parts can add menu items or notifications.</summary>
    public TrayService Tray => _tray;

    /// <summary>The message window, for the parts that need display-change or hotkey messages (valid after <see cref="Start"/>).</summary>
    public MessageWindow Messages => _messages ?? throw new InvalidOperationException("The shell has not been started.");

    /// <summary>Whether the quick-panel hotkey is registered (another program may own the combination).</summary>
    public bool HotkeyAvailable => HotkeyStatus.Registered;

    /// <summary>Whether the quick-panel hotkey works and, if not, the words for the user (the panel and the tray tooltip show them).</summary>
    public HotkeyStatus HotkeyStatus => _hotkey?.Status ?? HotkeyStatus.None;

    /// <summary>The hotkey was applied again (start, or the setting changed). Raised on the UI thread.</summary>
    public event Action<HotkeyStatus>? HotkeyStatusChanged;

    /// <summary>Starts the tray, the hotkey and "start with Windows". Call on the UI thread.</summary>
    public void Start()
    {
        _messages = new MessageWindow();
        _hotkeys = new HotkeyService(_messages);
        _hotkeys.QuickPanelPressed += RaiseQuickPanel;
        _hotkey = new QuickPanelHotkey(_hotkeys, _logger);
        _hotkey.StatusChanged += OnHotkeyStatusChanged;

        WireTray();

        _tray.Show();

        // The bar: watch for the LED, then create the (hidden) window so the capture-exclusion state is known at once.
        _messages.DisplayChanged += _led.NotifyDisplayChanged;
        _led.Start();
        _overlay.Start();
        _logger.LogInformation(
            "LED: {Led} (modo de teste: {TestLed})",
            _led.Led is { } led ? $"{led.DeviceName} {led.Width}x{led.Height}" : "desligado",
            _led.TestMode);

        // The alerts: the saved queue, the bar, Telegram and the local API. The tray shows the pause whoever started it
        // (the tray itself, the panel, or Holyrics through the API).
        _runtime.Status.PropertyChanged += OnStatusChanged;
        _tray.SetPaused(_runtime.Status.Paused);
        _runtime.Start();

        _hotkey.Apply(_settings.Current.System.Hotkey);
        _settings.Changed += OnSettingsChanged;

        var registration = _startup.Apply(ex => _logger.LogWarning(ex, "Nao foi possivel ajustar o inicio com o Windows"));
        _logger.LogInformation("Inicio com o Windows: {Result}", registration);

        _logger.LogInformation("Alerta-IBT iniciado (bandeja: {Tray}, LED de teste: {TestLed})", _arguments.Tray, _arguments.TestLed);
        if (!_arguments.Tray)
        {
            RaiseMainWindow();
        }
    }

    /// <summary>
    /// Connects the tray icon to the app: click, double click, Pausar/Retomar and Sair. Part of <see cref="Start"/>; separate so a
    /// test can press the tray menu without registering a hotkey or showing an icon.
    /// </summary>
    internal void WireTray()
    {
        _tray.QuickPanelRequested += RaiseQuickPanel;
        _tray.MainWindowRequested += RaiseMainWindow;
        _tray.PauseToggleRequested += () =>
        {
            _runtime.TogglePause(); // the sermon starts or ends here, once; the event below only tells the others
            PauseToggleRequested?.Invoke();
        };
        _tray.ExitRequested += () => _ = ExitAsync();
    }

    /// <summary>Asks the main window to show; used for a second launch of the app.</summary>
    public void RaiseMainWindow()
    {
        _logger.LogDebug("Janela principal pedida");
        MainWindowRequested?.Invoke();
    }

    /// <summary>Asks, with a confirmation, to quit the app. Quitting stops the alerts on the LED, so a stray click must not do it.</summary>
    public async Task ExitAsync()
    {
        if (_exiting || _exitDialogOpen)
        {
            return;
        }

        _exitDialogOpen = true;
        var box = Views.ExitDialog.Create(_logger);

        Wpf.Ui.Controls.MessageBoxResult answer;
        try
        {
            answer = await box.ShowDialogAsync();
        }
        finally
        {
            _exitDialogOpen = false; // if the dialog fails, the tray's Sair still works the next time
        }

        if (answer == Wpf.Ui.Controls.MessageBoxResult.Primary)
        {
            _exiting = true;
            _logger.LogInformation("Saindo a pedido do usuario");
            Application.Current.Shutdown();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _settings.Changed -= OnSettingsChanged;
        _runtime.Status.PropertyChanged -= OnStatusChanged;
        _overlay.Dispose(); // first: it ends the pass in progress, so stopping the alert service does not wait for the UI thread
        _runtime.Dispose();
        _led.Dispose();
        _tray.Dispose();
        _hotkeys?.Dispose();
        _messages?.Dispose();
    }

    private void RaiseQuickPanel()
    {
        _hotkey?.Retry(); // the program that held the combination may have closed; the click always works either way
        QuickPanelRequested?.Invoke();
    }

    private void OnStatusChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(StatusHub.Paused))
        {
            return;
        }

        // The pause can start on any thread (the API, Telegram); the tray belongs to the UI thread.
        Application.Current?.Dispatcher.BeginInvoke(() => _tray.SetPaused(_runtime.Status.Paused));
    }

    private void OnSettingsChanged(AppSettings changed)
    {
        // The settings may change on any thread; the hotkey belongs to the UI thread's window.
        Application.Current?.Dispatcher.BeginInvoke(() => _hotkey?.Apply(changed.System.Hotkey));
    }

    private void OnHotkeyStatusChanged(HotkeyStatus status)
    {
        if (status.Registered)
        {
            _logger.LogInformation("Atalho do painel rapido: {Hotkey}", status.Text);
        }
        else
        {
            _logger.LogWarning("Atalho do painel rapido indisponivel: {Hotkey} ({Reason})", status.Text, status.Short);
        }

        _tray.SetHotkeyWarning(status.Short);
        HotkeyStatusChanged?.Invoke(status);

        // A saved value that could not be used was replaced by the default: save that back, so the tab shows what really works.
        if (status.FellBackFrom is not null)
        {
            _settings.Update(settings => settings with { System = settings.System with { Hotkey = status.Text } });
        }
    }
}
