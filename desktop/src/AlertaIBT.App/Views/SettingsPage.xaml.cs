using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AlertaIBT.App.ViewModels;
using AlertaIBT.Core.Input;

namespace AlertaIBT.App.Views;

/// <summary>
/// The Configurações tab (spec 7.2): the bar with its live preview and the 3-second test, Telegram, the shortcut and the
/// Diagnóstico area. The page binds to <see cref="SettingsViewModel"/>; what it adds is the capture of the shortcut keys, the focus
/// after "Trocar" and the width of the sample text for the speed label.
/// </summary>
public partial class SettingsPage : UserControl
{
    /// <summary>Builds the page; the data context (a <see cref="SettingsViewModel"/>) is set by the window.</summary>
    public SettingsPage()
    {
        InitializeComponent();
        Preview.SampleWidthMeasured += width => (DataContext as SettingsViewModel)?.ReportSampleWidth(width);
        DataContextChanged += (_, e) =>
        {
            if (e.NewValue is SettingsViewModel settings)
            {
                settings.ReportSampleWidth(Preview.TextWidth);
            }
        };
    }

    // A numeric or color box lost the focus: it shows the value that was kept, never what could not be used.
    private void OnFieldLostFocus(object sender, RoutedEventArgs e)
    {
        switch ((sender as FrameworkElement)?.Tag)
        {
            case IntegerField number:
                number.Normalize();
                break;
            case ColorField color:
                color.Normalize();
                break;
        }
    }

    // "Trocar" opens the token box: the keyboard goes into it so a paste works at once. (Click comes before the command runs, so the
    // box is looked at afterwards.)
    private void OnTokenLockClick(object sender, RoutedEventArgs e)
    {
        Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Input,
            () =>
            {
                if (DataContext is SettingsViewModel { Token.IsEditing: true })
                {
                    TokenEntryBox.Focus();
                    Keyboard.Focus(TokenEntryBox);
                }
            });
    }

    // The shortcut box takes the combination that is pressed in it.
    private void OnHotkeyKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not SettingsViewModel settings)
        {
            return;
        }

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var modifiers = ToModifiers(Keyboard.Modifiers);
        if (key == Key.Tab && modifiers == HotkeyModifiers.None)
        {
            return; // leaving the box by keyboard still works
        }

        e.Handled = true;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin
            or Key.None or Key.DeadCharProcessed or Key.ImeProcessed)
        {
            return; // a modifier on its own is not a shortcut yet
        }

        if (settings.TryCaptureHotkey(modifiers, KeyInterop.VirtualKeyFromKey(key)))
        {
            Keyboard.ClearFocus();
        }
    }

    private static HotkeyModifiers ToModifiers(ModifierKeys keys)
    {
        var modifiers = HotkeyModifiers.None;
        if (keys.HasFlag(ModifierKeys.Control))
        {
            modifiers |= HotkeyModifiers.Control;
        }

        if (keys.HasFlag(ModifierKeys.Alt))
        {
            modifiers |= HotkeyModifiers.Alt;
        }

        if (keys.HasFlag(ModifierKeys.Shift))
        {
            modifiers |= HotkeyModifiers.Shift;
        }

        if (keys.HasFlag(ModifierKeys.Windows))
        {
            modifiers |= HotkeyModifiers.Win;
        }

        return modifiers;
    }
}
