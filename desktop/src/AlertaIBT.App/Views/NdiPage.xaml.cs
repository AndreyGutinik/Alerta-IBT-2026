using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AlertaIBT.App.ViewModels;

namespace AlertaIBT.App.Views;

/// <summary>
/// The NDI tab. The page binds to <see cref="NdiViewModel"/>; what it adds is the life of the tab: while it is on screen the view-model asks the
/// engine to look for sources and the numbers (fps, signal) are read about twice a second; when the tab (or the window) is hidden both stop, and
/// the thumbnails stop polling on their own.
/// </summary>
public partial class NdiPage : UserControl
{
    private static readonly TimeSpan NumbersTick = TimeSpan.FromMilliseconds(500);

    private readonly DispatcherTimer _numbers;
    private NdiViewModel? _watched;

    /// <summary>Builds the page; the data context (a <see cref="NdiViewModel"/>) is set by the window.</summary>
    public NdiPage()
    {
        InitializeComponent();
        _numbers = new DispatcherTimer(NumbersTick, DispatcherPriority.Background, (_, _) => ViewModel?.Tick(), Dispatcher);
        _numbers.Stop();
        IsVisibleChanged += (_, _) => UpdateActive();
        DataContextChanged += (_, _) => UpdateActive();
        Unloaded += (_, _) => Deactivate();
    }

    /// <summary>The two big pictures (Prévia, No ar), for tests.</summary>
    internal NdiThumbnailView PreviewPicture => Pictures().First();

    /// <summary>The picture of the lane that is on air, for tests.</summary>
    internal NdiThumbnailView OnAirPicture => Pictures().Last();

    private NdiViewModel? ViewModel => DataContext as NdiViewModel;

    private IEnumerable<NdiThumbnailView> Pictures()
    {
        var list = new List<NdiThumbnailView>();
        Collect(PreviewLane, list);
        Collect(OnAirLane, list);
        return list;
    }

    private static void Collect(DependencyObject parent, List<NdiThumbnailView> found)
    {
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is NdiThumbnailView view)
            {
                found.Add(view);
            }

            Collect(child, found);
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
            _numbers.Start();
        }
        else
        {
            Deactivate();
        }
    }

    private void Deactivate()
    {
        _numbers.Stop();
        _watched?.SetActive(false);
    }
}
