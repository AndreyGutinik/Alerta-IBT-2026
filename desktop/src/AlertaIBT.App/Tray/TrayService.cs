using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Shapes;
using AlertaIBT.App.Theme;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using MenuItem = System.Windows.Controls.MenuItem;
using Separator = System.Windows.Controls.Separator;

namespace AlertaIBT.App.Tray;

/// <summary>
/// The notification-area icon. A click asks for the quick panel, a double click for the main window; the right-click menu
/// has Abrir, Pausar/Retomar and Sair. The service only raises events: what each one does is decided by
/// <see cref="AppShell"/> and the windows that listen to it.
/// </summary>
public sealed class TrayService : IDisposable
{
    private const string AppName = "IBT Controller";

    private readonly TaskbarIcon _icon;
    private readonly MenuItem _pauseItem;
    private readonly MenuItem _exitItem;
    private readonly MenuItem _openItem;
    private readonly MenuItem _mouseLockItem;
    private bool _paused;
    private bool _mouseLocked;
    private string? _hotkeyWarning;
    private string? _updateNotice;

    /// <summary>Creates the icon (not yet visible; call <see cref="Show"/>). Needs the WPF application resources and the UI thread.</summary>
    public TrayService()
    {
        _openItem = new MenuItem { Header = "Abrir", FontWeight = FontWeights.SemiBold, Icon = LucideIcon("LucidePanelTop") };
        _openItem.Click += (_, _) => MainWindowRequested?.Invoke();

        _pauseItem = new MenuItem { Header = PauseHeader(paused: false), Icon = LucideIcon("LucidePause") };
        _pauseItem.Click += (_, _) => PauseToggleRequested?.Invoke();

        _mouseLockItem = new MenuItem
        {
            Header = MouseLockHeader,
            IsCheckable = true,
            Icon = LucideIcon("LucideLock"),
        };
        _mouseLockItem.Click += (_, _) =>
        {
            MouseLockToggleRequested?.Invoke();
            _mouseLockItem.IsChecked = _mouseLocked; // the check shows what really happened (the lock may have been refused)
        };

        _exitItem = new MenuItem { Header = "Sair", Icon = LucideIcon("LucidePower") };
        _exitItem.Click += (_, _) => ExitRequested?.Invoke();

        Menu = new ContextMenu();
        Menu.Opened += (_, _) => ExcludeFromCapture(Menu); // the menu is a window of its own, at the taskbar
        Menu.Items.Add(_openItem);
        Menu.Items.Add(_pauseItem);
        Menu.Items.Add(_mouseLockItem);
        Menu.Items.Add(new Separator());
        Menu.Items.Add(_exitItem);

        LeftClickCommand = new ActionCommand(() => QuickPanelRequested?.Invoke());
        DoubleClickCommand = new ActionCommand(() => MainWindowRequested?.Invoke());

        _icon = new TaskbarIcon
        {
            ToolTipText = AppName,
            MenuActivation = PopupActivationMode.RightClick,
            ContextMenu = Menu,

            // With the click delay on (the default) a double click does not first fire the single-click action.
            NoLeftClickDelay = false,
            LeftClickCommand = LeftClickCommand,
            DoubleClickCommand = DoubleClickCommand,
        };
    }

    /// <summary>Raised on a single click of the icon: show the quick panel.</summary>
    public event Action? QuickPanelRequested;

    /// <summary>Raised on a double click of the icon and by the menu's Abrir: show the main window.</summary>
    public event Action? MainWindowRequested;

    /// <summary>Raised by the menu's Pausar/Retomar.</summary>
    public event Action? PauseToggleRequested;

    /// <summary>Raised by the menu's "Travar o mouse na tela principal".</summary>
    public event Action? MouseLockToggleRequested;

    /// <summary>Raised by the menu's Sair.</summary>
    public event Action? ExitRequested;

    /// <summary>The text of the mouse-lock menu item.</summary>
    public const string MouseLockHeader = "Travar o mouse na tela principal";

    /// <summary>The tooltip suffix while the mouse is locked.</summary>
    public const string MouseLockedSuffix = "mouse travado";

    /// <summary>The right-click menu, so a later part of the app can add an item.</summary>
    public ContextMenu Menu { get; }

    /// <summary>What the icon does on a single click. Exposed so the wiring can be tested without a real taskbar.</summary>
    internal ICommand LeftClickCommand { get; }

    /// <summary>What the icon does on a double click.</summary>
    internal ICommand DoubleClickCommand { get; }

    /// <summary>The text of the menu's pause item right now (<c>Pausar</c> or <c>Retomar</c>).</summary>
    public string PauseItemText => _pauseItem.Header as string ?? string.Empty;

    /// <summary>Puts the icon in the notification area.</summary>
    public void Show()
    {
        _icon.Icon = AppIcon.LoadTrayIcon(); // the frame that matches this PC's tray size, not the 256 one shrunk by Windows
        _icon.ForceCreate(enablesEfficiencyMode: false);
    }

    /// <summary>Shows the paused state: the menu item becomes Retomar and the tooltip says so.</summary>
    public void SetPaused(bool paused)
    {
        _paused = paused;
        _pauseItem.Header = PauseHeader(paused);
        _pauseItem.Icon = LucideIcon(paused ? "LucidePlay" : "LucidePause");
        UpdateToolTip();
    }

    /// <summary>Shows whether the mouse is locked to the primary monitor: the menu item is checked and the tooltip says so.</summary>
    public void SetMouseLocked(bool locked)
    {
        _mouseLocked = locked;
        _mouseLockItem.IsChecked = locked;
        UpdateToolTip();
    }

    /// <summary>
    /// Tells the tooltip that the quick-panel hotkey does not work (<c>null</c> when it does), so the user finds out without
    /// opening anything; the click on the icon always opens the panel.
    /// </summary>
    public void SetHotkeyWarning(string? warning)
    {
        _hotkeyWarning = string.IsNullOrWhiteSpace(warning) ? null : warning;
        UpdateToolTip();
    }

    /// <summary>
    /// Puts the update line in the tooltip while an update downloads or installs (<c>null</c> when none), so the operator sees why the
    /// app is about to restart even with the window closed.
    /// </summary>
    public void SetUpdateNotice(string? notice)
    {
        _updateNotice = string.IsNullOrWhiteSpace(notice) ? null : notice;
        UpdateToolTip();
    }

    /// <summary>The tooltip of the icon right now: the name, <c>- pausado</c> while paused, the hotkey warning and the update line if any.</summary>
    public string ToolTip => _icon.ToolTipText;

    /// <summary>Whether the icon shows the paused state.</summary>
    public bool IsPaused => _paused;

    /// <summary>Whether the icon shows the mouse lock on (the menu item is checked).</summary>
    public bool IsMouseLocked => _mouseLocked;

    /// <summary>The mouse-lock menu item, for tests.</summary>
    internal MenuItem MouseLockItem => _mouseLockItem;

    /// <summary>A balloon from the icon. Failing to show one never matters.</summary>
    public void ShowNotification(string title, string message)
    {
        try
        {
            _icon.ShowNotification(title, message, NotificationIcon.Info);
        }
        catch (Exception)
        {
            // A notification is a courtesy: it never takes the app down.
        }
    }

    /// <inheritdoc />
    public void Dispose() => _icon.Dispose();

    // The popup of the menu has its own handle, made when it opens: it never shows in a capture either (best effort: it opens at
    // the notification area, which is not the LED).
    private static void ExcludeFromCapture(ContextMenu menu)
    {
        if (System.Windows.PresentationSource.FromVisual(menu) is System.Windows.Interop.HwndSource source)
        {
            Platform.CaptureExclusion.Apply(source.Handle);
        }
    }

    // A Lucide icon for a menu item; null when the app resources are not there (a bare test), which just means no icon.
    private static Path? LucideIcon(string geometryKey)
    {
        var resources = Application.Current?.Resources;
        if (resources?[geometryKey] is not System.Windows.Media.Geometry geometry)
        {
            return null;
        }

        var path = new Path { Data = geometry, Width = 16, Height = 16 };
        if (resources["LucideIcon"] is Style style)
        {
            path.Style = style;
        }

        return path;
    }

    private void UpdateToolTip()
    {
        var text = _paused ? $"{AppName} - pausado" : AppName;
        if (_mouseLocked)
        {
            text += $" - {MouseLockedSuffix}";
        }

        if (_hotkeyWarning is not null)
        {
            text += $"\n{_hotkeyWarning}";
        }

        if (_updateNotice is not null)
        {
            text += $"\n{_updateNotice}";
        }

        _icon.ToolTipText = text;
    }

    private static string PauseHeader(bool paused) => paused ? "Retomar" : "Pausar";

    private sealed class ActionCommand(Action action) : ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => action();
    }
}
