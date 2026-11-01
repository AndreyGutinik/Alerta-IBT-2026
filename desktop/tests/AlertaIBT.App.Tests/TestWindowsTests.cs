using System.Windows;
using System.Windows.Interop;

namespace AlertaIBT.App.Tests;

// The self-check of the test infrastructure (batch 39): layout tests never put a window on a monitor, and the guard notices when one does.
[Collection(RealDesktopCollection.Name)] // the negative checks put a 40 px window in the work area for a moment
public sealed class TestWindowsTests
{
    private static Window Tiny() => new() { Width = 40, Height = 40, Style = null };

    [Fact]
    public void An_off_screen_window_is_on_no_monitor_does_not_take_the_focus_and_has_no_taskbar_button()
    {
        WpfTestHost.Run(() =>
        {
            var foreground = NativeForeground();
            var window = Tiny();
            try
            {
                TestWindows.ShowOffScreen(window);

                var handle = new WindowInteropHelper(window).Handle;
                Assert.True(window.IsVisible);
                Assert.False(TestWindows.IsOnAnyMonitor(handle, out var rect), $"on a monitor at {rect.Describe()}");
                Assert.False(window.ShowInTaskbar);
                Assert.False(window.IsActive);
                Assert.Equal(foreground, NativeForeground());
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void The_rectangles_are_judged_against_every_monitor_even_by_one_pixel()
    {
        var monitors = TestWindows.Monitors();
        Assert.NotEmpty(monitors);

        var first = monitors[0];
        Assert.True(TestWindows.IntersectsAnyMonitor(new TestWindows.Rect32 { Left = first.Left + 10, Top = first.Top + 10, Right = first.Left + 20, Bottom = first.Top + 20 }, monitors));
        Assert.True(TestWindows.IntersectsAnyMonitor(new TestWindows.Rect32 { Left = first.Left - 50, Top = first.Top + 10, Right = first.Left + 1, Bottom = first.Top + 20 }, monitors));
        Assert.False(TestWindows.IntersectsAnyMonitor(new TestWindows.Rect32 { Left = -20000, Top = 100, Right = -19000, Bottom = 400 }, monitors));
        Assert.False(TestWindows.IntersectsAnyMonitor(new TestWindows.Rect32 { Left = (int)TestWindows.OffScreenLeft, Top = (int)TestWindows.OffScreenTop, Right = (int)TestWindows.OffScreenLeft + 2560, Bottom = (int)TestWindows.OffScreenTop + 1440 }, monitors));
    }

    [Fact]
    public void The_guard_fails_a_layout_test_whose_window_ends_up_on_a_monitor_and_not_one_that_stayed_off_screen()
    {
        WpfTestHost.Run(() =>
        {
            // A layout test that cheats: the window is placed in the work area. (Invisible: size 40, opacity 0; it lives about 40 ms.)
            WindowGuard.Begin("a layout test that cheats", exempt: false);
            var onScreen = Tiny();
            TestWindows.PlaceOnScreen(onScreen);
            onScreen.Show();
            try
            {
                var ex = Assert.Throws<InvalidOperationException>(WindowGuard.End);
                Assert.Contains("a layout test that cheats", ex.Message);
                Assert.Contains("TestWindows.ShowOffScreen", ex.Message);
            }
            finally
            {
                onScreen.Close();
            }

            // The same shape of test with the helper: nothing to report.
            WindowGuard.Begin("a layout test that behaves", exempt: false);
            var offScreen = Tiny();
            TestWindows.ShowOffScreen(offScreen);
            try
            {
                WindowGuard.End();
            }
            finally
            {
                offScreen.Close();
            }

            // A real-desktop test may use the screen.
            WindowGuard.Begin("a real desktop test", exempt: true);
            var real = Tiny();
            TestWindows.PlaceOnScreen(real);
            real.Show();
            try
            {
                WindowGuard.End();
            }
            finally
            {
                real.Close();
            }
        });
    }

    [Fact]
    public void ShowOffScreen_itself_refuses_a_window_that_landed_on_a_monitor()
    {
        WpfTestHost.Run(() =>
        {
            var window = Tiny();
            TestWindows.PlaceOnScreen(window);
            window.Show();
            try
            {
                var ex = Assert.Throws<InvalidOperationException>(() => TestWindows.AssertOffScreen(window));
                Assert.Contains("TestWindows.ShowOffScreen", ex.Message);
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static IntPtr NativeForeground() => GetForegroundWindow();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
}
