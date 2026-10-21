using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AlertaIBT.App.ViewModels;

namespace AlertaIBT.App.Views;

/// <summary>The Correção tab (spec 7.2): the correction switch, Gemini or "Só offline", the Gemini key, the model and the fixed rules.</summary>
public partial class CorrectionPage : UserControl
{
    /// <summary>Builds the page; the data context (a <see cref="CorrectionViewModel"/>) is set by the window.</summary>
    public CorrectionPage()
    {
        InitializeComponent();
    }

    // "Trocar" opens the key box: the keyboard goes into it so a paste works at once. (Click comes before the command runs, so the
    // box is looked at afterwards.)
    private void OnKeyLockClick(object sender, RoutedEventArgs e)
    {
        Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Input,
            () =>
            {
                if (DataContext is CorrectionViewModel { GeminiKey.IsEditing: true })
                {
                    KeyEntryBox.Focus();
                    Keyboard.Focus(KeyEntryBox);
                }
            });
    }
}
