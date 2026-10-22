using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using AlertaIBT.App.Platform;
using AlertaIBT.App.Tray;

namespace AlertaIBT.App.Tests.Tray;

public sealed class TrayServiceTests
{
    [Fact]
    public void A_click_asks_for_the_quick_panel_and_a_double_click_for_the_main_window()
    {
        WpfTestHost.Run(() =>
        {
            using var tray = new TrayService();
            var events = new List<string>();
            tray.QuickPanelRequested += () => events.Add("panel");
            tray.MainWindowRequested += () => events.Add("window");

            tray.LeftClickCommand.Execute(null);
            tray.DoubleClickCommand.Execute(null);

            Assert.Equal(["panel", "window"], events);
        });
    }

    [Fact]
    public void The_menu_has_Abrir_Pausar_and_Sair_in_that_order_and_each_raises_its_event()
    {
        WpfTestHost.Run(() =>
        {
            using var tray = new TrayService();
            var events = new List<string>();
            tray.MainWindowRequested += () => events.Add("open");
            tray.PauseToggleRequested += () => events.Add("pause");
            tray.ExitRequested += () => events.Add("exit");

            var items = tray.Menu.Items.OfType<MenuItem>().ToList();
            Assert.Equal(["Abrir", "Pausar", "Sair"], items.Select(i => (string)i.Header));

            foreach (var item in items)
            {
                item.RaiseEvent(new System.Windows.RoutedEventArgs(MenuItem.ClickEvent));
            }

            Assert.Equal(["open", "pause", "exit"], events);
        });
    }

    [Fact]
    public void The_menu_popup_is_excluded_from_capture_when_it_opens()
    {
        WpfTestHost.Run(() =>
        {
            using var tray = new TrayService();
            tray.Menu.Placement = PlacementMode.Absolute;
            tray.Menu.HorizontalOffset = -30000;
            tray.Menu.VerticalOffset = -30000;
            try
            {
                tray.Menu.IsOpen = true;

                var source = Assert.IsType<HwndSource>(System.Windows.PresentationSource.FromVisual(tray.Menu));
                Assert.True(CaptureExclusion.IsExcluded(source.Handle));
            }
            finally
            {
                tray.Menu.IsOpen = false;
            }
        });
    }

    [Fact]
    public void Pausing_turns_the_menu_item_into_Retomar_and_back()
    {
        WpfTestHost.Run(() =>
        {
            using var tray = new TrayService();
            Assert.Equal("Pausar", tray.PauseItemText);
            Assert.False(tray.IsPaused);

            tray.SetPaused(true);
            Assert.Equal("Retomar", tray.PauseItemText);
            Assert.True(tray.IsPaused);

            tray.SetPaused(false);
            Assert.Equal("Pausar", tray.PauseItemText);
        });
    }

    [Fact]
    public void The_tooltip_names_the_app_the_pause_and_a_hotkey_that_does_not_work()
    {
        WpfTestHost.Run(() =>
        {
            using var tray = new TrayService();
            Assert.Equal("Alerta-IBT", tray.ToolTip);

            tray.SetHotkeyWarning("Atalho Ctrl+Alt+Espa\U000000E7o em uso por outro programa");
            Assert.Equal("Alerta-IBT\nAtalho Ctrl+Alt+Espa\U000000E7o em uso por outro programa", tray.ToolTip);

            tray.SetPaused(true);
            Assert.Equal("Alerta-IBT - pausado\nAtalho Ctrl+Alt+Espa\U000000E7o em uso por outro programa", tray.ToolTip);

            tray.SetHotkeyWarning(null);
            Assert.Equal("Alerta-IBT - pausado", tray.ToolTip);

            tray.SetPaused(false);
            tray.SetHotkeyWarning("  ");
            Assert.Equal("Alerta-IBT", tray.ToolTip);
        });
    }

    [Fact]
    public void The_tooltip_says_what_the_update_is_doing_and_goes_back_when_it_is_done()
    {
        WpfTestHost.Run(() =>
        {
            using var tray = new TrayService();

            tray.SetUpdateNotice("Baixando a atualiza\U000000E7\U000000E3o 8.0.1-pre.2\U00002026");
            Assert.Equal("Alerta-IBT\nBaixando a atualiza\U000000E7\U000000E3o 8.0.1-pre.2\U00002026", tray.ToolTip);

            tray.SetPaused(true);
            tray.SetHotkeyWarning("Atalho em uso");
            tray.SetUpdateNotice("Instalando e reiniciando\U00002026");
            Assert.Equal("Alerta-IBT - pausado\nAtalho em uso\nInstalando e reiniciando\U00002026", tray.ToolTip);

            tray.SetUpdateNotice(null);
            tray.SetHotkeyWarning(null);
            tray.SetPaused(false);
            Assert.Equal("Alerta-IBT", tray.ToolTip);
        });
    }

    [Fact]
    public void The_menu_items_carry_lucide_icons()
    {
        WpfTestHost.Run(() =>
        {
            using var tray = new TrayService();

            Assert.All(tray.Menu.Items.OfType<MenuItem>(), item => Assert.IsType<System.Windows.Shapes.Path>(item.Icon));
        });
    }
}
