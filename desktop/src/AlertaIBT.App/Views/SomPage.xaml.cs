using System.Windows;
using System.Windows.Controls;
using AlertaIBT.App.ViewModels;

namespace AlertaIBT.App.Views;

/// <summary>
/// The Som tab: the volumes, the routing of the outputs, the music player and the alarms. The page binds to <see cref="SomViewModel"/>; what it
/// adds is the "box lost the focus" handling of the number and time boxes (they show the value that was kept, never what could not be used).
/// </summary>
public partial class SomPage : UserControl
{
    /// <summary>Builds the page; the data context (a <see cref="SomViewModel"/>) is set by the window.</summary>
    public SomPage() => InitializeComponent();

    // A volume box lost the focus: it shows the value that was kept.
    private void OnFieldLostFocus(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is IntegerField number)
        {
            number.Normalize();
        }
    }

    private void OnStartLostFocus(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is AlarmRow row)
        {
            row.NormalizeStart();
        }
    }

    private void OnLatestLostFocus(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is AlarmRow row)
        {
            row.NormalizeLatest();
        }
    }
}
