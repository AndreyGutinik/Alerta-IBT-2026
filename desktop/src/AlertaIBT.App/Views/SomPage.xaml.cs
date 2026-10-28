using System.Windows;
using System.Windows.Controls;
using AlertaIBT.App.ViewModels;

namespace AlertaIBT.App.Views;

/// <summary>
/// <summary>
/// The Som tab: the player card on top, then the volumes, the routing of the outputs, the playlists, the alarms and the behaviour of the player.
/// The page binds to <see cref="SomViewModel"/>; what it adds is the "box lost the focus" handling of the number and time boxes (they show the
/// value that was kept, never what could not be used) and the wiring of the timeline (<see cref="SeekSlider"/>) to the player.
/// </summary>
public partial class SomPage : UserControl
{
    /// <summary>Under this width the two columns of cards become one (the rows would only stack their controls under their titles).</summary>
    public const double SingleColumnBelow = 800;

    private bool _singleColumn;

    /// <summary>Builds the page; the data context (a <see cref="SomViewModel"/>) is set by the window.</summary>
    public SomPage()
    {
        InitializeComponent();
        SettingsGrid.SizeChanged += (_, e) => ArrangeColumns(e.NewSize.Width);
        SeekSlider.SeekStarted += (_, _) => Player?.BeginSeek();
        SeekSlider.SeekMoved += (_, e) => Player?.UpdateSeek(e.Seconds);
        SeekSlider.SeekCommitted += (_, e) =>
        {
            Player?.UpdateSeek(e.Seconds);
            Player?.CommitSeek();
        };
        SeekSlider.SeekCancelled += (_, _) => Player?.CancelSeek();
        SeekSlider.SeekStepped += (_, e) => Player?.SeekBy(e.Seconds);
    }

    /// <summary>Whether the cards are in one column now (the window is narrow).</summary>
    public bool IsSingleColumn => _singleColumn;

    // Two columns of cards; narrow, one column: the left stack first, the right stack under it.
    private void ArrangeColumns(double width)
    {
        var single = width < SingleColumnBelow;
        if (single == _singleColumn)
        {
            return;
        }

        _singleColumn = single;
        Grid.SetColumnSpan(LeftColumn, single ? 3 : 1);
        Grid.SetColumn(RightColumn, single ? 0 : 2);
        Grid.SetColumnSpan(RightColumn, single ? 3 : 1);
        Grid.SetRow(RightColumn, single ? 1 : 0);
    }

    private PlayerViewModel? Player => (DataContext as SomViewModel)?.Player;

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
