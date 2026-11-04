using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AlertaIBT.App.Platform;
using AlertaIBT.App.Tests.Platform;
using AlertaIBT.App.Tests.ViewModels;
using AlertaIBT.App.Tray;
using AlertaIBT.App.ViewModels;
using AlertaIBT.App.Views;
using AlertaIBT.Core.Diagnostics;
using AlertaIBT.Core.Ndi;
using AlertaIBT.Core.Screens;
using AlertaIBT.Core.Settings;
using AlertaIBT.Core.Temple;

namespace AlertaIBT.App.Tests.Views;

// Batch 41: "Templo menor" in the interface. Tabs and parts per combination of the gear list, the first visible tab, the green border, the card with the switch and the
// gear, the two dialogs and the Resolucao fields, at 900, 1100 and 1600 px. Set ALERTAIBT_RENDER_DIR to also get PNG pictures to look at.
public sealed class TempleUiTests
{
    private static readonly double[] Widths = [900, 1100, 1600];

    public static TheoryData<double> WindowWidths => new(Widths);

    private static AppWindowPolicy Policy() => PolicyFakes.Policy(null, [PolicyFakes.Primary()], (0, 0));

    private static TempleSettings Mode(bool on, params TempleFeature[] off) => new() { Enabled = on, Disabled = off.Select(TempleFeatures.Key).ToArray() };

    private sealed class Rig : IDisposable
    {
        private readonly SettingsRig _settings;
        private readonly TempleModeService _service;

        public Rig(double windowWidth, TempleSettings temple, Func<IReadOnlyList<TempleFeature>, IReadOnlyList<TempleFeature>?>? pick = null, Func<TemplePlan, bool>? confirm = null)
        {
            _settings = new SettingsRig(new AppSettings { Temple = temple });
            _service = new TempleModeService(_settings.Settings);
            State = new TempleViewState(_service);
            var gateway = new FakeGateway(_settings.Clock);
            var composer = new QuickPanelViewModel(gateway, _settings.Hub, clock: _settings.Clock);
            var home = new HomeViewModel(gateway, composer, clock: _settings.Clock);
            var correction = new CorrectionViewModel(_settings.Settings, _settings.Protector, _settings.Hub, _settings.Saver);
            var diagnostics = new DiagnosticsViewModel(new LogFiles(Path.Combine(_settings.Folder, "logs")), _ => { }, () => null);
            var updates = new FakeUpdateStatus();
            Temple = new TempleViewModel(_service, _settings.Settings, pick, confirm);
            var settings = new SettingsViewModel(_settings.Settings, _settings.Protector, _settings.Hub, _settings.Saver, diagnostics, () => Task.CompletedTask, updates: updates)
            {
                Temple = Temple,
            };
            ViewModel = new MainViewModel(_settings.Hub, home, correction, settings, _settings.Saver, updates: updates, temple: State);
            Window = new MainWindow(ViewModel, null);
            Root = (FrameworkElement)Window.Content;
            Layout(windowWidth);
        }

        public SettingsRig Settings => _settings;

        public TempleModeService Service => _service;

        public TempleViewState State { get; }

        public TempleViewModel Temple { get; }

        public MainViewModel ViewModel { get; }

        public MainWindow Window { get; }

        public FrameworkElement Root { get; }

        public void Layout(double width)
        {
            var size = new Size(width, 4600);
            Root.Width = width;
            Root.Height = 4600;
            Root.InvalidateMeasure();
            Root.Measure(size);
            Root.Arrange(new Rect(new Point(0, 0), size));
            Root.UpdateLayout();
        }

        public Rect BoundsOf(FrameworkElement element) =>
            element.TransformToAncestor(Root).TransformBounds(new Rect(element.RenderSize));

        public void Snapshot(string name, FrameworkElement? from = null, FrameworkElement? to = null)
        {
            var dir = Environment.GetEnvironmentVariable("ALERTAIBT_RENDER_DIR");
            if (string.IsNullOrEmpty(dir))
            {
                return;
            }

            var top = from is null ? 0 : Math.Max(0, BoundsOf(from).Top - 16);
            var bottom = to is null ? Math.Min(Root.ActualHeight, 1000) : BoundsOf(to).Bottom + 16;
            var width = (int)Math.Ceiling(Root.ActualWidth);
            var height = (int)Math.Ceiling(bottom - top);
            Render(Root, width, height, new Point(0, -top), dir, name);
        }

        public void Dispose()
        {
            Temple.Dispose();
            State.Dispose();
            _service.Dispose();
            ViewModel.Dispose();
            _settings.Dispose();
        }
    }

    private static void Render(FrameworkElement element, int width, int height, Point offset, string dir, string name)
    {
        Directory.CreateDirectory(dir);
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x0A, 0x0A, 0x0C)), null, new Rect(0, 0, width, height));
            var brush = new VisualBrush(element)
            {
                Stretch = Stretch.Fill,
                ViewboxUnits = BrushMappingMode.Absolute,
                Viewbox = new Rect(-offset.X, -offset.Y, width, height),
                ViewportUnits = BrushMappingMode.Absolute,
                Viewport = new Rect(0, 0, width, height),
            };
            context.DrawRectangle(brush, null, new Rect(0, 0, width, height));
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(dir, name + ".png"));
        encoder.Save(stream);
    }

    private static void RenderDialog(Window dialog, string name)
    {
        var dir = Environment.GetEnvironmentVariable("ALERTAIBT_RENDER_DIR");
        if (string.IsNullOrEmpty(dir))
        {
            return;
        }

        TestWindows.ShowOffScreen(dialog);
        try
        {
            var content = (FrameworkElement)dialog.Content;
            content.UpdateLayout();
            Render(content, (int)Math.Ceiling(dialog.Width), (int)Math.Ceiling(dialog.Height), new Point(0, 0), dir, name);
        }
        finally
        {
            dialog.Hide();
        }
    }

    private static bool Shown(UIElement element) => element.Visibility == Visibility.Visible;

    // ---- the tabs ----

    [Theory]
    [InlineData(MainTab.Home)]
    public void With_the_mode_off_everything_shows_and_the_window_opens_on_Inicio(MainTab expected)
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig(1100, Mode(false, TempleFeature.Alerts, TempleFeature.Ndi));

            Assert.Equal(expected, rig.ViewModel.SelectedTab);
            Assert.All(new UIElement[] { rig.Window.HomeTab, rig.Window.SomTab, rig.Window.NdiTab, rig.Window.ControlTab, rig.Window.SettingsTab }, tab => Assert.True(Shown(tab)));
            Assert.False(Shown(rig.Window.TempleBorder));
        });
    }

    [Fact]
    public void The_window_opens_on_the_first_visible_tab_Som_then_NDI_then_Controle_then_Configuracoes()
    {
        WpfTestHost.Run(() =>
        {
            using (var rig = new Rig(1100, Mode(true, TempleFeature.Alerts)))
            {
                Assert.Equal(MainTab.Som, rig.ViewModel.SelectedTab);
                Assert.False(Shown(rig.Window.HomeTab));
            }

            using (var rig = new Rig(1100, Mode(true, TempleFeature.Alerts, TempleFeature.Player, TempleFeature.StartupAudio)))
            {
                Assert.Equal(MainTab.Ndi, rig.ViewModel.SelectedTab);
                Assert.False(Shown(rig.Window.SomTab));
            }

            using (var rig = new Rig(1100, Mode(true, TempleFeature.Alerts, TempleFeature.Player, TempleFeature.StartupAudio, TempleFeature.Ndi)))
            {
                Assert.Equal(MainTab.Control, rig.ViewModel.SelectedTab);
                Assert.False(Shown(rig.Window.NdiTab));
            }

            using (var rig = new Rig(1100, Mode(true, [.. TempleFeatures.All])))
            {
                Assert.Equal(MainTab.Settings, rig.ViewModel.SelectedTab);
                Assert.False(Shown(rig.Window.ControlTab));
                Assert.True(Shown(rig.Window.SettingsTab));
            }
        });
    }

    [Fact]
    public void A_selected_tab_that_disappears_hands_over_to_the_first_visible_one_and_comes_back_with_the_function()
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig(1100, Mode(false));
            rig.ViewModel.SelectTabCommand.Execute(MainTab.Ndi);
            Assert.Equal(MainTab.Ndi, rig.ViewModel.SelectedTab);

            rig.Service.Apply(true, [TempleFeature.Ndi]);

            Assert.Equal(MainTab.Home, rig.ViewModel.SelectedTab);
            Assert.False(Shown(rig.Window.NdiTab));
            Assert.True(Shown(rig.Window.TempleBorder));

            rig.Service.SetOn(false);
            Assert.True(Shown(rig.Window.NdiTab));
            Assert.False(Shown(rig.Window.TempleBorder));
        });
    }

    // ---- the parts of the pages ----

    [Fact]
    public void Each_part_of_each_page_follows_its_function()
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig(1100, Mode(true, TempleFeature.Alerts, TempleFeature.Player, TempleFeature.Holyrics, TempleFeature.Gemini));
            var settings = rig.Window.SettingsPage;

            // Avisos off: their cards go; the cards that are not about alerts stay.
            Assert.All(new UIElement[] { settings.BarSection, settings.PreviewSection, settings.TelegramSection, settings.SiteSection, settings.AlertsSection, settings.HistorySection, settings.SiteStatusRow }, c => Assert.False(Shown(c)));
            Assert.All(new UIElement[] { settings.MouseSection, settings.QuickPanelSection, settings.UpdatesSection, settings.SystemSection, settings.TempleSection, settings.ResolutionSection }, c => Assert.True(Shown(c)));
            Assert.False(Shown(settings.GeminiSection));

            // The player is off, the startup volumes and outputs stay (so the Som tab stays).
            Assert.False(Shown(rig.Window.SomPage.PlayerHero));
            Assert.False(Shown(rig.Window.SomPage.AlarmsSection));
            Assert.True(Shown(rig.Window.SomPage.BottomRow));

            // The Holyrics card goes, the MA2 part stays.
            Assert.False(Shown(rig.Window.ControlPage.HolyricsCard));
            Assert.True(Shown(rig.Window.ControlPage.HeaderCard));
            Assert.True(Shown(rig.Window.ControlPage.MapCard));

            Assert.False(Shown(rig.Window.HomePage.CorrectionPanel));

            // Turning the mode off brings all of it back.
            rig.Service.SetOn(false);
            Assert.True(Shown(settings.BarSection));
            Assert.True(Shown(rig.Window.SomPage.PlayerHero));
            Assert.True(Shown(rig.Window.ControlPage.HolyricsCard));
            Assert.True(Shown(settings.GeminiSection));
        });
    }

    [Fact]
    public void The_first_card_that_remains_in_a_column_closes_the_gap_the_hidden_ones_left()
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig(1100, Mode(true, TempleFeature.Alerts, TempleFeature.Player));
            var som = rig.Window.SomPage;

            // The player and the alarms are gone: the bottom row is the first block under the title, with no extra 24 px above it.
            Assert.Equal(0, som.BottomRow.Margin.Top);

            rig.Service.SetOn(false);
            Assert.Equal(24, som.BottomRow.Margin.Top);
            Assert.Equal(24, som.AlarmsSection.Margin.Top);
        });
    }

    [Fact]
    public void With_both_Som_functions_off_the_Som_tab_and_its_pages_are_gone_and_with_both_control_ones_off_so_is_Controle()
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig(1100, Mode(true, TempleFeature.Player, TempleFeature.StartupAudio, TempleFeature.Ma2, TempleFeature.Holyrics));

            Assert.False(Shown(rig.Window.SomTab));
            Assert.False(Shown(rig.Window.ControlTab));
            Assert.True(Shown(rig.Window.HomeTab));
            Assert.True(Shown(rig.Window.NdiTab));
        });
    }

    // ---- the green border ----

    [Fact]
    public void The_border_is_thin_green_always_on_top_and_never_catches_the_mouse()
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig(1100, Mode(true, TempleFeature.Alerts));
            var border = rig.Window.TempleBorder;

            Assert.True(Shown(border));
            Assert.InRange(border.BorderThickness.Left, 1, 2);
            Assert.False(border.IsHitTestVisible);
            Assert.Same(Application.Current.FindResource("IbtTempleBrush"), border.BorderBrush);
            Assert.Equal("Templo menor ligado", AutomationProperties.GetName(border));
            var box = rig.BoundsOf(border);
            Assert.Equal(0, box.Left, 1);
            Assert.Equal(0, box.Top, 1);
            Assert.Equal(1100, box.Width, 1);
            rig.Snapshot("temple-border-1100");
        });
    }

    [Fact]
    public void The_quick_panel_shows_the_same_border_and_drops_the_rows_of_the_functions_that_are_off()
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig(1100, Mode(true, TempleFeature.Alerts, TempleFeature.Player));
            var gateway = new FakeGateway(rig.Settings.Clock);
            var panel = new QuickPanelViewModel(gateway, rig.Settings.Hub, clock: rig.Settings.Clock);
            panel.SetTemple(rig.State);

            Assert.False(panel.ShowPlayer);
            Assert.Null(panel.PlayerRow);
            Assert.False(panel.ShowAlertsOffNote); // the alerts are not part of this PC: no "Alertas desligados" note either
            Assert.True(panel.Temple.IsOn);

            rig.Service.SetOn(false);
            Assert.True(panel.Temple.AlertsVisible);
            Assert.False(panel.Temple.IsOn);
        });
    }

    [Fact]
    public void The_tray_menu_drops_Pausar_and_the_presentation_mode_with_their_functions()
    {
        WpfTestHost.Run(() =>
        {
            using var tray = new TrayService();

            tray.SetFunctionsVisible(alerts: false, holyrics: true);
            Assert.Equal(Visibility.Collapsed, tray.PauseItem.Visibility);
            Assert.Equal(Visibility.Visible, tray.PresentationItem.Visibility);

            tray.SetFunctionsVisible(alerts: true, holyrics: false);
            Assert.Equal(Visibility.Visible, tray.PauseItem.Visibility);
            Assert.Equal(Visibility.Collapsed, tray.PresentationItem.Visibility);
            Assert.Equal(Visibility.Visible, tray.MouseLockItem.Visibility);
        });
    }

    // ---- the card, the switch and the gear ----

    [Theory]
    [MemberData(nameof(WindowWidths))]
    public void The_card_has_the_switch_and_the_gear_aligned_with_icons_and_tooltips_and_the_resolution_card_the_two_sizes(double width)
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig(width, Mode(false));
            rig.ViewModel.SelectTabCommand.Execute(MainTab.Settings);
            rig.Layout(width);
            var page = rig.Window.SettingsPage;

            Assert.Equal("Templo menor", (string)page.TempleSection.Header);
            Assert.NotNull(page.TempleSection.Icon);
            Assert.False(string.IsNullOrWhiteSpace(page.TempleRow.Info));
            Assert.Equal(32, page.TempleGearButton.ActualWidth);
            Assert.Equal(32, page.TempleGearButton.ActualHeight);
            Assert.False(string.IsNullOrWhiteSpace(page.TempleGearButton.ToolTip as string));
            Assert.Equal("Fun\U000000E7\U000000F5es desligadas no templo menor", AutomationProperties.GetName(page.TempleGearButton));
            var gear = rig.BoundsOf(page.TempleGearButton);
            var toggle = rig.BoundsOf(page.TempleSwitch);
            Assert.True(gear.Right <= toggle.Left, "the gear sits beside the switch");
            Assert.Equal(gear.Top + (gear.Height / 2), toggle.Top + (toggle.Height / 2), 2);
            Assert.True(toggle.Right <= rig.BoundsOf(page.TempleRow).Right);
            Assert.True(rig.BoundsOf(page.TempleSection).Bottom + 24 <= rig.BoundsOf(page.ResolutionSection).Top + 0.5);

            // Resolucao: the two sizes with their boxes, aligned, nothing cut.
            Assert.Equal("Resolu\U000000E7\U000000E3o do tel\U000000E3o", (string)page.ResolutionSection.Header);
            Assert.Equal("2560", page.NormalWidthBox.Text);
            Assert.Equal("768", page.NormalHeightBox.Text);
            Assert.Equal("1920", page.TempleWidthBox.Text);
            Assert.Equal("1080", page.TempleHeightBox.Text);
            var section = rig.BoundsOf(page.ResolutionSection);
            foreach (var box in new FrameworkElement[] { page.NormalWidthBox, page.NormalHeightBox, page.TempleWidthBox, page.TempleHeightBox })
            {
                Assert.True(section.Contains(rig.BoundsOf(box)), box.Name + " is cut");
            }

            Assert.Equal(rig.BoundsOf(page.NormalWidthBox).Left, rig.BoundsOf(page.TempleWidthBox).Left, 1);
            Assert.Equal(rig.BoundsOf(page.NormalHeightBox).Right, rig.BoundsOf(page.TempleHeightBox).Right, 1);
            rig.Snapshot("temple-card-off-" + width, page.TempleSection, page.ResolutionSection);

            // The size in force is outlined in green (normal now), then the temple one with the mode on.
            Assert.Same(Application.Current.FindResource("IbtAccentBrush"), page.NormalSizeBlock.BorderBrush);
            Assert.NotSame(Application.Current.FindResource("IbtAccentBrush"), page.TempleSizeBlock.BorderBrush);
            rig.Service.SetOn(true);
            rig.Layout(width);
            Assert.Same(Application.Current.FindResource("IbtAccentBrush"), page.TempleSizeBlock.BorderBrush);
            Assert.NotSame(Application.Current.FindResource("IbtAccentBrush"), page.NormalSizeBlock.BorderBrush);
            Assert.True((bool)page.TempleSwitch.IsChecked!);
            rig.Snapshot("temple-card-on-" + width, page.TempleSection, page.ResolutionSection);
        });
    }

    [Theory]
    [MemberData(nameof(WindowWidths))]
    public void The_pictures_of_the_settings_tab_with_Avisos_and_Player_off_and_with_everything_off(double width)
    {
        WpfTestHost.Run(() =>
        {
            using (var rig = new Rig(width, Mode(true, TempleFeature.Alerts, TempleFeature.Player)))
            {
                rig.ViewModel.SelectTabCommand.Execute(MainTab.Settings);
                rig.Layout(width);
                rig.Snapshot("temple-settings-avisos-player-off-" + width, to: rig.Window.SettingsPage.ResolutionSection);
                rig.ViewModel.SelectTabCommand.Execute(MainTab.Som);
                rig.Layout(width);
                rig.Snapshot("temple-som-player-off-" + width, to: rig.Window.SomPage.BottomRow);
            }

            using (var rig = new Rig(width, Mode(true, [.. TempleFeatures.All])))
            {
                Assert.Equal(MainTab.Settings, rig.ViewModel.SelectedTab);
                rig.Layout(width);
                rig.Snapshot("temple-everything-off-" + width, to: rig.Window.SettingsPage.ResolutionSection);
                var page = rig.Window.SettingsPage;
                Assert.True(rig.BoundsOf(page.BackupSection).Top < 400, "the first card that remains is at the top of the column");
            }
        });
    }

    [Theory]
    [MemberData(nameof(WindowWidths))]
    public void With_cards_gone_the_settings_page_fills_two_balanced_columns_in_reading_order_or_one_when_narrow(double width)
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig(width, Mode(true, TempleFeature.Alerts, TempleFeature.Gemini));
            rig.ViewModel.SelectTabCommand.Execute(MainTab.Settings);
            rig.Layout(width);
            var page = rig.Window.SettingsPage;
            var flow = page.Columns;
            var cards = new FrameworkElement[] { page.MouseSection, page.QuickPanelSection, page.UpdatesSection, page.SystemSection, page.TempleSection, page.ResolutionSection };

            if (width >= 900 + 0 && flow.Columns == 2)
            {
                var left = cards.Where(c => flow.Placement[c] == 0).ToList();
                var right = cards.Where(c => flow.Placement[c] == 1).ToList();
                Assert.NotEmpty(left);
                Assert.NotEmpty(right); // no empty column
                Assert.Equal(rig.BoundsOf(left[0]).Top, rig.BoundsOf(right[0]).Top, 1);
                var a = left.Sum(c => rig.BoundsOf(c).Height);
                var b = right.Sum(c => rig.BoundsOf(c).Height);
                Assert.True(Math.Min(a, b) >= Math.Max(a, b) * 0.4, "the columns are balanced");
                foreach (var column in new[] { left, right })
                {
                    for (var i = 1; i < column.Count; i++)
                    {
                        Assert.Equal(rig.BoundsOf(column[i - 1]).Bottom + 24, rig.BoundsOf(column[i]).Top, 1); // no holes
                    }
                }

                // Reading order: a card never sits above one that comes before it in the page order.
                var order = cards.Select(c => (Top: rig.BoundsOf(c).Top, Left: rig.BoundsOf(c).Left)).ToList();
                Assert.True(order.Zip(order.Skip(1)).All(p => p.Second.Top > p.First.Top - 0.5 || p.Second.Left > p.First.Left));
            }
            else
            {
                Assert.Equal(1, flow.Columns);
                for (var i = 1; i < cards.Length; i++)
                {
                    Assert.Equal(rig.BoundsOf(cards[i - 1]).Bottom + 24, rig.BoundsOf(cards[i]).Top, 1);
                }
            }

            rig.Snapshot("temple-settings-balanced-" + width, to: page.ResolutionSection);
        });
    }

    [Theory]
    [MemberData(nameof(WindowWidths))]
    public void The_control_and_ndi_tabs_with_functions_off_leave_no_empty_half(double width)
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig(width, Mode(true, TempleFeature.Ma2));
            rig.ViewModel.SelectTabCommand.Execute(MainTab.Control);
            rig.Layout(width);
            var holyrics = rig.Window.ControlPage.HolyricsCard;
            Assert.True(Shown(holyrics));
            Assert.True(rig.BoundsOf(holyrics).Width >= Math.Min(1280, width - 172) - 48 - 1, "the card spans the page");
            Assert.True(rig.BoundsOf(holyrics).Top < 140, "it sits right under the title");
            rig.Snapshot("temple-control-ma2-off-" + width, to: holyrics);
        });
    }

    // ---- the flows of the switch and the gear ----

    [Fact]
    public void The_switch_changes_nothing_until_Confirmar_and_Cancelar_keeps_everything()
    {
        WpfTestHost.Run(() =>
        {
            TemplePlan? asked = null;
            using var rig = new Rig(1100, Mode(false, TempleFeature.Alerts, TempleFeature.Ndi), confirm: plan =>
            {
                asked = plan;
                return false;
            });

            rig.Temple.Toggle();

            Assert.NotNull(asked);
            Assert.Equal([TempleFeature.Alerts, TempleFeature.Ndi], asked!.Stops);
            Assert.Equal(new TargetSize(1920, 1080), asked.To);
            Assert.False(rig.Service.IsOn);
            Assert.False(rig.Settings.Settings.Current.Temple.Enabled);
            Assert.False(rig.Temple.IsOn);
        });
    }

    [Fact]
    public void Confirmar_turns_the_mode_on_and_the_same_switch_turns_it_off_with_its_own_confirmation_listing_what_starts()
    {
        WpfTestHost.Run(() =>
        {
            var plans = new List<TemplePlan>();
            using var rig = new Rig(1100, Mode(false, TempleFeature.Alerts), confirm: plan =>
            {
                plans.Add(plan);
                return true;
            });

            rig.Temple.Toggle();
            Assert.True(rig.Service.IsOn);
            Assert.True(rig.Temple.TempleActive);
            Assert.False(rig.Temple.NormalActive);

            rig.Temple.Toggle();
            Assert.False(rig.Service.IsOn);
            Assert.Equal([TempleFeature.Alerts], plans[1].Starts);
            Assert.Equal(new TargetSize(2560, 768), plans[1].To);
            Assert.True(rig.Temple.NormalActive);
        });
    }

    [Fact]
    public void The_first_time_the_gear_opens_first_with_nothing_marked_and_cancelling_it_cancels_the_switch()
    {
        WpfTestHost.Run(() =>
        {
            var seen = new List<int>();
            using var cancelled = new Rig(1100, Mode(false), pick: current =>
            {
                seen.Add(current.Count);
                return null;
            });
            cancelled.Temple.Toggle();
            Assert.Equal([0], seen);
            Assert.False(cancelled.Service.IsOn);

            using var picked = new Rig(1100, Mode(false), pick: _ => [TempleFeature.Alerts, TempleFeature.Player]);
            picked.Temple.Toggle();
            Assert.True(picked.Service.IsOn);
            Assert.Equal([TempleFeature.Alerts, TempleFeature.Player], picked.Service.Disabled);
        });
    }

    [Fact]
    public void Changing_the_gear_list_while_the_mode_is_on_asks_and_with_the_mode_off_only_saves_the_list()
    {
        WpfTestHost.Run(() =>
        {
            var plans = new List<TemplePlan>();
            using var rig = new Rig(1100, Mode(true, TempleFeature.Alerts), pick: _ => [TempleFeature.Alerts, TempleFeature.Ndi], confirm: plan =>
            {
                plans.Add(plan);
                return true;
            });

            rig.Temple.OpenGear();
            Assert.Single(plans);
            Assert.Equal([TempleFeature.Ndi], plans[0].Stops);
            Assert.Equal([TempleFeature.Alerts, TempleFeature.Ndi], rig.Service.Disabled);

            rig.Service.SetOn(false);
            plans.Clear();
            rig.Temple.OpenGear();
            Assert.Empty(plans); // the mode is off: nothing changes now, so nothing to confirm
        });
    }

    [Fact]
    public void The_size_boxes_save_valid_numbers_and_hold_the_others_with_a_hint()
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig(1100, Mode(false));

            rig.Temple.TempleWidth.Text = "1600";
            rig.Temple.TempleHeight.Text = "900";

            Assert.Equal(new TargetSize(1600, 900), rig.Settings.Settings.Current.Temple.TempleSize);
            Assert.Equal("1600 x 900", rig.Temple.TempleText);
            Assert.Equal("2560 x 768", rig.Temple.NormalText);

            rig.Temple.NormalWidth.Text = "99999";
            Assert.Equal(TempleSettings.MaxSide, rig.Settings.Settings.Current.Temple.NormalWidth);
            Assert.True(rig.Temple.NormalWidth.HasHint);
            rig.Temple.NormalWidth.Text = "abc";
            Assert.Equal(TempleSettings.MaxSide, rig.Settings.Settings.Current.Temple.NormalWidth); // letters never reach the settings
        });
    }

    // ---- the dialogs ----

    [Fact]
    public void The_gear_dialog_has_one_switch_per_function_with_icon_and_tooltip_and_nothing_marked_the_first_time()
    {
        WpfTestHost.Run(() =>
        {
            var dialog = TempleGearDialog.Create([], Policy());

            foreach (var feature in TempleFeatures.All)
            {
                var toggle = dialog.SwitchOf(feature);
                Assert.False(toggle.IsChecked);
                Assert.Equal(TempleFeatures.Title(feature), AutomationProperties.GetName(toggle));
                var row = (Border)((FrameworkElement)toggle.Parent).Parent;
                Assert.Equal(TempleFeatures.Detail(feature), row.ToolTip);
                Assert.IsType<System.Windows.Shapes.Path>(((Grid)toggle.Parent).Children[0]);
            }

            dialog.SwitchOf(TempleFeature.Alerts).IsChecked = true;
            dialog.SwitchOf(TempleFeature.Gemini).IsChecked = true;
            dialog.SaveButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.Equal([TempleFeature.Alerts, TempleFeature.Gemini], dialog.Answer);
            RenderDialog(TempleGearDialog.Create([TempleFeature.Alerts, TempleFeature.Player], Policy()), "temple-gear-dialog");

            var cancelled = TempleGearDialog.Create([TempleFeature.Ndi], Policy());
            Assert.True(cancelled.SwitchOf(TempleFeature.Ndi).IsChecked);
            cancelled.CancelButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.Null(cancelled.Answer);
        });
    }

    [Fact]
    public void The_confirmation_lists_what_stops_what_starts_and_the_new_size_and_Cancelar_is_the_default()
    {
        WpfTestHost.Run(() =>
        {
            var plan = TempleProfile.Plan(Mode(false), Mode(true, TempleFeature.Alerts, TempleFeature.Ndi));
            var dialog = TempleConfirmDialog.Create(plan, Policy());

            Assert.Equal("Ligar o Templo menor", dialog.Title);
            Assert.True(dialog.CancelButton.IsDefault);
            Assert.False(dialog.ConfirmButton.IsDefault);
            var texts = AllText((DependencyObject)dialog.Content);
            Assert.Contains("Avisos", texts);
            Assert.Contains("NDI / c\U000000E2mera no tel\U000000E3o", texts);
            Assert.Contains(texts, t => t.Contains("2560 x 768", StringComparison.Ordinal) && t.Contains("1920 x 1080", StringComparison.Ordinal));
            Assert.DoesNotContain("Volta a funcionar", texts);
            RenderDialog(dialog, "temple-confirm-dialog");

            var back = TempleConfirmDialog.Create(TempleProfile.Plan(Mode(true, TempleFeature.Alerts), Mode(false, TempleFeature.Alerts)), Policy());
            Assert.Equal("Desligar o Templo menor", back.Title);
            Assert.Contains("Volta a funcionar", AllText((DependencyObject)back.Content));
            back.ConfirmButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.True(back.Answer);
            RenderDialog(TempleConfirmDialog.Create(TempleProfile.Plan(Mode(true, TempleFeature.Alerts), Mode(false, TempleFeature.Alerts)), Policy()), "temple-confirm-off-dialog");
        });
    }

    private static List<string> AllText(DependencyObject root)
    {
        var list = new List<string>();
        void Walk(DependencyObject node)
        {
            if (node is TextBlock text)
            {
                list.Add(text.Text);
            }

            foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>())
            {
                Walk(child);
            }
        }

        Walk(root);
        return list;
    }

    // ---- NDI: the crop controls ----

    [Fact]
    public void A_1920x1080_camera_on_a_1920x1080_telao_has_no_vertical_crop_so_the_controls_go_and_on_the_led_they_stay()
    {
        WpfTestHost.Run(() =>
        {
            var lane = NdiLaneFor(1920, 1080);

            Assert.True(lane.CanMoveVertically); // the size of the picture is not known yet: as always

            lane.ReportSource(1920, 1080);
            Assert.False(lane.CanMoveVertically);
            Assert.Equal(16.0 / 9, lane.CropAspect, 6);

            lane.CropWidth = 2560;
            lane.CropHeight = 768;
            Assert.True(lane.CanMoveVertically);
            Assert.Equal(2560.0 / 768, lane.CropAspect, 6);

            lane.CropWidth = 1920;
            lane.CropHeight = 1080;
            lane.ReportSource(3840, 2160);
            Assert.False(lane.CanMoveVertically);
            lane.ReportSource(1280, 960);
            Assert.True(lane.CanMoveVertically); // 4:3 into 16:9 still overflows vertically
        });
    }

    private static NdiLaneViewModel NdiLaneFor(int width, int height)
    {
        var ctor = typeof(NdiLaneViewModel).GetConstructors(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public).Single();
        var lane = (NdiLaneViewModel)ctor.Invoke([null, true, (ThumbnailCopy)((Span<byte> _, out int w, out int h, out long v) =>
        {
            w = 0;
            h = 0;
            v = 0;
            return false;
        })]);
        lane.CropWidth = width;
        lane.CropHeight = height;
        return lane;
    }
}
