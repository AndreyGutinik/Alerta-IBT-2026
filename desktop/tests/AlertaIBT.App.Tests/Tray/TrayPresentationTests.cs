using System.Windows.Controls;
using AlertaIBT.App.Platform;
using AlertaIBT.App.Tray;
using AlertaIBT.App.Views;

namespace AlertaIBT.App.Tests.Tray;

// The tray shows the presentation mode: a check item and a tooltip line, no notification.
public sealed class TrayPresentationTests
{
    [Fact]
    public void The_menu_has_a_checkable_item_that_raises_the_toggle_and_never_lies()
    {
        WpfTestHost.Run(() =>
        {
            using var tray = new TrayService();
            var item = tray.PresentationItem;
            Assert.True(item.IsCheckable);
            Assert.Equal("Modo apresenta\U000000E7\U000000E3o (Ctrl+Alt+P)", (string)item.Header);
            Assert.Contains(item, tray.Menu.Items.OfType<MenuItem>());

            var raised = 0;
            tray.PresentationToggleRequested += () => raised++;
            item.RaiseEvent(new System.Windows.RoutedEventArgs(MenuItem.ClickEvent));

            Assert.Equal(1, raised);
            Assert.False(item.IsChecked); // the mode was refused (nothing set it): the check goes back

            tray.SetPresentation(true);
            Assert.True(item.IsChecked);
            Assert.True(tray.IsPresentationOn);
            tray.SetPresentation(false);
            Assert.False(item.IsChecked);
        });
    }

    [Fact]
    public void The_tooltip_says_the_mode_is_on_only_while_it_is()
    {
        WpfTestHost.Run(() =>
        {
            using var tray = new TrayService();
            Assert.DoesNotContain("Modo apresenta", tray.ToolTip);

            tray.SetPresentation(true);
            Assert.Contains("Modo apresenta\U000000E7\U000000E3o ligado", tray.ToolTip);

            tray.SetPresentation(false);
            Assert.DoesNotContain("ligado", tray.ToolTip);
        });
    }
}

public sealed class KeyCaptureInterpretationTests
{
    [Theory]
    [InlineData(0x10)]
    [InlineData(0x11)]
    [InlineData(0x12)]
    [InlineData(0xA2)]
    [InlineData(0x5B)]
    public void A_modifier_alone_keeps_the_dialog_waiting(int key)
    {
        Assert.Equal((null, null), KeyCaptureDialog.Interpret(key));
    }

    [Fact]
    public void A_usable_key_is_taken_escape_cancels_and_a_media_key_is_refused_with_a_note()
    {
        Assert.Equal((0x22, null), KeyCaptureDialog.Interpret(0x22));
        Assert.Equal((null, "cancel"), KeyCaptureDialog.Interpret(0x1B));

        var (take, note) = KeyCaptureDialog.Interpret(0xB3);
        Assert.Null(take);
        Assert.False(string.IsNullOrEmpty(note));
    }

    [Fact]
    public void The_key_of_an_event_is_read_through_system_and_ime_keys()
    {
        Assert.Equal(0x22, KeyCaptureDialog.VirtualKeyOf(System.Windows.Input.Key.PageDown, System.Windows.Input.Key.None, System.Windows.Input.Key.None));
        Assert.Equal(0x27, KeyCaptureDialog.VirtualKeyOf(System.Windows.Input.Key.System, System.Windows.Input.Key.Right, System.Windows.Input.Key.None));
        Assert.Equal(0x42, KeyCaptureDialog.VirtualKeyOf(System.Windows.Input.Key.ImeProcessed, System.Windows.Input.Key.None, System.Windows.Input.Key.B));
    }
}
