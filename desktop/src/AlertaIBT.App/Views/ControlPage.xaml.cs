using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AlertaIBT.App.ViewModels;

namespace AlertaIBT.App.Views;

/// <summary>
/// The Controle tab. The page binds to <see cref="ControlViewModel"/>; what it adds is the life of the tab: while it is on screen the live levels of the
/// channels are read about 15 times a second, and when the tab (or the window) is hidden the reading stops.
/// </summary>
public partial class ControlPage : UserControl
{
    private static readonly TimeSpan LiveTick = TimeSpan.FromMilliseconds(66);

    private readonly DispatcherTimer _live;
    private ControlViewModel? _watched;

    /// <summary>Builds the page; the data context (a <see cref="ControlViewModel"/>) is set by the window.</summary>
    public ControlPage()
    {
        InitializeComponent();
        _live = new DispatcherTimer(LiveTick, DispatcherPriority.Background, (_, _) => ViewModel?.Tick(), Dispatcher);
        _live.Stop();
        IsVisibleChanged += (_, _) => UpdateActive();
        DataContextChanged += (_, _) =>
        {
            UpdateActive();
            SetCompact(RowList.ActualWidth);
        };
        Unloaded += (_, _) => Deactivate();
        RowList.SizeChanged += (_, e) => SetCompact(e.NewSize.Width);
    }

    /// <summary>Whether the live reading runs now (for tests).</summary>
    internal bool IsLive => _live.IsEnabled;

    /// <summary>The list is narrower than this many pixels: a line of the map takes two lines.</summary>
    internal const double CompactBelow = 760;

    private void SetCompact(double listWidth)
    {
        if (ViewModel is { } vm && listWidth > 0)
        {
            vm.Compact = listWidth < CompactBelow;
        }
    }

    private ControlViewModel? ViewModel => DataContext as ControlViewModel;

    private void OnFieldLostFocus(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is IntegerField number)
        {
            number.Normalize();
        }
    }

    private void UpdateActive()
    {
        if (!ReferenceEquals(_watched, ViewModel))
        {
            Deactivate();
            _watched = ViewModel;
        }

        if (IsVisible && _watched is not null)
        {
            _watched.SetActive(true);
            _live.Start();
        }
        else
        {
            Deactivate();
        }
    }

    private void Deactivate()
    {
        _live.Stop();
        _watched?.SetActive(false);
    }
}
