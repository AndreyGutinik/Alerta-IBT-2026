using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AlertaIBT.App.Tests.ViewModels;
using AlertaIBT.App.Tray;
using AlertaIBT.App.ViewModels;
using AlertaIBT.App.Views;
using AlertaIBT.Core.Settings;

namespace AlertaIBT.App.Tests.Views;

// What the windows do when the master switch "Alertas ligados" is off: the quick panel keeps only the player, Inicio shows the banner with
// its button, the switch of Configuracoes follows the setting, and the setting is saved at once.
public sealed class AlertsOffUiTests
{
    private static void Layout(FrameworkElement root, double width)
    {
        root.InvalidateMeasure();
        root.Measure(new Size(width, 2000));
        root.Arrange(new Rect(0, 0, width, root.DesiredSize.Height));
        root.UpdateLayout();
    }

    private static void Snapshot(FrameworkElement root, string name, double width)
    {
        var dir = Environment.GetEnvironmentVariable("ALERTAIBT_RENDER_DIR");
        if (string.IsNullOrEmpty(dir))
        {
            return;
        }

        Directory.CreateDirectory(dir);
        var w = (int)Math.Ceiling(width);
        var h = (int)Math.Ceiling(root.ActualHeight);
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x0A, 0x0A, 0x0C)), null, new Rect(0, 0, w, h));
            context.DrawRectangle(new VisualBrush(root) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, new Rect(0, 0, w, h));
        }

        var bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(dir, name + ".png"));
        encoder.Save(stream);
    }

    [Fact]
    public void Render_the_alerts_off_screens_for_the_owner_to_look_at()
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new SomRig();
            var gateway = new FakeGateway(rig.Settings.Clock);
            var composer = new QuickPanelViewModel(gateway, rig.Settings.Hub, clock: rig.Settings.Clock);
            using var home = new HomeViewModel(gateway, composer, clock: rig.Settings.Clock);
            var page = new HomePage { DataContext = home, Background = new SolidColorBrush(Color.FromRgb(0x0A, 0x0A, 0x0C)) };
            rig.Settings.Hub.SetAlertsOn(false);
            Layout(page, 960);
            Snapshot(page, "inicio-alertas-desligados", 960);

            using var panel = new QuickPanelViewModel(gateway, rig.Settings.Hub, clock: rig.Settings.Clock, player: rig.PlayerView);
            var window = new QuickPanel(panel, null);
            var root = (FrameworkElement)window.Content;
            Layout(root, 354);
            Snapshot(root, "painel-rapido-alertas-desligados", 354);
            rig.Settings.Hub.SetAlertsOn(true);
            Layout(root, 354);
            Snapshot(root, "painel-rapido-alertas-ligados", 354);
        });
    }

    [Fact]
    public void The_quick_panel_view_model_follows_the_hub_for_the_alert_part()
    {
        using var rig = new SomRig();
        var gateway = new FakeGateway(rig.Settings.Clock);
        using var panel = new QuickPanelViewModel(gateway, rig.Settings.Hub, clock: rig.Settings.Clock, player: rig.PlayerView);
        var changed = new List<string>();
        panel.PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? string.Empty);
        Assert.True(panel.AlertsOn);
        Assert.False(panel.AlertsOff);

        rig.Settings.Hub.SetAlertsOn(false);

        Assert.False(panel.AlertsOn);
        Assert.True(panel.AlertsOff);
        Assert.Contains(nameof(QuickPanelViewModel.AlertsOn), changed);
        Assert.Contains(nameof(QuickPanelViewModel.AlertsOff), changed);
        Assert.True(panel.ShowPlayer); // the player keeps its row

        rig.Settings.Hub.SetAlertsOn(true);
        Assert.True(panel.AlertsOn);
    }

    [Fact]
    public void The_quick_panel_hides_the_alert_part_and_keeps_the_player_row_while_the_alerts_are_off()
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new SomRig();
            var gateway = new FakeGateway(rig.Settings.Clock);
            using var panel = new QuickPanelViewModel(gateway, rig.Settings.Hub, clock: rig.Settings.Clock, player: rig.PlayerView);
            var window = new QuickPanel(panel, null);
            var root = (FrameworkElement)window.Content;
            Layout(root, 354);
            Assert.Equal(Visibility.Visible, window.AlertsPart.Visibility);
            Assert.Equal(Visibility.Collapsed, window.AlertsOffNote.Visibility);
            var before = root.DesiredSize.Height;

            rig.Settings.Hub.SetAlertsOn(false);
            Layout(root, 354);

            Assert.Equal(Visibility.Collapsed, window.AlertsPart.Visibility);
            Assert.Equal(Visibility.Visible, window.AlertsOffNote.Visibility);
            Assert.True(root.DesiredSize.Height < before - 100, $"only the note and the player row are left: {before} -> {root.DesiredSize.Height}");
            Assert.NotNull(panel.PlayerRow);
            Assert.Contains(window.AlertsOffNote.Children.OfType<StackPanel>().Single().Children.OfType<TextBlock>(), t => t.Text.StartsWith("Alertas desligados", StringComparison.Ordinal));

            rig.Settings.Hub.SetAlertsOn(true);
            Layout(root, 354);
            Assert.Equal(Visibility.Visible, window.AlertsPart.Visibility);
            Assert.Equal(Visibility.Collapsed, window.AlertsOffNote.Visibility);
            Assert.Equal(before, root.DesiredSize.Height, 1);
        });
    }

    [Fact]
    public void Inicio_shows_the_banner_with_a_button_that_asks_for_the_alerts_to_be_turned_on_and_greys_the_rest()
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new SomRig();
            var gateway = new FakeGateway(rig.Settings.Clock);
            var composer = new QuickPanelViewModel(gateway, rig.Settings.Hub, clock: rig.Settings.Clock);
            using var home = new HomeViewModel(gateway, composer, clock: rig.Settings.Clock);
            var page = new HomePage { DataContext = home };
            Layout(page, 1000);
            Assert.Equal(Visibility.Collapsed, page.AlertsOffBanner.Visibility);
            Assert.True(page.PauseButton.IsEnabled);

            rig.Settings.Hub.SetAlertsOn(false);
            Layout(page, 1000);

            Assert.True(home.AlertsOff);
            Assert.Equal(Visibility.Visible, page.AlertsOffBanner.Visibility);
            Assert.False(page.PauseButton.IsEnabled);
            Assert.Contains(page.AlertsOffBanner.Child is Grid grid ? grid.Children.OfType<StackPanel>().First().Children.OfType<TextBlock>() : [], t => t.Text == "Alertas desligados");
            Assert.Equal("Ligar os alertas", System.Windows.Automation.AutomationProperties.GetName(page.TurnAlertsOnButton));

            page.TurnAlertsOnButton.Command.Execute(null);

            Assert.Equal([true], gateway.AlertSwitches);
        });
    }

    [Fact]
    public void The_alerts_switch_of_configuracoes_is_on_by_default_saves_at_once_and_follows_a_change_made_elsewhere()
    {
        using var rig = new SettingsRig();
        using var diagnostics = new DiagnosticsViewModel(new AlertaIBT.Core.Diagnostics.LogFiles(Path.Combine(rig.Folder, "logs")), _ => { }, () => null);
        using var settings = new SettingsViewModel(rig.Settings, rig.Protector, rig.Hub, rig.Saver, diagnostics, () => Task.CompletedTask);
        Assert.True(settings.AlertsEnabled);

        settings.AlertsEnabled = false;

        Assert.False(rig.Settings.Current.System.AlertsEnabled); // no 600 ms wait
        Assert.False(rig.OnDisk().System.AlertsEnabled);

        // The "Ligar os alertas" button of Inicio changes the setting: the switch follows without saving it again.
        rig.Settings.Update(s => s with { System = s.System with { AlertsEnabled = true } });
        Assert.True(settings.AlertsEnabled);
    }

    [Fact]
    public void The_alerts_card_has_the_switch_the_short_title_with_the_long_words_in_the_tooltip_and_the_off_hint_only_while_off()
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new SettingsRig();
            using var diagnostics = new DiagnosticsViewModel(new AlertaIBT.Core.Diagnostics.LogFiles(Path.Combine(rig.Folder, "logs")), _ => { }, () => null);
            using var settings = new SettingsViewModel(rig.Settings, rig.Protector, rig.Hub, rig.Saver, diagnostics, () => Task.CompletedTask);
            var page = new SettingsPage { DataContext = settings };
            Layout(page, 1000);

            Assert.Equal("Alertas", page.AlertsSection.Header);
            Assert.Equal("Alertas ligados", page.AlertsRow.Title);
            Assert.Contains("Barra no LED, Telegram, site e API do overlay.", page.AlertsRow.Info, StringComparison.Ordinal); // the long words are the tooltip
            Assert.True(page.AlertsSwitch.IsChecked);
            Assert.Equal(Visibility.Collapsed, page.AlertsOffHint.Visibility);

            settings.AlertsEnabled = false;
            Layout(page, 1000);

            Assert.False(page.AlertsSwitch.IsChecked);
            Assert.Equal(Visibility.Visible, page.AlertsOffHint.Visibility);
        });
    }
}
