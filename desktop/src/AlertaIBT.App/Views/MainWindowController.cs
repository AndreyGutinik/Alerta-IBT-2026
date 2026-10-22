using AlertaIBT.App.ViewModels;
using Microsoft.Extensions.Logging;

namespace AlertaIBT.App.Views;

/// <summary>
/// Opens the main window (spec 7.2) when the shell asks for it: the tray's double click and menu "Abrir", "Abrir janela" of the
/// quick panel, a second launch of the app, or the first run after an installation. The window is made on first use, on the UI thread, and then
/// reused (its X only hides it). Also the owner of the window's view-model: disposing the controller saves a setting that was still
/// waiting for its 600 ms.
/// </summary>
public sealed class MainWindowController : IDisposable
{
    private readonly Func<IMainWindow> _createWindow;
    private readonly ILogger<MainWindowController> _logger;
    private IMainWindow? _window;

    /// <param name="viewModel">The logic the window binds to.</param>
    /// <param name="createWindow">Makes the window; called once, on the UI thread, when it is first wanted.</param>
    /// <param name="logger">Receives failures to show the window.</param>
    public MainWindowController(MainViewModel viewModel, Func<IMainWindow> createWindow, ILogger<MainWindowController> logger)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(createWindow);
        ArgumentNullException.ThrowIfNull(logger);

        ViewModel = viewModel;
        _createWindow = createWindow;
        _logger = logger;
    }

    /// <summary>The window's view-model.</summary>
    public MainViewModel ViewModel { get; }

    /// <summary>Whether the window is on screen right now.</summary>
    public bool IsOpen => _window?.IsVisible ?? false;

    /// <summary>Wires the shell: a request for the main window shows it, and the state of the global shortcut shows in Configurações.</summary>
    public void Attach(AppShell shell)
    {
        ArgumentNullException.ThrowIfNull(shell);

        shell.MainWindowRequested += Show;
        shell.HotkeyStatusChanged += ViewModel.Settings.ApplyHotkey;
        ViewModel.Settings.ApplyHotkey(shell.HotkeyStatus);
    }

    /// <summary>Shows the window in front (making it the first time). A failure is logged, never thrown: the app lives in the tray.</summary>
    public void Show()
    {
        try
        {
            var window = _window ??= _createWindow();
            window.ShowWindow();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Nao foi possivel abrir a janela principal");
        }
    }

    /// <summary>Saves what the settings tabs still hold and lets go of the view-model.</summary>
    public void Dispose()
    {
        try
        {
            ViewModel.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro ao fechar a janela principal");
        }
    }
}
