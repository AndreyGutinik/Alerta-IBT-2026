using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using AlertaIBT.App.Platform;
using AlertaIBT.App.ViewModels;

namespace AlertaIBT.App.Tray;

/// <summary>
/// The quick panel (spec 7.1): a small card that opens above the tray, takes the keyboard for the field, and closes on Esc, on
/// a click anywhere else, or when the same hotkey/tray click is used again. It is not a taskbar window and never stays open.
/// </summary>
public partial class QuickPanel : Window, IQuickPanelWindow
{
    private static readonly TimeSpan ProgressTick = TimeSpan.FromMilliseconds(100);

    private readonly QuickPanelViewModel _viewModel;
    private readonly AppWindowPolicy _policy;
    private readonly DispatcherTimer _progress;
    private IntPtr _previousForeground;

    /// <summary>Whether the keyboard focus is in the text box (a test replaces it: a window that is not shown never has the focus).</summary>
    internal Func<bool> InputHasFocus { get; set; } = () => false;

    /// <param name="viewModel">The logic of the panel; the window binds to it.</param>
    /// <param name="policy">Where windows may open (never the LED) and the capture exclusion; a plain one when omitted.</param>
    public QuickPanel(QuickPanelViewModel viewModel, AppWindowPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        _viewModel = viewModel;
        _policy = policy ?? AppWindowPolicy.Default;
        DataContext = viewModel;
        InitializeComponent();

        // The handle is made now, hidden: the panel is out of every screen capture before its first pixel (the LED is captured).
        _policy.Protect(this);

        _progress = new DispatcherTimer(ProgressTick, DispatcherPriority.Background, (_, _) => _viewModel.RefreshProgress(), Dispatcher);
        _progress.Stop();

        _viewModel.FocusInputRequested += FocusInput;
        _viewModel.SendRejected += Shake;
        Deactivated += (_, _) => HideCore(restoreFocus: false); // the user clicked somewhere else: that window already has the focus
        InputHasFocus = () => InputBox.IsKeyboardFocused;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    /// <inheritdoc />
    public event Action? Hidden;

    /// <inheritdoc />
    public void ShowPanel()
    {
        _previousForeground = QuickPanelFocus.WindowToRestore(NativeMethods.GetForegroundWindow(), new WindowInteropHelper(this).Handle);

        Opacity = 0;
        Show();
        PositionNearTray();
        _policy.Reevaluate(this); // moved by code: the move events may not have fired yet
        Activate();
        NativeMethods.SetForegroundWindow(new WindowInteropHelper(this).Handle);
        AnimateIn();
        _progress.Start();
        _viewModel.RefreshProgress();
        FocusInput();
    }

    /// <inheritdoc />
    public void HidePanel() => HideCore(restoreFocus: true);

    /// <inheritdoc />
    protected override void OnClosing(CancelEventArgs e)
    {
        // The panel is reused: closing it only hides it (unless the app is quitting).
        if (!Dispatcher.HasShutdownStarted)
        {
            e.Cancel = true;
            HideCore(restoreFocus: true);
        }

        base.OnClosing(e);
    }

    private void HideCore(bool restoreFocus)
    {
        if (!IsVisible)
        {
            return;
        }

        var handle = new WindowInteropHelper(this).Handle;
        var wasForeground = NativeMethods.GetForegroundWindow() == handle;
        _progress.Stop();
        Hide();
        if (restoreFocus && wasForeground && _previousForeground != IntPtr.Zero && NativeMethods.IsWindow(_previousForeground))
        {
            // Esc, a send or the hotkey: hand the keyboard back to Holyrics/OBS/whatever was in front.
            NativeMethods.SetForegroundWindow(_previousForeground);
        }

        _previousForeground = IntPtr.Zero;
        Hidden?.Invoke();
    }

    private void OnCopyImage(object sender, RoutedEventArgs e) => CopyPanelImage();

    /// <summary>
    /// Puts a picture of the panel (the card, without the shadow margin) on the clipboard, so it can be pasted in a chat: the tray panel closes when
    /// another window (the Win+Shift+S overlay) takes the focus, so a screenshot of it by hotkey is not reliable. Works whatever the capture rule says.
    /// </summary>
    /// <returns>Whether the picture was copied (the clipboard may be locked by another program).</returns>
    internal bool CopyPanelImage()
    {
        if (Card.ActualWidth < 1 || Card.ActualHeight < 1)
        {
            return false;
        }

        try
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            var width = Card.ActualWidth;
            var height = Card.ActualHeight;
            var visual = new DrawingVisual();
            using (var context = visual.RenderOpen())
            {
                context.DrawRectangle(new VisualBrush(Card) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, new Rect(0, 0, width, height));
            }

            var bitmap = new RenderTargetBitmap(
                (int)Math.Ceiling(width * dpi.DpiScaleX),
                (int)Math.Ceiling(height * dpi.DpiScaleY),
                96 * dpi.DpiScaleX,
                96 * dpi.DpiScaleY,
                PixelFormats.Pbgra32);
            bitmap.Render(visual);
            Clipboard.SetImage(bitmap);
            CopyImageButton.ToolTip = "Imagem copiada: cole com Ctrl+V";
            return true;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException or InvalidOperationException)
        {
            CopyImageButton.ToolTip = "N\U000000E3o consegui copiar: tente de novo";
            return false;
        }
    }

    private void FocusInput()
    {
        // After the window is active: focus the field and select the old draft, so typing replaces it.
        Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            () =>
            {
                InputBox.Focus();
                Keyboard.Focus(InputBox);
                InputBox.SelectAll();
            });
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            _viewModel.Escape();
        }
    }

    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        // Left/Right while the owner types an alert are slide back/next in Holyrics (only here, only with the field focused, never global). A key with a
        // modifier (Shift+Left selects text, Ctrl+Left jumps a word) stays a normal key.
        if (e.Key is Key.Left or Key.Right
            && InputHasFocus()
            && Keyboard.Modifiers == ModifierKeys.None
            && _viewModel.TryForwardArrow(e.Key == Key.Right, e.IsRepeat))
        {
            e.Handled = true;
            return;
        }

        if (e.Key != Key.Enter || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            return; // Shift+Enter is a new line
        }

        e.Handled = true;
        var command = Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ? _viewModel.SendUrgentCommand : _viewModel.SendCommand;
        command.Execute(null);
    }

    // Bottom right of the work area of the monitor chosen by the policy (the one under the mouse, but never the LED), 0 px from
    // the edge: the 12 px of shadow margin of the window are the visible gap. That is where the tray lives.
    internal void PositionNearTray() => _policy.PlaceBottomRight(new WindowInteropHelper(this).Handle);

    private void AnimateIn()
    {
        if (!SystemParameters.ClientAreaAnimation)
        {
            Opacity = 1;
            return;
        }

        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    private void Shake()
    {
        if (!SystemParameters.ClientAreaAnimation)
        {
            return;
        }

        var shake = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(400) };
        shake.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(0)));
        shake.KeyFrames.Add(new LinearDoubleKeyFrame(-5, KeyTime.FromPercent(0.25)));
        shake.KeyFrames.Add(new LinearDoubleKeyFrame(5, KeyTime.FromPercent(0.75)));
        shake.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(1)));
        ShakeTransform.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, shake);
    }
}
