using AlertaIBT.App.Platform;
using AlertaIBT.Core.Input;

namespace AlertaIBT.App.Tests.Platform;

public sealed class HotkeyServiceTests
{
    // An unusual combination so the test does not fight a real program for it.
    private static HotkeyGesture Gesture()
    {
        Assert.True(HotkeyGesture.TryParse("Ctrl+Alt+Shift+F23", out var gesture));
        return gesture;
    }

    [Fact]
    public void A_free_combination_registers_and_is_given_back_on_unregister()
    {
        WpfTestHost.Run(() =>
        {
            using var window = new MessageWindow();
            using var service = new HotkeyService(window);

            Assert.True(service.Register(Gesture()));
            Assert.Equal(Gesture(), service.Current);

            service.Unregister();
            Assert.Null(service.Current);

            // Given back: a second owner can take it.
            using var other = new MessageWindow();
            using var otherService = new HotkeyService(other);
            Assert.True(otherService.Register(Gesture()));
        });
    }

    [Fact]
    public void A_combination_owned_by_another_window_is_refused_and_leaves_no_hotkey()
    {
        WpfTestHost.Run(() =>
        {
            using var windowA = new MessageWindow();
            using var a = new HotkeyService(windowA);
            using var windowB = new MessageWindow();
            using var b = new HotkeyService(windowB);

            Assert.True(a.Register(Gesture()));
            Assert.False(b.Register(Gesture()));
            Assert.Null(b.Current);
        });
    }

    [Fact]
    public void Registering_again_replaces_the_previous_combination()
    {
        WpfTestHost.Run(() =>
        {
            using var window = new MessageWindow();
            using var service = new HotkeyService(window);
            Assert.True(HotkeyGesture.TryParse("Ctrl+Alt+Shift+F22", out var second));

            Assert.True(service.Register(Gesture()));
            Assert.True(service.Register(second));

            Assert.Equal(second, service.Current);
        });
    }

    [Fact]
    public void The_hotkey_message_raises_the_quick_panel_event()
    {
        WpfTestHost.Run(() =>
        {
            using var window = new MessageWindow();
            using var service = new HotkeyService(window);
            var raised = 0;
            service.QuickPanelPressed += () => raised++;
            Assert.True(service.Register(Gesture()));

            // WM_HOTKEY with the service's id, delivered to the hidden window like Windows does.
            const int wmHotkey = 0x0312;
            SendMessage(window.Handle, wmHotkey, 0xB11, 0);

            Assert.Equal(1, raised);

            // A different id (another program's hotkey on the same window) is ignored.
            SendMessage(window.Handle, wmHotkey, 0x1, 0);
            Assert.Equal(1, raised);
        });
    }

    [Fact]
    public void Two_hotkeys_on_one_window_each_hear_only_their_own_id()
    {
        WpfTestHost.Run(() =>
        {
            using var window = new MessageWindow();
            using var panel = new HotkeyService(window);
            using var mouse = new HotkeyService(window, HotkeyService.MouseLockHotkeyId);
            var panelPressed = 0;
            var mousePressed = 0;
            panel.Pressed += () => panelPressed++;
            mouse.Pressed += () => mousePressed++;
            Assert.True(HotkeyGesture.TryParse("Ctrl+Alt+Shift+F21", out var other));

            // Two different combinations on the same window, with their own ids: both register.
            Assert.True(panel.Register(Gesture()));
            Assert.True(mouse.Register(other));

            const int wmHotkey = 0x0312;
            SendMessage(window.Handle, wmHotkey, HotkeyService.MouseLockHotkeyId, 0);
            Assert.Equal((0, 1), (panelPressed, mousePressed));

            SendMessage(window.Handle, wmHotkey, HotkeyService.QuickPanelHotkeyId, 0);
            Assert.Equal((1, 1), (panelPressed, mousePressed));

            mouse.Unregister();
            Assert.Null(mouse.Current);
            Assert.NotNull(panel.Current); // giving back one leaves the other registered
        });
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, nint wParam, nint lParam);
}
