using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AlertaIBT.App.Overlay;
using AlertaIBT.App.Platform;
using AlertaIBT.Core.Overlay;
using AlertaIBT.Core.Screens;
using AlertaIBT.Core.Settings;

namespace AlertaIBT.App.Tests.Overlay;

/// <summary>
/// The real WPF window, created on the test host's STA thread. They check what the church needs and nobody can eyeball:
/// excluded from capture, click-through, never focused, topmost, in the right place.
/// </summary>
public sealed class OverlayWindowTests
{
    private const int WdaExcludeFromCapture = 0x11;

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowDisplayAffinity(IntPtr hWnd, out uint affinity);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    private static ScreenInfo Primary() => MonitorService.Enumerate().First(s => s.IsPrimary);

    // Transparent colors keep the real bar invisible on the developer's screen while the window is really shown.
    private static BarSettings Invisible() => new() { Background = "#00000000", Foreground = "#00000000" };

    private static uint Affinity(IntPtr hwnd)
    {
        Assert.True(GetWindowDisplayAffinity(hwnd, out var affinity));
        return affinity;
    }

    [Fact]
    public void A_new_window_is_already_excluded_from_capture_before_anything_is_shown()
    {
        WpfTestHost.Run(() =>
        {
            using var window = new OverlayWindow();

            Assert.NotEqual(IntPtr.Zero, window.Handle);
            Assert.False(window.IsVisible);
            Assert.False(IsWindowVisible(window.Handle));
            Assert.Equal((uint)WdaExcludeFromCapture, Affinity(window.Handle));
            Assert.True(window.CaptureExcluded);
        });
    }

    [Fact]
    public void The_window_has_the_layered_transparent_noactivate_toolwindow_and_topmost_styles()
    {
        WpfTestHost.Run(() =>
        {
            using var window = new OverlayWindow();

            var style = NativeMethods.GetExStyle(window.Handle);

            Assert.True((style & NativeMethods.WS_EX_LAYERED) != 0, "layered");
            Assert.True((style & NativeMethods.WS_EX_TRANSPARENT) != 0, "transparent (click-through)");
            Assert.True((style & NativeMethods.WS_EX_NOACTIVATE) != 0, "no-activate");
            Assert.True((style & NativeMethods.WS_EX_TOOLWINDOW) != 0, "tool window (no taskbar, no Alt+Tab)");
            Assert.True((style & NativeMethods.WS_EX_TOPMOST) != 0, "topmost");
            Assert.False(window.ShowInTaskbar);
            Assert.True(window.AllowsTransparency);
        });
    }

    [Fact]
    public void The_window_refuses_activation_and_lets_every_click_through()
    {
        WpfTestHost.Run(() =>
        {
            using var window = new OverlayWindow();

            const int wmMouseActivate = 0x0021;
            const int wmNcHitTest = 0x0084;

            Assert.Equal(NativeMethods.MA_NOACTIVATE, (int)SendMessage(window.Handle, wmMouseActivate, IntPtr.Zero, IntPtr.Zero));
            Assert.Equal(NativeMethods.HTTRANSPARENT, (int)SendMessage(window.Handle, wmNcHitTest, IntPtr.Zero, IntPtr.Zero));
        });
    }

    [Fact]
    public void Showing_the_bar_puts_it_exactly_where_the_layout_says_and_never_takes_focus()
    {
        WpfTestHost.Run(() =>
        {
            var screen = Primary();
            var before = NativeMethods.GetForegroundWindow();
            using var window = new OverlayWindow();

            window.Prepare("CRETA PRETO | PLACA: GBR-9003 | FAROL ACESO", Invisible(), screen);

            Assert.True(window.IsVisible);
            Assert.False(window.IsActive, "the overlay must never become the active window");
            Assert.NotEqual(window.Handle, NativeMethods.GetForegroundWindow());
            Assert.Equal(before, NativeMethods.GetForegroundWindow());

            var layout = window.Layout!;
            Assert.True(NativeMethods.GetWindowRect(window.Handle, out var rect));
            Assert.Equal(screen.X, rect.Left);
            Assert.Equal(screen.Y, rect.Top); // top is the default position
            Assert.Equal(screen.Width, rect.Right - rect.Left);
            Assert.Equal(56, rect.Bottom - rect.Top);
            Assert.Equal(56, layout.WindowHeight);

            Assert.True((NativeMethods.GetExStyle(window.Handle) & NativeMethods.WS_EX_TOPMOST) != 0);
            Assert.Equal((uint)WdaExcludeFromCapture, Affinity(window.Handle));
        });
    }

    [Fact]
    public void A_bar_at_the_bottom_sits_on_the_last_rows_of_the_screen()
    {
        WpfTestHost.Run(() =>
        {
            var screen = Primary();
            using var window = new OverlayWindow();

            window.Prepare("GOL BRANCO", Invisible() with { Position = "bottom", Height = 80 }, screen);

            Assert.True(NativeMethods.GetWindowRect(window.Handle, out var rect));
            Assert.Equal(screen.Y + screen.Height - 80, rect.Top);
            Assert.Equal(screen.Y + screen.Height, rect.Bottom);
            window.Hide();
        });
    }

    [Fact]
    public void Capture_exclusion_survives_hiding_showing_and_every_new_alert()
    {
        WpfTestHost.Run(() =>
        {
            var screen = Primary();
            using var window = new OverlayWindow();

            for (var i = 0; i < 3; i++)
            {
                window.Prepare("ALERTA " + i, Invisible(), screen);
                Assert.Equal((uint)WdaExcludeFromCapture, Affinity(window.Handle));
                window.Hide();
                Assert.False(window.IsVisible);
                Assert.Equal((uint)WdaExcludeFromCapture, Affinity(window.Handle));
            }

            Assert.True(window.CaptureExcluded);
        });
    }

    [Fact]
    public void The_affinity_is_set_again_if_something_cleared_it()
    {
        WpfTestHost.Run(() =>
        {
            using var window = new OverlayWindow();
            var changes = 0;
            window.CaptureExcludedChanged += () => changes++;
            Assert.True(NativeMethods.SetWindowDisplayAffinity(window.Handle, NativeMethods.WDA_NONE));

            window.Prepare("X", Invisible(), Primary());

            Assert.Equal((uint)WdaExcludeFromCapture, Affinity(window.Handle));
            Assert.True(window.CaptureExcluded);
            Assert.Equal(0, changes); // it was repaired before anything could notice
            window.Hide();
        });
    }

    [Fact]
    public void Hiding_and_showing_again_work_and_the_window_stays_unfocused()
    {
        WpfTestHost.Run(() =>
        {
            var screen = Primary();
            using var window = new OverlayWindow();

            window.Prepare("A", Invisible(), screen);
            window.Hide();
            window.Prepare("B", Invisible(), screen);

            Assert.True(window.IsVisible);
            Assert.False(window.IsActive);
            window.Hide();
            Assert.False(IsWindowVisible(window.Handle));
        });
    }

    [Fact]
    public void The_text_is_drawn_in_the_bar_color_scheme_and_only_where_the_frame_puts_it()
    {
        WpfTestHost.Run(() =>
        {
            var screen = Primary();
            using var window = new OverlayWindow();
            var bar = new BarSettings(); // black bar, cream text
            var prepared = window.Prepare("COROLLA PRATA | PLACA: ABC-1D23 | VIDRO ABERTO", bar, screen);
            window.Hide(); // drawn off-screen below: nothing flashes on the developer's monitor

            var layout = window.Layout!;
            Assert.True(prepared.TextWidthPx > 200);

            // Text waiting beyond the right edge: only the black bar shows.
            window.Apply(new BarFrame(BarPhase.Scroll, 0, 1, screen.Width));
            var waiting = Render(window, layout);
            Assert.Equal(0, CountPixels(waiting, IsCream));
            Assert.True(CountPixels(waiting, IsBlack) > waiting.PixelWidth * waiting.PixelHeight * 0.95);

            // Text in the middle of the screen: cream pixels appear.
            window.Apply(new BarFrame(BarPhase.Scroll, 0, 1, 10));
            var crossing = Render(window, layout);
            Assert.True(CountPixels(crossing, IsCream) > 200, "the text should be visible");

            // Text gone beyond the left edge.
            window.Apply(new BarFrame(BarPhase.Scroll, 0, 1, -prepared.TextWidthPx));
            var gone = Render(window, layout);
            Assert.Equal(0, CountPixels(gone, IsCream));
        });
    }

    [Fact]
    public void The_font_is_marble_and_every_character_of_a_portuguese_alert_has_a_glyph()
    {
        WpfTestHost.Run(() =>
        {
            var family = MarbleFont.Family();
            var element = new MarqueeText();
            var text = "ÁÃÂÉÊÍÓÕÔÚÇ COROLLA PRATA | PLACA: ABC-1D23 | VIDRO ABERTO 0123456789";

            element.Layout(
                text,
                new Typeface(family, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
                new Typeface("Segoe UI"),
                fontSize: 36,
                letterSpacingEm: BarLayout.LetterSpacingEm,
                padding: 24,
                barHeight: 56,
                foreground: Brushes.White,
                pixelsPerDip: 1);

            Assert.Equal(text.Length, element.GlyphCount);
        });
    }

    [Fact]
    public void Letter_spacing_and_padding_widen_the_text_by_exactly_their_share()
    {
        WpfTestHost.Run(() =>
        {
            var family = MarbleFont.Family();
            var typeface = new Typeface(family, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
            var fallback = new Typeface("Segoe UI");

            double Width(double spacing, double padding)
            {
                var e = new MarqueeText();
                e.Layout("PLACA", typeface, fallback, 40, spacing, padding, 56, Brushes.White, 1);
                return e.ContentWidth;
            }

            var plain = Width(0, 0);
            Assert.True(plain > 40);
            Assert.Equal(plain + (5 * 40 * 0.08), Width(0.08, 0), 6); // 5 letters, 0.08 em each
            Assert.Equal(plain + 48, Width(0, 24), 6); // 24 on each side
        });
    }

    [Fact]
    public void Controller_and_real_window_play_the_three_second_test_to_the_end_and_leave_nothing_visible()
    {
        var result = WpfTestHost.RunAsync(async () =>
        {
            var dir = Path.Combine(Path.GetTempPath(), "alerta-ibt-e2e-" + Guid.NewGuid().ToString("N"));
            using var settings = new SettingsService(new SettingsStore(Path.Combine(dir, "settings.json")));
            using var window = new OverlayWindow();
            var surface = new InvisibleSurface(window);
            using var controller = new OverlayController(
                settings,
                new FakeLed(Primary()),
                () => surface,
                new CompositionFrameSource(),
                new WpfUiDispatcher(System.Windows.Threading.Dispatcher.CurrentDispatcher),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<OverlayController>.Instance);

            var clock = System.Diagnostics.Stopwatch.StartNew();
            await controller.PreviewAsync();
            clock.Stop();

            var outcome = (clock.Elapsed, surface.Frames, Visible: window.IsVisible, Shown: controller.IsShowing);
            controller.Dispose();
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
                // Left in the temp folder; harmless.
            }

            return outcome;
        });

        // The preview is one pass of 3 s: slide in, scroll, slide out, driven by real rendered frames. The upper bound only says "it did not hang":
        // the other WPF tests share this one dispatcher thread, and when they keep it busy the real frames of this pass wait for it.
        Assert.InRange(result.Elapsed.TotalSeconds, 2.8, 30.0);
        Assert.True(result.Frames > 10, $"only {result.Frames} frames were drawn"); // 30 or more on an idle PC; fewer when the other WPF tests hold the dispatcher
        Assert.False(result.Visible);
        Assert.False(result.Shown);
    }

    // The real window, real frame clock and real controller, but every frame is drawn fully transparent so the developer's
    // screen does not flash a black bar for three seconds.
    private sealed class InvisibleSurface(OverlayWindow window) : IBarSurface
    {
        public bool CaptureExcluded => window.CaptureExcluded;

        public event Action? CaptureExcludedChanged
        {
            add => window.CaptureExcludedChanged += value;
            remove => window.CaptureExcludedChanged -= value;
        }

        public PreparedBar Prepare(string text, BarSettings bar, ScreenInfo screen) => window.Prepare(text, bar, screen);

        public int Frames { get; private set; }

        public void Apply(BarFrame frame)
        {
            Frames++;
            window.Apply(frame with { Opacity = 0 });
        }

        public void Hide() => window.Hide();

        public void Dispose() => window.Dispose();
    }

    private static RenderTargetBitmap Render(OverlayWindow window, BarLayout layout)
    {
        var bar = window.BarElement;
        var size = new Size(layout.WindowWidth / layout.DpiScale, layout.BarHeight);
        bar.Measure(size);
        bar.Arrange(new Rect(size));
        bar.UpdateLayout();
        var bitmap = new RenderTargetBitmap(layout.WindowWidth, layout.WindowHeight, 96 * layout.DpiScale, 96 * layout.DpiScale, PixelFormats.Pbgra32);
        bitmap.Render(bar);
        return bitmap;
    }

    private static int CountPixels(BitmapSource bitmap, Func<byte, byte, byte, bool> match)
    {
        var stride = bitmap.PixelWidth * 4;
        var pixels = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, stride, 0);
        var count = 0;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            if (match(pixels[i + 2], pixels[i + 1], pixels[i]))
            {
                count++;
            }
        }

        return count;
    }

    // Text color #FFF7E8: light and warm. Anti-aliased edges are ignored by asking for a strong match.
    private static bool IsCream(byte r, byte g, byte b) => r > 0xE0 && g > 0xD8 && b > 0xC0;

    private static bool IsBlack(byte r, byte g, byte b) => r < 0x10 && g < 0x10 && b < 0x10;
}
