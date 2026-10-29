using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using AlertaIBT.App.Platform;
using AlertaIBT.App.ViewModels;
using Wpf.Ui.Controls;

namespace AlertaIBT.App.Views;

/// <summary>The main window as the controller sees it, so the open rules are tested without a screen.</summary>
public interface IMainWindow
{
    /// <summary>Whether the window is on screen.</summary>
    bool IsVisible { get; }

    /// <summary>Shows the window in front, restored if it was minimized, and tells the view-model it was shown.</summary>
    void ShowWindow();
}

/// <summary>
/// The main window (spec 7.2): the side bar with the three tabs and the status list, and the tabs themselves. Closing it only hides
/// it (the app lives in the tray); a second launch, the tray's double click and "Abrir janela" bring it back.
/// </summary>
public partial class MainWindow : FluentWindow, IMainWindow
{
    private static readonly TimeSpan ProgressTick = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan LogTick = TimeSpan.FromSeconds(3);

    private readonly MainViewModel _viewModel;
    private readonly AppWindowPolicy _policy;
    private bool _placed;
    private readonly DispatcherTimer _progress;
    private readonly DispatcherTimer _logs;

    /// <param name="viewModel">The logic of the window; the window binds to it.</param>
    /// <param name="policy">Where windows may open (never the LED) and the capture exclusion; a plain one when omitted.</param>
    public MainWindow(MainViewModel viewModel, AppWindowPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        _viewModel = viewModel;
        _policy = policy ?? AppWindowPolicy.Default;
        DataContext = viewModel;
        InitializeComponent();

        // The handle is made now, hidden: the window is out of every screen capture before its first pixel (the LED is captured).
        _policy.Exclude(this);

        // The timers only run while the window is on screen.
        _progress = new DispatcherTimer(ProgressTick, DispatcherPriority.Background, (_, _) => _viewModel.Home.Composer.RefreshProgress(), Dispatcher);
        _logs = new DispatcherTimer(LogTick, DispatcherPriority.Background, (_, _) => _viewModel.Settings.Diagnostics.Refresh(), Dispatcher);
        _progress.Stop();
        _logs.Stop();
        IsVisibleChanged += (_, _) => UpdateTimers();

        // The logo in the title bar and the side bar: the icon frame that matches the pixels drawn (16 and 24 dips), not a stretched one.
        Loaded += (_, _) => UpdateLogos();
        DpiChanged += (_, _) => UpdateLogos();
    }

    /// <summary>The Início page (for tests).</summary>
    internal HomePage Home => HomePage;

    /// <summary>The correction card, which sits in the Início page (for tests).</summary>
    internal CorrectionPanel Correction => HomePage.CorrectionPanel;

    /// <summary>The Configurações page (for tests).</summary>
    internal SettingsPage Settings => SettingsPage;

    /// <summary>The Som page (for tests).</summary>
    internal SomPage Som => SomPage;

    /// <summary>The NDI page (for tests).</summary>
    internal NdiPage Ndi => NdiPage;

    /// <inheritdoc />
    public void ShowWindow()
    {
        if (!IsVisible)
        {
            PlaceBeforeShow();
            Show();
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        // Windows only lets the foreground program come to the front; flipping Topmost is the usual way for a second launch to
        // bring the window up without stealing the keyboard in a hostile way.
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
        _viewModel.OnShown();
    }

    /// <inheritdoc />
    protected override void OnClosing(CancelEventArgs e)
    {
        // The window is reused: the X only hides it (unless the app is quitting).
        if (!Dispatcher.HasShutdownStarted)
        {
            e.Cancel = true;
            Hide();
        }

        base.OnClosing(e);
    }

    /// <summary>
    /// Centered on the first opening, and again whenever it would come up on the LED: always on a monitor that is not the LED
    /// (unless the LED is the only monitor, where the window is hidden from capture anyway). Done on the hidden window, so it is
    /// never seen in the wrong place.
    /// </summary>
    internal void PlaceBeforeShow()
    {
        // It opens at about 1100 x 860; on a screen that is not that tall it is as tall as the work area (never off the bottom).
        if (!_placed)
        {
            Height = Math.Max(MinHeight, Math.Min(Height, SystemParameters.WorkArea.Height));
        }

        _policy.KeepOffLed(this, neverPlaced: !_placed);
        _placed = true;
    }

    private void UpdateLogos()
    {
        var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        TitleLogo.Source = Theme.AppIcon.Frame(16, scale);
        SidebarLogo.Source = Theme.AppIcon.Frame(24, scale);
        RenderOptions.SetBitmapScalingMode(SidebarLogo, BitmapScalingMode.HighQuality);
    }

    private void UpdateTimers()
    {
        if (IsVisible)
        {
            _progress.Start();
            _logs.Start();
        }
        else
        {
            _progress.Stop();
            _logs.Stop();
        }
    }
}
