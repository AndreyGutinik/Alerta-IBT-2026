using System.Windows;
using AlertaIBT.App.Platform;
using AlertaIBT.Core.Screens;

namespace AlertaIBT.App.Tests.Platform;

/// <summary>Builds policies over fake monitors, so the tests need no second screen: the LED is a monitor Windows does not have.</summary>
internal static class PolicyFakes
{
    public static ScreenInfo Primary() => MonitorService.Enumerate().First(s => s.IsPrimary);

    /// <summary>A 2560x768 "LED" to the right of the primary monitor (it is not real: nothing is ever shown on it).</summary>
    public static ScreenInfo FakeLed()
    {
        var primary = Primary();
        return new ScreenInfo("fake-led", @"\\.\DISPLAYLEDTEST", primary.X + primary.Width, primary.Y, 2560, 768, false);
    }

    public static (int X, int Y) CenterOf(ScreenInfo screen) => (screen.X + (screen.Width / 2), screen.Y + (screen.Height / 2));

    public static AppWindowPolicy Policy(ScreenInfo? led, ScreenInfo[] screens, (int X, int Y) cursor) =>
        new(() => led, null, () => screens, () => cursor);

    /// <summary>A policy whose "Janelas do app aparecem em prints" switch is <paramref name="inScreenshots"/> (read every time, so a test can flip it).</summary>
    public static AppWindowPolicy Policy(ScreenInfo? led, ScreenInfo[] screens, (int X, int Y) cursor, Func<bool> inScreenshots) =>
        new(() => led, null, () => screens, () => cursor, inScreenshots);

    /// <summary>Moves the window (shown or not) so its top left corner is at the physical pixel.</summary>
    public static void MoveTo(IntPtr hwnd, int x, int y) =>
        NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);

    public static NativeMethods.RECT WorkAreaOf(ScreenInfo screen)
    {
        var monitor = NativeMethods.MonitorFromPoint(
            new NativeMethods.POINT { X = screen.X + (screen.Width / 2), Y = screen.Y + (screen.Height / 2) },
            NativeMethods.MONITOR_DEFAULTTONEAREST);
        var info = new NativeMethods.MONITORINFOEX
        {
            cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFOEX>(),
            szDevice = string.Empty,
        };
        Assert.True(NativeMethods.GetMonitorInfo(monitor, ref info));
        return info.rcWork;
    }

    public static NativeMethods.RECT RectOf(IntPtr hwnd)
    {
        Assert.True(NativeMethods.GetWindowRect(hwnd, out var rect));
        return rect;
    }

    public static bool Inside(NativeMethods.RECT inner, NativeMethods.RECT outer) =>
        inner.Left >= outer.Left && inner.Top >= outer.Top && inner.Right <= outer.Right && inner.Bottom <= outer.Bottom;
}

// A subclass: an exact System.Windows.Window gets the Wpf.Ui implicit style, which sets AllowsTransparency once the handle exists.
internal sealed class PlainWindow : Window
{
}

[Collection(RealDesktopCollection.Name)]
public sealed class AppWindowPolicyTests
{
    [Fact]
    public void Protect_makes_the_handle_and_leaves_a_window_away_from_the_LED_capturable_before_it_is_ever_shown()
    {
        WpfTestHost.Run(() =>
        {
            var window = new PlainWindow { ShowInTaskbar = false };
            try
            {
                Assert.False(PolicyFakes.Policy(null, [PolicyFakes.Primary()], (0, 0)).Protect(window));

                var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
                Assert.NotEqual(IntPtr.Zero, handle);
                Assert.False(window.IsVisible);
                Assert.False(CaptureExclusion.IsExcluded(handle));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void The_exclusion_over_the_LED_survives_hiding_and_showing_the_window_again()
    {
        WpfTestHost.Run(() =>
        {
            var window = new PlainWindow
            {
                ShowInTaskbar = false,
                Opacity = 0,
                Width = 10,
                Height = 10,
                WindowStyle = WindowStyle.None,
            };
            TestWindows.PlaceOnScreen(window);
            try
            {
                var primary = PolicyFakes.Primary();
                PolicyFakes.Policy(primary, [primary], (0, 0)).Protect(window); // the primary stands in for the LED: the window is over it
                var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
                PolicyFakes.MoveTo(handle, primary.X + 10, primary.Y + 10);
                Assert.True(CaptureExclusion.IsExcluded(handle));

                window.Show();
                Assert.True(CaptureExclusion.IsExcluded(handle));
                window.Hide();
                window.Show();
                Assert.True(CaptureExclusion.IsExcluded(handle));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void ChooseMonitor_with_the_mouse_on_the_LED_gives_the_primary()
    {
        var primary = PolicyFakes.Primary();
        var led = PolicyFakes.FakeLed();

        var chosen = PolicyFakes.Policy(led, [primary, led], PolicyFakes.CenterOf(led)).ChooseMonitor();

        Assert.Equal(primary, chosen);
    }

    [Fact]
    public void A_hidden_window_on_the_LED_is_brought_back_to_the_chosen_monitor()
    {
        WpfTestHost.Run(() =>
        {
            var primary = PolicyFakes.Primary();
            var led = PolicyFakes.FakeLed();
            var policy = PolicyFakes.Policy(led, [primary, led], PolicyFakes.CenterOf(led));
            var window = new PlainWindow { ShowInTaskbar = false, Width = 400, Height = 300 };
            try
            {
                policy.Protect(window);
                var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
                NativeMethods.SetWindowPos(handle, IntPtr.Zero, led.X + 100, led.Y + 100, 0, 0, NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
                Assert.True(policy.IsOnLed(handle));

                policy.KeepOffLed(window, neverPlaced: false);

                Assert.False(policy.IsOnLed(handle));
                Assert.True(PolicyFakes.Inside(PolicyFakes.RectOf(handle), PolicyFakes.WorkAreaOf(primary)));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void A_window_that_is_already_off_the_LED_stays_where_the_user_left_it()
    {
        WpfTestHost.Run(() =>
        {
            var primary = PolicyFakes.Primary();
            var led = PolicyFakes.FakeLed();
            var policy = PolicyFakes.Policy(led, [primary, led], PolicyFakes.CenterOf(led));
            var window = new PlainWindow { ShowInTaskbar = false, Width = 400, Height = 300 };
            try
            {
                policy.Protect(window);
                var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
                NativeMethods.SetWindowPos(handle, IntPtr.Zero, primary.X + 37, primary.Y + 41, 0, 0, NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
                var before = PolicyFakes.RectOf(handle);

                policy.KeepOffLed(window, neverPlaced: false);

                Assert.Equal(before, PolicyFakes.RectOf(handle));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void The_first_opening_centers_the_window_on_the_chosen_monitor()
    {
        WpfTestHost.Run(() =>
        {
            var primary = PolicyFakes.Primary();
            var policy = PolicyFakes.Policy(null, [primary], PolicyFakes.CenterOf(primary));
            var window = new PlainWindow { ShowInTaskbar = false, Width = 400, Height = 300 };
            try
            {
                policy.Protect(window);
                var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;

                policy.KeepOffLed(window, neverPlaced: true);

                var rect = PolicyFakes.RectOf(handle);
                var work = PolicyFakes.WorkAreaOf(primary);
                Assert.True(PolicyFakes.Inside(rect, work));
                Assert.InRange(((rect.Left + rect.Right) / 2) - ((work.Left + work.Right) / 2), -2, 2);
                Assert.InRange(((rect.Top + rect.Bottom) / 2) - ((work.Top + work.Bottom) / 2), -2, 2);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void When_the_LED_is_the_only_monitor_the_window_still_opens_there_and_is_not_chased_away()
    {
        WpfTestHost.Run(() =>
        {
            var primary = PolicyFakes.Primary();
            var onlyLed = primary with { Width = 2560, Height = 768 }; // stands in for a lone LED monitor
            var policy = PolicyFakes.Policy(onlyLed, [onlyLed], PolicyFakes.CenterOf(onlyLed));
            var window = new PlainWindow { ShowInTaskbar = false, Width = 400, Height = 300 };
            try
            {
                policy.Protect(window);
                var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
                NativeMethods.SetWindowPos(handle, IntPtr.Zero, primary.X + 10, primary.Y + 10, 0, 0, NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
                var before = PolicyFakes.RectOf(handle);

                policy.KeepOffLed(window, neverPlaced: false);

                Assert.Equal(before, PolicyFakes.RectOf(handle));
                Assert.Equal(onlyLed, policy.ChooseMonitor());
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void A_window_over_the_LED_is_excluded_even_partly()
    {
        WpfTestHost.Run(() =>
        {
            var primary = PolicyFakes.Primary();
            var led = PolicyFakes.FakeLed();
            var policy = PolicyFakes.Policy(led, [primary, led], (0, 0));
            var window = new PlainWindow { ShowInTaskbar = false, Width = 400, Height = 300 };
            try
            {
                policy.Protect(window);
                var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;

                PolicyFakes.MoveTo(handle, led.X - 100, led.Y + 50); // 300 px of its 400 are over the LED, 100 are not
                policy.Reevaluate(window);

                Assert.True(CaptureExclusion.IsExcluded(handle));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void Moving_the_window_off_the_LED_makes_it_capturable_and_back_on_excludes_it_again()
    {
        WpfTestHost.Run(() =>
        {
            var primary = PolicyFakes.Primary();
            var led = PolicyFakes.FakeLed();
            var policy = PolicyFakes.Policy(led, [primary, led], (0, 0));
            var window = new PlainWindow { ShowInTaskbar = false, Width = 400, Height = 300 };
            try
            {
                policy.Protect(window);
                var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
                PolicyFakes.MoveTo(handle, led.X + 100, led.Y + 100);
                policy.Reevaluate(window);
                Assert.True(CaptureExclusion.IsExcluded(handle));

                // No explicit re-evaluation: the window's own LocationChanged does it.
                PolicyFakes.MoveTo(handle, primary.X + 50, primary.Y + 50);
                Assert.False(CaptureExclusion.IsExcluded(handle));

                PolicyFakes.MoveTo(handle, led.X + 100, led.Y + 100);
                Assert.True(CaptureExclusion.IsExcluded(handle));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void With_the_setting_off_every_window_is_excluded_even_away_from_the_LED_and_back_on_frees_it()
    {
        WpfTestHost.Run(() =>
        {
            var primary = PolicyFakes.Primary();
            var led = PolicyFakes.FakeLed();
            var inScreenshots = false;
            var policy = PolicyFakes.Policy(led, [primary, led], (0, 0), () => inScreenshots);
            var window = new PlainWindow { ShowInTaskbar = false, Width = 400, Height = 300 };
            try
            {
                Assert.True(policy.Protect(window));
                var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
                PolicyFakes.MoveTo(handle, primary.X + 50, primary.Y + 50);
                Assert.True(CaptureExclusion.IsExcluded(handle));

                inScreenshots = true;
                policy.Refresh();
                Assert.False(CaptureExclusion.IsExcluded(handle));

                inScreenshots = false;
                policy.Refresh();
                Assert.True(CaptureExclusion.IsExcluded(handle));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void A_closed_window_is_forgotten_and_a_refresh_does_not_touch_it()
    {
        WpfTestHost.Run(() =>
        {
            var primary = PolicyFakes.Primary();
            var policy = PolicyFakes.Policy(null, [primary], (0, 0));
            var window = new PlainWindow { ShowInTaskbar = false, Width = 100, Height = 100, Opacity = 0 };
            TestWindows.PlaceOnScreen(window);
            policy.Protect(window);
            window.Show();
            window.Close();

            policy.Refresh(); // would throw on a dead handle if it were still tracked
        });
    }
}
