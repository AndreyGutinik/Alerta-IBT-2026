using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AlertaIBT.App.ViewModels;
using AlertaIBT.Core.Ndi;

namespace AlertaIBT.App.Views;

/// <summary>
/// One of the two big 16:9 pictures of the NDI tab. It draws the newest thumbnail of its lane, a rectangle over it showing which band of the picture
/// the telao will show (the rest is dimmed) and a placeholder when there is no camera or no signal (never a black rectangle). The mouse wheel moves
/// the band by 1% (Shift = 5%). The picture is copied into ONE reusable <see cref="WriteableBitmap"/> about 20 times a second, and only while the
/// control is on screen (a hidden tab polls nothing); the 60 fps telao path never waits for it.
/// </summary>
public sealed class NdiThumbnailView : FrameworkElement
{
    /// <summary>The lane this picture shows.</summary>
    public static readonly DependencyProperty LaneProperty =
        DependencyProperty.Register(nameof(Lane), typeof(NdiLaneViewModel), typeof(NdiThumbnailView), new FrameworkPropertyMetadata(null, OnLaneChanged));

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);
    private static readonly Brush Well = Freeze(new SolidColorBrush(Color.FromRgb(0x10, 0x10, 0x14)));
    private static readonly Brush Shade = Freeze(new SolidColorBrush(Color.FromArgb(0xB3, 0x06, 0x06, 0x08)));
    private static readonly Pen CropPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0x4F, 0xD8, 0x8E)), 2));
    private static readonly Pen Edge = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0x32, 0x32, 0x3A)), 1));

    private readonly byte[] _scratch = new byte[ThumbnailBuffer.ThumbWidth * 480 * 4];
    private readonly DispatcherTimer _timer;
    private WriteableBitmap? _bitmap;
    private long _version;

    /// <summary>Builds the picture.</summary>
    public NdiThumbnailView()
    {
        ClipToBounds = true;
        Focusable = false;
        SnapsToDevicePixels = true;
        _timer = new DispatcherTimer(PollInterval, DispatcherPriority.Background, (_, _) => Poll(), Dispatcher);
        _timer.Stop();
        IsVisibleChanged += (_, _) => UpdateTimer();
        Loaded += (_, _) => UpdateTimer();
        Unloaded += (_, _) => _timer.Stop();
    }

    /// <summary>The lane this picture shows.</summary>
    public NdiLaneViewModel? Lane
    {
        get => (NdiLaneViewModel?)GetValue(LaneProperty);
        set => SetValue(LaneProperty, value);
    }

    /// <summary>Whether the picture asks for new thumbnails now (it does while the control is on screen).</summary>
    public bool IsPolling => _timer.IsEnabled;

    /// <summary>The bitmap that is reused for every picture (null until the first one); the same object for the whole life of the control, while the size stays.</summary>
    public WriteableBitmap? Bitmap => _bitmap;

    /// <summary>Asks the lane for its newest picture and, if there is one, copies it into the reusable bitmap and redraws. Called by the timer; tests call it by hand.</summary>
    public void Poll()
    {
        var lane = Lane;
        if (lane is null)
        {
            return;
        }

        if (!lane.TryCopyThumbnail(_scratch, out var width, out var height, out var version) || version == _version || width <= 0 || height <= 0)
        {
            return;
        }

        _version = version;
        if (_bitmap is null || _bitmap.PixelWidth != width || _bitmap.PixelHeight != height)
        {
            _bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
        }

        _bitmap.WritePixels(new Int32Rect(0, 0, width, height), _scratch, width * 4, 0);
        InvalidateVisual();
    }

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? 480 : availableSize.Width;
        return new Size(width, Math.Round(width * 9 / 16));
    }

    /// <inheritdoc />
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        var lane = Lane;
        if (lane is { HasCamera: true })
        {
            var notches = Math.Max(1, Math.Abs(e.Delta) / 120);
            var step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 5 : 1;

            // Wheel up lifts the band toward the top of the picture (a smaller position).
            lane.Nudge((e.Delta > 0 ? -1 : 1) * notches * step);
            InvalidateVisual();
        }

        e.Handled = true;
        base.OnMouseWheel(e);
    }

    /// <inheritdoc />
    protected override void OnRender(DrawingContext dc)
    {
        var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
        var round = new RectangleGeometry(bounds, 10, 10);
        dc.PushClip(round);
        dc.DrawRectangle(Well, null, bounds);

        var lane = Lane;
        var hasPicture = _bitmap is not null && lane is { HasCamera: true };
        if (hasPicture)
        {
            var picture = Fit(bounds, _bitmap!.PixelWidth, _bitmap.PixelHeight);
            dc.DrawImage(_bitmap, picture);
            if (lane!.NoSignal)
            {
                dc.DrawRectangle(Shade, null, bounds);
                DrawPlaceholder(dc, bounds, "LucideWifiOff", "sem sinal");
            }

            DrawCrop(dc, picture, lane);
        }
        else if (lane is { HasCamera: true })
        {
            DrawPlaceholder(dc, bounds, lane.State == NdiConnectionState.Connecting ? "LucideVideo" : "LucideWifiOff", lane.State == NdiConnectionState.Connecting ? "conectando" : "sem sinal");
        }
        else
        {
            DrawPlaceholder(dc, bounds, "LucideVideo", "sem c\U000000E2mera");
        }

        dc.Pop();
        dc.DrawRoundedRectangle(null, Edge, new Rect(0.5, 0.5, Math.Max(0, ActualWidth - 1), Math.Max(0, ActualHeight - 1)), 10, 10);
    }

    private static void OnLaneChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        var view = (NdiThumbnailView)sender;
        if (e.OldValue is NdiLaneViewModel old)
        {
            old.PropertyChanged -= view.OnLaneProperty;
        }

        if (e.NewValue is NdiLaneViewModel lane)
        {
            lane.PropertyChanged += view.OnLaneProperty;
        }

        view._version = 0;
        view.InvalidateVisual();
    }

    private void OnLaneProperty(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => InvalidateVisual();

    private void UpdateTimer()
    {
        if (IsVisible && IsLoaded)
        {
            _timer.Start();
        }
        else
        {
            _timer.Stop();
        }
    }

    // The 16:9 (or whatever the camera sends) picture, as large as fits, centered.
    private static Rect Fit(Rect box, int width, int height)
    {
        var scale = Math.Min(box.Width / width, box.Height / height);
        var w = width * scale;
        var h = height * scale;
        return new Rect(box.X + ((box.Width - w) / 2), box.Y + ((box.Height - h) / 2), w, h);
    }

    // Dims what the telao will not show and outlines what it will.
    private static void DrawCrop(DrawingContext dc, Rect picture, NdiLaneViewModel lane)
    {
        var crop = NdiCropMath.Compute(
            (int)Math.Round(picture.Width * 100),
            (int)Math.Round(picture.Height * 100),
            lane.CropWidth,
            lane.CropHeight,
            lane.Position);
        var visible = new Rect(
            picture.X + (crop.U0 * picture.Width),
            picture.Y + (crop.V0 * picture.Height),
            Math.Max(1, (crop.U1 - crop.U0) * picture.Width),
            Math.Max(1, (crop.V1 - crop.V0) * picture.Height));
        var shade = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(picture), new RectangleGeometry(visible));
        dc.DrawGeometry(Shade, null, shade);
        visible.Inflate(-1, -1);
        dc.DrawRectangle(null, CropPen, visible);
    }

    private void DrawPlaceholder(DrawingContext dc, Rect bounds, string iconKey, string text)
    {
        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var muted = (Brush)(TryFindResource("IbtTextSecondaryBrush") ?? Brushes.Gray);
        var family = TryFindResource("MarbleFont") as FontFamily ?? new FontFamily("Segoe UI");
        var label = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), 13, muted, pixelsPerDip);

        var size = Math.Clamp(bounds.Height * 0.22, 20, 40);
        var gap = 8d;
        var total = size + gap + label.Height;
        var top = bounds.Y + ((bounds.Height - total) / 2);
        if (TryFindResource(iconKey) is Geometry geometry)
        {
            var pen = new Pen(muted, 2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            var scale = size / 24;
            var transform = new TransformGroup();
            transform.Children.Add(new ScaleTransform(scale, scale));
            transform.Children.Add(new TranslateTransform(bounds.X + ((bounds.Width - size) / 2), top));
            dc.PushTransform(transform);
            dc.DrawGeometry(null, pen, geometry);
            dc.Pop();
        }

        dc.DrawText(label, new Point(bounds.X + ((bounds.Width - label.Width) / 2), top + size + gap));
    }

    private static T Freeze<T>(T freezable)
        where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
