using System.Windows.Interop;
using AlertaIBT.App.Platform;
using AlertaIBT.App.Tests.Platform;
using AlertaIBT.App.Views;
using AlertaIBT.Core.Screens;

namespace AlertaIBT.App.Tests.Views;

// The smaller windows of the app: the quit confirmation and the notice about the old app. Both are out of every capture
// before their first pixel, like the quick panel and the main window.
public sealed class DialogWindowTests
{
    [Fact]
    public void The_exit_confirmation_is_excluded_from_capture_before_it_is_shown()
    {
        WpfTestHost.Run(() =>
        {
            // Never shown, and the Wpf.Ui box cannot be closed from outside (Close() throws): it is simply left to the collector.
            var box = ExitDialog.Create();

            var handle = new WindowInteropHelper(box).Handle;
            Assert.NotEqual(IntPtr.Zero, handle);
            Assert.False(box.IsVisible);
            Assert.True(CaptureExclusion.IsExcluded(handle));
            Assert.Equal("Sair do IBT Controller?", box.Title);
            Assert.Equal("Sair", box.PrimaryButtonText);
        });
    }

    [Fact]
    public void The_legacy_app_notice_is_excluded_from_capture_before_it_is_shown()
    {
        WpfTestHost.Run(() =>
        {
            var primary = PolicyFakes.Primary();
            var window = NoticeWindow.Create("Alerta-IBT antigo encontrado", "O app antigo esta aberto.", askYesNo: true, PolicyFakes.Policy(null, [primary], (0, 0)));
            try
            {
                var handle = new WindowInteropHelper(window).Handle;
                Assert.NotEqual(IntPtr.Zero, handle);
                Assert.False(window.IsVisible);
                Assert.True(CaptureExclusion.IsExcluded(handle));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void The_legacy_app_notice_opens_off_the_LED_when_the_mouse_is_on_it()
    {
        WpfTestHost.Run(() =>
        {
            var primary = PolicyFakes.Primary();
            var led = PolicyFakes.FakeLed();
            var window = NoticeWindow.Create("t", "m", askYesNo: false, PolicyFakes.Policy(led, [primary, led], PolicyFakes.CenterOf(led)));
            try
            {
                var rect = PolicyFakes.RectOf(new WindowInteropHelper(window).Handle);
                Assert.True(PolicyFakes.Inside(rect, PolicyFakes.WorkAreaOf(primary)));
                Assert.False(WindowMonitorChooser.OverlapsLed(led, rect.Left, rect.Top, rect.Right, rect.Bottom));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void The_notice_remembers_which_button_was_chosen()
    {
        WpfTestHost.Run(() =>
        {
            var policy = PolicyFakes.Policy(null, [PolicyFakes.Primary()], (0, 0));
            var yes = NoticeWindow.Create("t", "m", askYesNo: true, policy);
            var no = NoticeWindow.Create("t", "m", askYesNo: true, policy);

            yes.Finish(true);
            no.Finish(false);

            Assert.True(yes.Answer);
            Assert.False(no.Answer);
        });
    }
}
