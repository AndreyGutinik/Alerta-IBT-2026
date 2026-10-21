using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using AlertaIBT.App.ViewModels;

namespace AlertaIBT.App.Views;

/// <summary>
/// The Início tab (spec 7.2): what is on the bar, the message field with its preview, the queue and the history. The page only
/// binds to <see cref="HomeViewModel"/> and forwards the keys of the field.
/// </summary>
public partial class HomePage : UserControl
{
    /// <summary>Builds the page; the data context (a <see cref="HomeViewModel"/>) is set by the window.</summary>
    public HomePage()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    /// <summary>Puts the keyboard in the message field.</summary>
    public void FocusInput()
    {
        Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Input,
            () =>
            {
                InputBox.Focus();
                Keyboard.Focus(InputBox);
                InputBox.SelectAll();
            });
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        Detach(e.OldValue as HomeViewModel);
        if (e.NewValue is HomeViewModel home)
        {
            home.Composer.SendRejected += Shake;
            home.Composer.FocusInputRequested += FocusInput;
        }
    }

    private void Detach(HomeViewModel? home)
    {
        if (home is null)
        {
            return;
        }

        home.Composer.SendRejected -= Shake;
        home.Composer.FocusInputRequested -= FocusInput;
    }

    // Enter sends, Ctrl+Enter sends urgent, Shift+Enter is a new line, Esc clears the field.
    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not HomeViewModel home)
        {
            return;
        }

        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            home.Composer.ClearCommand.Execute(null);
            return;
        }

        if (e.Key != Key.Enter || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            return;
        }

        e.Handled = true;
        var command = Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ? home.Composer.SendUrgentCommand : home.Composer.SendCommand;
        command.Execute(null);
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
        ShakeTransform.BeginAnimation(TranslateTransform.XProperty, shake);
    }
}
