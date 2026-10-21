using AlertaIBT.App.Tray;

namespace AlertaIBT.App.Tests.Tray;

public sealed class QuickPanelFocusTests
{
    [Theory]
    [InlineData("Shell_TrayWnd")]
    [InlineData("Shell_SecondaryTrayWnd")]
    [InlineData("NotifyIconOverflowWindow")]
    [InlineData("TopLevelWindowForOverflowXamlIsland")]
    [InlineData("Progman")]
    [InlineData("WorkerW")]
    [InlineData("")]
    [InlineData(null)]
    public void Windows_of_the_shell_are_never_given_the_focus_back(string? className)
    {
        Assert.True(QuickPanelFocus.IsShellWindow(className));
    }

    [Theory]
    [InlineData("Chrome_WidgetWin_1")]
    [InlineData("SunAwtFrame")]
    [InlineData("GLFW30")]
    [InlineData("HwndWrapper[Holyrics.exe;;1234]")]
    public void The_window_of_a_program_is_given_the_focus_back(string className)
    {
        Assert.False(QuickPanelFocus.IsShellWindow(className));
    }

    [Fact]
    public void Nothing_is_restored_when_there_was_no_foreground_window_or_it_was_the_panel_itself()
    {
        Assert.Equal(IntPtr.Zero, QuickPanelFocus.WindowToRestore(IntPtr.Zero, new IntPtr(5)));
        Assert.Equal(IntPtr.Zero, QuickPanelFocus.WindowToRestore(new IntPtr(5), new IntPtr(5)));
    }

    [Fact]
    public void The_desktop_window_is_not_restored()
    {
        // The shell's own desktop window is a real window of class Progman on any Windows session.
        var desktop = NativeProbe.ShellWindow();

        Assert.Equal(IntPtr.Zero, QuickPanelFocus.WindowToRestore(desktop, new IntPtr(5)));
    }

    private static class NativeProbe
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr GetShellWindow();

        public static IntPtr ShellWindow() => GetShellWindow();
    }
}
