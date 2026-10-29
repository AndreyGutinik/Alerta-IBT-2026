using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AlertaIBT.App.ViewModels;

namespace AlertaIBT.App.Views;

/// <summary>
/// The correction (spec 7.2) as a folded card of the Início tab: the correction switch, the Gemini key with the Gemini and "Só offline" icons at its
/// right, and the fixed rules. The model of Gemini is a card of Configurações.
/// </summary>
public partial class CorrectionPanel : UserControl
{
    /// <summary>Builds the card; the data context (a <see cref="CorrectionViewModel"/>) is set by the Início page.</summary>
    public CorrectionPanel()
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
