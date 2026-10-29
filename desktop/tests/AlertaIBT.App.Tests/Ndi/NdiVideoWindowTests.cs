using System.Runtime.InteropServices;
using System.Windows.Interop;
using AlertaIBT.App.Ndi;
using AlertaIBT.App.Platform;
using AlertaIBT.Core.Ndi;
using AlertaIBT.Core.Screens;

namespace AlertaIBT.App.Tests.Ndi;

/// <summary>
/// Real windows, real Win32 calls: the styles of the camera window, its place in the z-order against a topmost window (the alert bar) and a normal one
/// (Holyrics' output), and the pixels the Direct3D renderer really puts on the screen. They show small windows on the primary monitor for a moment.
/// </summary>
public sealed class NdiVideoWindowTests
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr window);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr window, IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern uint GetPixel(IntPtr dc, int x, int y);

    private static readonly ScreenInfo Spot = new("test", "test", 120, 120, 640, 192, false);

    private static HwndSource Helper(bool topmost)
    {
        var source = new HwndSource(new HwndSourceParameters("helper")
        {
            WindowStyle = unchecked((int)0x80000000),
            ExtendedWindowStyle = NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE | (topmost ? NativeMethods.WS_EX_TOPMOST : 0),
            Width = 100,
            Height = 100,
            PositionX = 900,
            PositionY = 500,
        });
        NativeMethods.SetWindowPos(
            source.Handle,
            topmost ? NativeMethods.HWND_TOPMOST : IntPtr.Zero,
            900,
            500,
            100,
            100,
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
        return source;
    }

    [Fact]
    public void The_camera_window_is_non_topmost_click_through_unfocusable_and_hidden_from_the_taskbar()
    {
        WpfTestHost.Run(() =>
        {
            using var window = new NdiVideoWindow();
            window.Place(Spot);
            window.Show();
            var foreground = NativeMethods.GetForegroundWindow();

            var facts = window.Facts();

            Assert.True(facts.Visible, "visible");
            Assert.False(facts.Topmost);
            Assert.True(facts.ToolWindow, "tool");
            Assert.True(facts.NoActivate, "noactivate");
            Assert.True(facts.ClickThrough, $"click {facts.ExStyle:X}");
            Assert.False(window.CaptureExcluded); // "Esconder da live" is off by default
            Assert.NotEqual(window.Handle, foreground); // showing it never takes the focus
            NativeMethods.GetWindowRect(window.Handle, out var rect);
            Assert.Equal((120, 120, 640, 192), (rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top));
        });
    }

    [Fact]
    public void Hit_testing_lets_the_mouse_through_and_activation_is_refused()
    {
        WpfTestHost.Run(() =>
        {
            using var window = new NdiVideoWindow();
            const int WmNcHitTest = 0x0084;
            const int WmMouseActivate = 0x0021;
            var hit = SendMessage(window.Handle, WmNcHitTest, IntPtr.Zero, IntPtr.Zero);
            var activate = SendMessage(window.Handle, WmMouseActivate, IntPtr.Zero, IntPtr.Zero);

            Assert.Equal(-1, (int)hit); // HTTRANSPARENT
            Assert.Equal(3, (int)activate); // MA_NOACTIVATE
        });
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

    [Fact]
    public void Z_order_puts_the_camera_below_a_normal_window_and_below_a_topmost_one()
    {
        WpfTestHost.Run(() =>
        {
            using var window = new NdiVideoWindow();
            window.Place(Spot);
            window.Show();
            using var holyrics = Helper(topmost: false); // raised after the camera was shown, as Holyrics' output is
            using var bar = Helper(topmost: true);

            window.KeepAtBottom();

            Assert.True(WindowFacts.IsAbove(holyrics.Handle, window.Handle), "a janela normal deve ficar acima da camera");
            Assert.True(WindowFacts.IsAbove(bar.Handle, window.Handle), "a barra (topmost) deve ficar acima da camera");
            Assert.True(WindowFacts.IsAbove(bar.Handle, holyrics.Handle));
            Assert.False(WindowFacts.IsAbove(window.Handle, holyrics.Handle));
            Assert.False(window.Facts().Topmost);
        });
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string className, string? windowName);

    [Fact]
    public void At_the_bottom_the_camera_is_still_above_the_desktop_itself()
    {
        WpfTestHost.Run(() =>
        {
            using var window = new NdiVideoWindow();
            window.Place(Spot);
            window.Show();
            window.KeepAtBottom();
            var desktop = FindWindow("Progman", null);
            Assert.SkipWhen(desktop == IntPtr.Zero, "sem a janela da area de trabalho");

            Assert.True(WindowFacts.IsAbove(window.Handle, desktop), "HWND_BOTTOM nao pode afundar a camera atras da area de trabalho");
        });
    }

    [Fact]
    public void Showing_again_after_a_normal_window_was_raised_puts_the_camera_back_at_the_bottom()
    {
        WpfTestHost.Run(() =>
        {
            using var window = new NdiVideoWindow();
            window.Place(Spot);
            window.Show();
            using var normal = Helper(topmost: false);
            window.Hide();

            window.Show();

            Assert.True(WindowFacts.IsAbove(normal.Handle, window.Handle));
        });
    }

    [Fact]
    public void Hide_from_live_excludes_the_window_from_capture_and_can_be_undone()
    {
        WpfTestHost.Run(() =>
        {
            using var window = new NdiVideoWindow();

            Assert.True(window.SetHiddenFromCapture(true));
            Assert.True(window.CaptureExcluded);
            Assert.True(window.SetHiddenFromCapture(false));
            Assert.False(window.CaptureExcluded);
        });
    }

    private static uint Pixel(int x, int y)
    {
        var dc = GetDC(IntPtr.Zero);
        try
        {
            return GetPixel(dc, x, y);
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, dc);
        }
    }

    // GDI COLORREF 0x00BBGGRR.
    private static (int R, int G, int B) Rgb(uint c) => ((int)(c & 0xFF), (int)((c >> 8) & 0xFF), (int)((c >> 16) & 0xFF));

    private static byte[] TwoBands(int w, int h)
    {
        var data = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var o = (y * w + x) * 4;
                var top = y < h / 2;
                data[o] = (byte)(top ? 0 : 255); // B
                data[o + 1] = 0;
                data[o + 2] = (byte)(top ? 255 : 0); // R
                data[o + 3] = 255;
            }
        }

        return data;
    }

    private static bool WaitPresented(D3dVideoRenderer renderer, long atLeast)
    {
        var end = Environment.TickCount64 + 5000;
        while (renderer.FramesPresented < atLeast && Environment.TickCount64 < end && renderer.Error is null)
        {
            Thread.Sleep(10);
        }

        return renderer.FramesPresented >= atLeast;
    }

    [Fact]
    public void The_renderer_crops_fill_and_the_vertical_position_picks_the_band_on_the_real_screen()
    {
        var mailbox = new FrameMailbox();
        var data = TwoBands(1920, 1080); // red top half, blue bottom half
        var renderer = WpfTestHost.Run(() =>
        {
            var window = new NdiVideoWindow();
            window.Place(Spot);
            window.Show();

            // Only for this pixel test: raise it above the developer's own windows so the screen pixels are the camera's (the z-order is tested above).
            NativeMethods.SetWindowPos(window.Handle, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0, NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
            var r = new D3dVideoRenderer(window.Handle, Spot.Width, Spot.Height, mailbox);
            r.Start();
            return new Disposer(window, r);
        });

        try
        {
            var renderer1 = renderer.Renderer;
            mailbox.Post(new NdiFrameView(1920, 1080, 1920 * 4, data, 0), 0); // top of the picture
            renderer1.Wake();
            Assert.True(WaitPresented(renderer1, 1), renderer1.Error ?? "nada foi pintado");
            Thread.Sleep(150);

            var midY = Spot.Y + Spot.Height / 2;
            var x = Spot.X + 100;
            var top = Rgb(Pixel(x, midY));
            Assert.SkipWhen(top == (0, 0, 0), "a captura da tela devolveu preto (sessao bloqueada?)");
            Assert.True(top.R > 200 && top.B < 60, $"posicao 0: o meio da faixa deve ser vermelho, veio {top}");

            renderer1.SetPosition(100); // bottom of the picture: the middle of the window is now blue
            Assert.True(WaitPresented(renderer1, renderer1.FramesPresented + 1));
            Thread.Sleep(150);
            var bottom = Rgb(Pixel(x, midY));
            Assert.True(bottom.B > 200 && bottom.R < 60, $"posicao 100: o meio da faixa deve ser azul, veio {bottom}");

            // The bands meet at 50% of the picture: at position 0 the visible 53.3% shows red for 93.75% of the window then blue.
            renderer1.SetPosition(0);
            Assert.True(WaitPresented(renderer1, renderer1.FramesPresented + 1));
            Thread.Sleep(150);
            var nearBottom = Rgb(Pixel(x, Spot.Y + Spot.Height - 3));
            Assert.True(nearBottom.B > 200, $"posicao 0: a borda de baixo ja deve ser azul, veio {nearBottom}");
        }
        finally
        {
            WpfTestHost.Run(renderer.Dispose);
        }
    }

    [Fact]
    public void The_renderer_paints_only_when_something_changed_and_is_cheap_per_frame()
    {
        var mailbox = new FrameMailbox();
        var data = TwoBands(1920, 1080);
        var pair = WpfTestHost.Run(() =>
        {
            var window = new NdiVideoWindow();
            window.Place(Spot);
            window.Show();

            // Only for this pixel test: raise it above the developer's own windows so the screen pixels are the camera's (the z-order is tested above).
            NativeMethods.SetWindowPos(window.Handle, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0, NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
            var r = new D3dVideoRenderer(window.Handle, Spot.Width, Spot.Height, mailbox);
            r.Start();
            return new Disposer(window, r);
        });

        try
        {
            var renderer = pair.Renderer;
            mailbox.Post(new NdiFrameView(1920, 1080, 1920 * 4, data, 0), 50);
            renderer.Wake();
            Assert.True(WaitPresented(renderer, 1), renderer.Error ?? "nada foi pintado");

            // Idle: no new frame, no new picture.
            var idle = renderer.FramesPresented;
            Thread.Sleep(500);
            Assert.Equal(idle, renderer.FramesPresented);

            // 2 seconds at 60 fps: every frame is uploaded and drawn (the CPU part, without the wait for the blank).
            var started = Environment.TickCount64;
            var posted = 0;
            while (Environment.TickCount64 - started < 2000)
            {
                mailbox.Post(new NdiFrameView(1920, 1080, 1920 * 4, data, 50), 50);
                renderer.Wake();
                posted++;
                Thread.Sleep(16);
            }

            Assert.True(renderer.FramesPresented > idle + 30);
            Assert.True(renderer.LastRenderMs < 8, $"{renderer.LastRenderMs:0.0} ms de CPU por quadro");
            Assert.Null(renderer.Error);
        }
        finally
        {
            WpfTestHost.Run(pair.Dispose);
        }
    }

    private sealed class Disposer(NdiVideoWindow window, D3dVideoRenderer renderer) : IDisposable
    {
        public NdiVideoWindow Window { get; } = window;

        public D3dVideoRenderer Renderer { get; } = renderer;

        public void Dispose()
        {
            Renderer.Dispose();
            Window.Dispose();
        }
    }
}
