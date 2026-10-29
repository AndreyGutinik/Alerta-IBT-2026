using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using AlertaIBT.App.Theme;
using AlertaIBT.App.ViewModels;

namespace AlertaIBT.App.Views;

/// <summary>
/// The Som tab: the player with its playlist and behaviour strip on top, then the alarms, then one bottom row with the start volumes and the audio
/// outputs. The page binds to <see cref="SomViewModel"/>; what it adds is the "box lost the focus" handling of the number and time boxes (they show
/// the value that was kept, never what could not be used), the wiring of the timeline (<see cref="SeekSlider"/>) to the player and the short fade of
/// the cover when the track changes (none when Windows animations are off).
/// </summary>
public partial class SomPage : UserControl
{
    private PlayerViewModel? _watched;

    /// <summary>Builds the page; the data context (a <see cref="SomViewModel"/>) is set by the window.</summary>
    public SomPage()
    {
        InitializeComponent();
        SeekSlider.SeekStarted += (_, _) => Player?.BeginSeek();
        SeekSlider.SeekMoved += (_, e) => Player?.UpdateSeek(e.Seconds);
        SeekSlider.SeekCommitted += (_, e) =>
        {
            Player?.UpdateSeek(e.Seconds);
            Player?.CommitSeek();
        };
        SeekSlider.SeekCancelled += (_, _) => Player?.CancelSeek();
        SeekSlider.SeekStepped += (_, e) => Player?.SeekBy(e.Seconds);
        DataContextChanged += (_, _) => WatchCover();
    }

    /// <summary>Whether the bottom row (volumes and outputs) is one column now (the window is narrow).</summary>
    public bool IsSingleColumn => BottomRow.Columns == 1;

    private PlayerViewModel? Player => (DataContext as SomViewModel)?.Player;

    // The cover changes with the track: it fades in (140 ms) instead of jumping.
    private void WatchCover()
    {
        if (_watched is not null)
        {
            _watched.PropertyChanged -= OnPlayerChanged;
        }

        _watched = Player;
        if (_watched is not null)
        {
            _watched.PropertyChanged += OnPlayerChanged;
        }
    }

    private void OnPlayerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PlayerViewModel.CoverArt))
        {
            Motion.FadeIn(CoverImage);
            Motion.FadeIn(CoverPlaceholder);
        }
    }

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
