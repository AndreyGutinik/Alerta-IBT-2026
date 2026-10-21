using AlertaIBT.App.ViewModels;
using Microsoft.Extensions.Logging;

namespace AlertaIBT.App.Tray;

/// <summary>The quick-panel window as the controller sees it, so the open/close rules are tested without a screen.</summary>
public interface IQuickPanelWindow
{
    /// <summary>Whether the panel is on screen.</summary>
    bool IsVisible { get; }

    /// <summary>Raised when the panel went away, whatever the reason (Esc, a click elsewhere, the hotkey, a send).</summary>
    event Action? Hidden;

    /// <summary>Shows the panel near the tray, takes the keyboard focus for the field and remembers what had the focus before.</summary>
    void ShowPanel();

    /// <summary>Hides the panel; gives the focus back to the program that had it unless the user already went elsewhere.</summary>
    void HidePanel();
}

/// <summary>
/// Opens and closes the quick panel (spec 7.1): one click on the tray icon or the hotkey toggles it, and it closes by itself
/// when it loses the focus. The window is created on first use, on the UI thread.
/// </summary>
public sealed class QuickPanelController
{
    /// <summary>
    /// A click on the tray icon first takes the focus away from the open panel (which closes it) and then arrives as a click: this
    /// long after the panel closed, a request to open is that same click and is ignored, so the click closes the panel.
    /// </summary>
    public static readonly TimeSpan ReopenGuard = TimeSpan.FromMilliseconds(300);

    private readonly Func<IQuickPanelWindow> _createWindow;
    private readonly TimeProvider _clock;
    private readonly ILogger<QuickPanelController> _logger;
    private IQuickPanelWindow? _window;
    private long? _hiddenAt;

    /// <param name="viewModel">The logic the window binds to.</param>
    /// <param name="createWindow">Makes the window; called once, on the UI thread, when the panel is first wanted.</param>
    /// <param name="logger">Receives failures to show the panel.</param>
    /// <param name="clock">The time source of <see cref="ReopenGuard"/>.</param>
    public QuickPanelController(
        QuickPanelViewModel viewModel,
        Func<IQuickPanelWindow> createWindow,
        ILogger<QuickPanelController> logger,
        TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(createWindow);
        ArgumentNullException.ThrowIfNull(logger);

        ViewModel = viewModel;
        _createWindow = createWindow;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>The panel's view-model.</summary>
    public QuickPanelViewModel ViewModel { get; }

    /// <summary>Whether the panel is on screen right now.</summary>
    public bool IsOpen => _window?.IsVisible ?? false;

    /// <summary>Wires the shell: the tray click and the hotkey toggle the panel, "Abrir janela" opens the main window, and the hotkey state shows in the panel.</summary>
    public void Attach(AppShell shell)
    {
        ArgumentNullException.ThrowIfNull(shell);

        shell.QuickPanelRequested += Toggle;
        shell.HotkeyStatusChanged += ViewModel.ApplyHotkey;
        ViewModel.OpenMainWindowRequested += shell.RaiseMainWindow;
        ViewModel.CloseRequested += Close;
        ViewModel.ApplyHotkey(shell.HotkeyStatus);
    }

    /// <summary>Opens the panel if it is closed, closes it if it is open.</summary>
    public void Toggle()
    {
        if (IsOpen)
        {
            Close();
            return;
        }

        if (_hiddenAt is { } at && _clock.GetElapsedTime(at) < ReopenGuard)
        {
            return;
        }

        Open();
    }

    /// <summary>Opens the panel (nothing happens when it is already open). A failure is logged, never thrown: the app lives in the tray.</summary>
    public void Open()
    {
        try
        {
            var window = _window ??= Create();
            if (!window.IsVisible)
            {
                ViewModel.OnShown();
                window.ShowPanel();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Nao foi possivel abrir o painel rapido");
        }
    }

    /// <summary>Closes the panel.</summary>
    public void Close()
    {
        try
        {
            _window?.HidePanel();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Nao foi possivel fechar o painel rapido");
        }
    }

    private IQuickPanelWindow Create()
    {
        var window = _createWindow();
        window.Hidden += () => _hiddenAt = _clock.GetTimestamp();
        return window;
    }
}
