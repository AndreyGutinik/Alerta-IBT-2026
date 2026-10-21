using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AlertaIBT.App.Overlay;
using AlertaIBT.Core.Overlay;
using AlertaIBT.Core.Settings;

namespace AlertaIBT.App.Views;

/// <summary>
/// The live preview of the bar (painel-v7.html, "Prévia no LED 2560x768"): a small copy of the LED with a slide on it and the bar
/// on top, running the same pass as the real bar over and over. It is drawn on a 2560x768 canvas in LED pixels under a
/// <see cref="Viewbox"/>, with the real text layout (<see cref="MarqueeText"/>) and the real timeline (<see cref="BarTimeline"/>), so what
/// moves here is what moves there. Any change of the bar's settings restarts the pass at once. It only runs while it is visible.
/// </summary>
internal sealed class BarPreviewControl : Decorator
{
    /// <summary>The bar's settings; the preview follows them at once.</summary>
    public static readonly DependencyProperty BarProperty = DependencyProperty.Register(
        nameof(Bar),
        typeof(BarSettings),
        typeof(BarPreviewControl),
        new PropertyMetadata(new BarSettings(), (d, _) => ((BarPreviewControl)d).Rebuild()));

    /// <summary>The text that crosses the bar.</summary>
    public static readonly DependencyProperty SampleTextProperty = DependencyProperty.Register(
        nameof(SampleText),
        typeof(string),
        typeof(BarPreviewControl),
        new PropertyMetadata(BarPreview.SampleText, (d, _) => ((BarPreviewControl)d).Rebuild()));

    /// <summary>The slide shown behind the bar (the example of the mockup).</summary>
    public const string SlideText = "QU\U000000C3O GRANDE \U000000C9 O MEU DEUS";

    private const double LedWidth = 2560;
    private const double LedHeight = 768;
    private static readonly TimeSpan Pause = TimeSpan.FromMilliseconds(1200);

    private readonly Canvas _led = new() { Width = LedWidth, Height = LedHeight, ClipToBounds = true };
    private readonly Border _bar = new() { Width = LedWidth, ClipToBounds = true };
    private readonly MarqueeText _text = new();
    private readonly TranslateTransform _barSlide = new();
    private readonly TranslateTransform _textShift = new();
    private BarTimeline _timeline = new(LedWidth, 800, 90, TimeSpan.FromMilliseconds(320));
    private (string Text, double Font, double Padding, double Height, string Foreground)? _layoutKey;
    private long _startedAt;
    private bool _running;
    private bool _atTop = true;
    private double _barHeight = 56;

    /// <summary>Builds the canvas: the slide, the bar and its text.</summary>
    public BarPreviewControl()
    {
        SnapsToDevicePixels = false;
        _led.Children.Add(BuildSlide());
        _bar.RenderTransform = _barSlide;
        _text.RenderTransform = _textShift;
        _bar.Child = new Canvas { ClipToBounds = true, Children = { _text } };
        _led.Children.Add(_bar);
        Child = new Viewbox { Stretch = Stretch.Uniform, Child = _led };

        IsVisibleChanged += (_, _) => UpdateRunning();
        Loaded += (_, _) => UpdateRunning();
        Unloaded += (_, _) => Stop();
        Rebuild();
    }

    /// <summary>The width of the sample text on the LED, in LED pixels, margins included. Raised when it is worked out.</summary>
    public event Action<double>? SampleWidthMeasured;

    /// <summary>The bar's settings.</summary>
    public BarSettings Bar
    {
        get => (BarSettings)GetValue(BarProperty);
        set => SetValue(BarProperty, value);
    }

    /// <summary>The text that crosses the bar.</summary>
    public string SampleText
    {
        get => (string)GetValue(SampleTextProperty);
        set => SetValue(SampleTextProperty, value);
    }

    /// <summary>The width of the text on the LED, margins included, in LED pixels.</summary>
    public double TextWidth => _text.ContentWidth;

    /// <summary>The length of one pass of the current settings.</summary>
    public TimeSpan PassLength => _timeline.Total;

    /// <summary>Whether the pass is playing (visible and loaded).</summary>
    public bool IsRunning => _running;

    /// <summary>The top of the bar inside the LED canvas, in LED pixels (0 for a bar at the top).</summary>
    public double BarTop => Canvas.GetTop(_bar);

    /// <summary>The height of the bar in LED pixels.</summary>
    public double BarHeightPx => _barHeight;

    /// <summary>Draws the bar as it is <paramref name="elapsed"/> after the start of a pass (for tests and for the resting picture).</summary>
    internal BarFrame ShowAt(TimeSpan elapsed)
    {
        var frame = _timeline.At(elapsed);
        _barSlide.Y = (_atTop ? -1 : 1) * frame.Offset * _barHeight;
        _bar.Opacity = Math.Clamp(frame.Opacity, 0, 1);
        _textShift.X = frame.TextX;
        return frame;
    }

    private static UIElement BuildSlide()
    {
        var slide = new Grid { Width = LedWidth, Height = LedHeight };
        slide.Children.Add(new Border
        {
            Background = new LinearGradientBrush(Color.FromRgb(0x0E, 0x12, 0x26), Color.FromRgb(0x07, 0x09, 0x12), 90),
        });
        slide.Children.Add(new Border
        {
            Background = new RadialGradientBrush(Color.FromRgb(0x26, 0x34, 0x6A), Colors.Transparent)
            {
                Center = new Point(0.28, 0.15),
                GradientOrigin = new Point(0.28, 0.15),
                RadiusX = 0.55,
                RadiusY = 0.95,
            },
        });
        slide.Children.Add(new Border
        {
            Background = new RadialGradientBrush(Color.FromRgb(0x43, 0x22, 0x57), Colors.Transparent)
            {
                Center = new Point(0.82, 0.95),
                GradientOrigin = new Point(0.82, 0.95),
                RadiusX = 0.5,
                RadiusY = 0.85,
            },
        });
        slide.Children.Add(new TextBlock
        {
            Text = SlideText,
            FontFamily = MarbleFont.Family(),
            FontWeight = FontWeights.Bold,
            FontSize = 64,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        });
        return slide;
    }

    private static Brush BrushOf(string hex, Color fallback)
    {
        try
        {
            if (ColorConverter.ConvertFromString(hex) is Color color)
            {
                return new SolidColorBrush(color);
            }
        }
        catch (FormatException)
        {
            // Settings hold #RRGGBB; anything else falls back.
        }

        return new SolidColorBrush(fallback);
    }

    private void Rebuild()
    {
        var bar = Bar ?? new BarSettings();
        _atTop = !string.Equals(bar.Position, "bottom", StringComparison.OrdinalIgnoreCase);
        _barHeight = Math.Clamp(bar.Height, BarSettings.MinHeight, BarSettings.MaxHeight);
        var font = bar.FontSize ?? Math.Round(_barHeight * BarLayout.AutoFontShare, MidpointRounding.AwayFromZero);
        var padding = Math.Max(0, bar.PaddingX);
        var text = SampleText ?? string.Empty;

        _bar.Height = _barHeight;
        _bar.Background = BrushOf(bar.Background, Colors.Black);
        Canvas.SetLeft(_bar, 0);
        Canvas.SetTop(_bar, _atTop ? 0 : LedHeight - _barHeight);

        // The glyphs are laid out once; only a change of what they depend on lays them out again (not the speed).
        var key = (text, font, padding, _barHeight, bar.Foreground);
        if (_layoutKey != key)
        {
            _text.Layout(
                text,
                MarbleFont.Bold(),
                MarbleFont.Fallback(),
                font,
                BarLayout.LetterSpacingEm,
                padding,
                _barHeight,
                BrushOf(bar.Foreground, Color.FromRgb(0xFF, 0xF7, 0xE8)),
                1.0);
            _layoutKey = key;
            SampleWidthMeasured?.Invoke(_text.ContentWidth);
        }

        _timeline = new BarTimeline(
            LedWidth,
            _text.ContentWidth,
            Math.Clamp(bar.Speed, BarSettings.MinSpeed, BarSettings.MaxSpeed),
            TimeSpan.FromMilliseconds(Math.Clamp(bar.SlideMs, BarSettings.MinSlideMs, BarSettings.MaxSlideMs)));

        if (_running)
        {
            _startedAt = Stopwatch.GetTimestamp(); // a change restarts the pass, as in the mockup
            ShowAt(TimeSpan.Zero);
        }
        else
        {
            // Not playing (hidden, or no screen): a resting picture with the text in the middle of the bar.
            ShowAt(_timeline.Total / 2);
        }
    }

    private void UpdateRunning()
    {
        if (IsLoaded && IsVisible)
        {
            Start();
        }
        else
        {
            Stop();
        }
    }

    private void Start()
    {
        if (_running)
        {
            return;
        }

        _running = true;
        _startedAt = Stopwatch.GetTimestamp();
        CompositionTarget.Rendering += OnRendering;
    }

    private void Stop()
    {
        if (!_running)
        {
            return;
        }

        _running = false;
        CompositionTarget.Rendering -= OnRendering;
        ShowAt(_timeline.Total / 2);
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        var elapsed = Stopwatch.GetElapsedTime(_startedAt);
        if (elapsed > _timeline.Total + Pause)
        {
            _startedAt = Stopwatch.GetTimestamp();
            elapsed = TimeSpan.Zero;
        }

        ShowAt(elapsed);
    }
}
