using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AlertaIBT.App.Tests.ViewModels;
using AlertaIBT.App.ViewModels;
using AlertaIBT.App.Views;
using AlertaIBT.Core.Diagnostics;
using AlertaIBT.Core.Input;
using AlertaIBT.Core.Settings;

namespace AlertaIBT.App.Tests.Views;

// The real Configuracoes page laid out at three window widths (the window is 900 wide at the least). Set ALERTAIBT_RENDER_DIR to also
// get PNG pictures of the whole page (every width, Sistema cards included) to look at.
public sealed class SettingsLayoutTests
{
    private static readonly double[] Widths = [900, 1100, 1600];

    private sealed class Rig : IDisposable
    {
        private readonly SettingsRig _settings = new();

        public Rig(double windowWidth)
        {
            var gateway = new FakeGateway(_settings.Clock);
            var logs = Path.Combine(_settings.Folder, "logs");
            var composer = new QuickPanelViewModel(gateway, _settings.Hub, clock: _settings.Clock);
            var home = new HomeViewModel(gateway, composer, clock: _settings.Clock);
            var correction = new CorrectionViewModel(_settings.Settings, _settings.Protector, _settings.Hub, _settings.Saver);
            var diagnostics = new DiagnosticsViewModel(new LogFiles(logs), _ => { }, () => null);
            var updates = new FakeUpdateStatus();
            var settings = new SettingsViewModel(_settings.Settings, _settings.Protector, _settings.Hub, _settings.Saver, diagnostics, () => Task.CompletedTask, updates: updates);
            ViewModel = new MainViewModel(_settings.Hub, home, correction, settings, _settings.Saver, updates: updates);
            Window = new MainWindow(ViewModel, null);
            Root = (FrameworkElement)Window.Content;
            ViewModel.SelectTabCommand.Execute(MainTab.Settings);
            Layout(windowWidth);
        }

        public MainViewModel ViewModel { get; }

        public MainWindow Window { get; }

        public FrameworkElement Root { get; }

        public SettingsPage Page => Window.Settings;

        public double Width { get; private set; }

        // Tall enough for the whole page, so nothing is hidden behind the scroll bar.
        public void Layout(double width)
        {
            Width = width;
            var size = new Size(width, 2600);
            Root.InvalidateMeasure();
            Root.Measure(size);
            Root.Arrange(new Rect(new Point(0, 0), size));
            Root.UpdateLayout();
        }

        public Rect BoundsOf(FrameworkElement element) =>
            element.TransformToAncestor(Root).TransformBounds(new Rect(element.RenderSize));

        public IEnumerable<T> Find<T>() where T : DependencyObject => Descendants(Page).OfType<T>();

        public double ContentBottom => Page.TransformToAncestor(Root).Transform(new Point(0, ((ScrollViewer)Page.Content).ExtentHeight)).Y;

        public void Snapshot(string name)
        {
            var dir = Environment.GetEnvironmentVariable("ALERTAIBT_RENDER_DIR");
            if (string.IsNullOrEmpty(dir))
            {
                return;
            }

            Directory.CreateDirectory(dir);
            var width = (int)Math.Ceiling(Root.ActualWidth);
            var height = (int)Math.Ceiling(Math.Min(ContentBottom + 8, Root.ActualHeight));
            var visual = new DrawingVisual();
            using (var context = visual.RenderOpen())
            {
                context.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x0A, 0x0A, 0x0C)), null, new Rect(0, 0, width, height));
                context.DrawRectangle(
                    new VisualBrush(Root) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top },
                    null,
                    new Rect(0, 0, width, height));
            }

            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(dir, name + ".png"));
            encoder.Save(stream);
        }

        public void Dispose()
        {
            ViewModel.Dispose();
            _settings.Dispose();
        }

        private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
        {
            var count = VisualTreeHelper.GetChildrenCount(parent);
            for (var i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                yield return child;
                foreach (var nested in Descendants(child))
                {
                    yield return nested;
                }
            }
        }
    }


    public static TheoryData<double> WindowWidths => new(Widths);

    private static SettingsSection? SectionOf(DependencyObject element) => AncestorOf<SettingsSection>(element);

    private static SettingRow? RowOf(DependencyObject element) => AncestorOf<SettingRow>(element);

    private static T? AncestorOf<T>(DependencyObject element) where T : class
    {
        for (var node = VisualTreeHelper.GetParent(element); node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is T found)
            {
                return found;
            }
        }

        return null;
    }

    private static TextBlock TitleOf(SettingRow row) => (TextBlock)row.Template.FindName("RowTitle", row);

    private static Border IconOf(SettingRow row) => (Border)row.Template.FindName("InfoIcon", row);

    private static Border LineOf(SettingRow row) => (Border)VisualTreeHelper.GetChild(row, 0);

    private static bool Overlap(Rect a, Rect b) => a.Width > 0 && a.Height > 0 && b.Width > 0 && b.Height > 0 && a.IntersectsWith(Rect.Inflate(b, -0.5, -0.5));

    // The title and the control of a row: the two children of its panel.
    private static (FrameworkElement Title, FrameworkElement Control) PartsOf(Rig rig, SettingRow row)
    {
        var panel = rig.Find<SplitRowPanel>().Single(p => ReferenceEquals(RowOf(p), row));
        return ((FrameworkElement)panel.Children[0], (FrameworkElement)panel.Children[1]);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var nested in Descendants(child))
            {
                yield return nested;
            }
        }
    }

    [Theory]
    [MemberData(nameof(WindowWidths))]
    public void The_six_sections_exist_in_order_each_with_its_title(double width)
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig(width);
            var page = rig.Page;

            var sections = new[] { page.AlertsSection, page.MouseSection, page.QuickPanelSection, page.HistorySection, page.UpdatesSection, page.SystemSection };
            Assert.Equal(["Alertas", "Mouse", "Painel r\U000000E1pido", "Hist\U000000F3rico", "Atualiza\U000000E7\U000000F5es", "Sistema"], sections.Select(s => (string)s.Header));

            // The title is drawn at the top of its card.
            foreach (var section in sections)
            {
                var title = (TextBlock)section.Template.FindName("SectionHeader", section);
                Assert.Equal((string)section.Header, title.Text);
                Assert.True(rig.BoundsOf(title).Top >= rig.BoundsOf(section).Top);
                Assert.True(rig.BoundsOf(title).Bottom <= rig.BoundsOf(section).Top + 40);
            }

            // Alertas, Mouse, Painel rapido and Historico go down the right column; Atualizacoes and Sistema share the row under it.
            Assert.True(rig.BoundsOf(page.MouseSection).Top > rig.BoundsOf(page.AlertsSection).Bottom);
            Assert.True(rig.BoundsOf(page.QuickPanelSection).Top > rig.BoundsOf(page.MouseSection).Bottom);
            Assert.True(rig.BoundsOf(page.HistorySection).Top > rig.BoundsOf(page.QuickPanelSection).Bottom);
            Assert.True(rig.BoundsOf(page.UpdatesSection).Top > rig.BoundsOf(page.HistorySection).Bottom);
            Assert.Equal(rig.BoundsOf(page.UpdatesSection).Top, rig.BoundsOf(page.SystemSection).Top, 1);
        });
    }

    [Fact]
    public void Every_control_lives_in_the_section_it_belongs_to_and_nothing_is_outside_a_row()
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig(1100);
            var page = rig.Page;

            var expected = new (FrameworkElement Control, SettingsSection Section)[]
            {
                (page.AlertsSwitch, page.AlertsSection),
                (page.AlertsOffHint, page.AlertsSection),
                (page.MouseLockSwitch, page.MouseSection),
                (page.MouseLockHotkeyBox, page.MouseSection),
                (page.MouseLockHotkeyHintLine, page.MouseSection),
                (page.MouseLockHotkeyProblemLine, page.MouseSection),
                (page.TripleCtrlSwitch, page.MouseSection),
                (page.HotkeyBox, page.QuickPanelSection),
                (page.ClearHistorySwitch, page.HistorySection),
                (page.AutoUpdateSwitch, page.UpdatesSection),
                (page.CheckUpdateButton, page.UpdatesSection),
                (page.UpdateResultLine, page.UpdatesSection),
                (page.HideFromLiveRow, page.SystemSection),
                (page.SiteStatusRow, page.SystemSection),
            };
            foreach (var (control, section) in expected)
            {
                Assert.Same(section, SectionOf(control));
            }

            // Ten rows, each in one of the six sections, in the order of the owner's list.
            var rows = rig.Find<SettingRow>().ToList();
            Assert.Equal(10, rows.Count);
            Assert.Equal(
                ["Alertas", "Mouse", "Mouse", "Mouse", "Painel r\U000000E1pido", "Hist\U000000F3rico", "Atualiza\U000000E7\U000000F5es", "Atualiza\U000000E7\U000000F5es", "Sistema", "Sistema"],
                rows.Select(r => (string)SectionOf(r)!.Header));

            // No switch, box or button sits in a section outside a row.
            foreach (var section in rig.Find<SettingsSection>())
            {
                var loose = Descendants(section)
                    .Where(d => d is System.Windows.Controls.Primitives.ToggleButton or Button or TextBox)
                    .Where(d => RowOf(d) is null);
                Assert.Empty(loose);
            }
        });
    }

    [Fact]
    public void The_keyboard_goes_through_the_sistema_controls_top_to_bottom_and_rows_and_sections_take_no_focus()
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig(1100);
            var page = rig.Page;
            Control[] order =
            [
                page.AlertsSwitch,
                page.MouseLockSwitch,
                page.MouseLockHotkeyBox,
                page.TripleCtrlSwitch,
                page.HotkeyBox,
                page.ClearHistorySwitch,
                page.AutoUpdateSwitch,
                page.CheckUpdateButton,
            ];

            // The visual tree order is the tab order (no TabIndex is set).
            Assert.Equal(order, Descendants(page).OfType<Control>().Where(c => order.Contains(c)).ToList());
            Assert.All(order, c => Assert.True(c.Focusable && c.IsTabStop));
            Assert.All(rig.Find<SettingRow>(), row => Assert.False(row.Focusable || row.IsTabStop));
            Assert.All(rig.Find<SettingsSection>(), section => Assert.False(section.Focusable || section.IsTabStop));
        });
    }

    [Theory]
    [MemberData(nameof(WindowWidths))]
    public void Every_row_has_the_same_structure_the_same_line_and_the_same_edges(double width)
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig(width);
            var page = rig.Page;
            var rows = rig.Find<SettingRow>().ToList();

            foreach (var row in rows)
            {
                // One template: a thin line above only, the same padding and height floor, the title with its own text.
                var line = LineOf(row);
                Assert.Equal(new Thickness(0, 1, 0, 0), line.BorderThickness);
                Assert.Equal(new Thickness(0, 10, 0, 10), line.Padding);
                Assert.True(line.ActualHeight >= 46, row.Title + " is too short");
                Assert.Equal(row.Title, MainWindowTests.TextOf(TitleOf(row)));
                Assert.Equal(TextWrapping.Wrap, TitleOf(row).TextWrapping);

                // The title starts at the left edge of the row and the control ends at its right edge, for every row.
                var bounds = rig.BoundsOf(row);
                var (title, control) = PartsOf(rig, row);
                Assert.Equal(bounds.Left, rig.BoundsOf(title).Left, 1);
                Assert.Equal(bounds.Right, rig.BoundsOf(control).Right, 1);
                Assert.True(bounds.Left >= rig.BoundsOf(SectionOf(row)!).Left + 12 && bounds.Right <= rig.BoundsOf(SectionOf(row)!).Right - 12);
            }

            // The sections of a column are as wide as each other, so their rows have one width.
            foreach (var column in new[] { new[] { page.AlertsSection, page.MouseSection, page.QuickPanelSection, page.HistorySection }, new[] { page.UpdatesSection }, new[] { page.SystemSection } })
            {
                Assert.Single(rows.Where(r => column.Contains(SectionOf(r)!)).Select(r => Math.Round(rig.BoundsOf(r).Width, 1)).Distinct());
            }

            // Inside a section each row starts where the one before ended.
            foreach (var section in new[] { page.AlertsSection, page.MouseSection, page.UpdatesSection, page.SystemSection })
            {
                var inSection = rows.Where(r => ReferenceEquals(SectionOf(r), section)).ToList();
                for (var i = 1; i < inSection.Count; i++)
                {
                    Assert.Equal(rig.BoundsOf(inSection[i - 1]).Bottom, rig.BoundsOf(inSection[i]).Top, 1);
                }
            }

            // The switches share one right edge, and every shortcut box has one width.
            Assert.Equal(rig.BoundsOf(page.MouseLockSwitch).Right, rig.BoundsOf(page.AlertsSwitch).Right, 1);
            Assert.Equal(rig.BoundsOf(page.MouseLockSwitch).Right, rig.BoundsOf(page.TripleCtrlSwitch).Right, 1);
            Assert.Equal(rig.BoundsOf(page.MouseLockSwitch).Right, rig.BoundsOf(page.ClearHistorySwitch).Right, 1);
            Assert.Equal(rig.BoundsOf(page.MouseLockHotkeyBox).Width, rig.BoundsOf(page.HotkeyBox).Width, 1);
            Assert.Equal(rig.BoundsOf(page.AutoUpdateSwitch).Right, rig.BoundsOf(page.CheckUpdateButton).Right, 1);
        });
    }

    [Theory]
    [MemberData(nameof(WindowWidths))]
    public void Nothing_overlaps_or_leaves_its_card_or_the_page_and_nothing_needs_a_horizontal_scroll(double width)
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig(width);

            // A hint under the lock shortcut makes one footer show.
            rig.ViewModel.Settings.TryCaptureMouseLockHotkey(HotkeyModifiers.Control, 0x56);
            rig.Layout(width);

            var page = rig.Page;
            var scroll = (ScrollViewer)page.Content;
            Assert.True(scroll.ExtentWidth <= scroll.ViewportWidth + 0.5, "horizontal overflow at " + width);
            var pageBounds = rig.BoundsOf(page);

            var sections = rig.Find<SettingsSection>().ToList();
            for (var i = 0; i < sections.Count; i++)
            {
                Assert.True(Rect.Inflate(pageBounds, 0.5, 0.5).Contains(rig.BoundsOf(sections[i])), "section " + sections[i].Header + " leaves the page");
                for (var j = i + 1; j < sections.Count; j++)
                {
                    Assert.False(Overlap(rig.BoundsOf(sections[i]), rig.BoundsOf(sections[j])), sections[i].Header + " overlaps " + sections[j].Header);
                }
            }

            foreach (var row in rig.Find<SettingRow>())
            {
                var rowBounds = Rect.Inflate(rig.BoundsOf(row), 0.5, 0.5);
                var (titlePart, controlPart) = PartsOf(rig, row);
                var title = rig.BoundsOf(titlePart);
                var control = rig.BoundsOf(controlPart);
                Assert.True(title.Width > 0 && control.Width > 0, row.Title + ": empty");
                Assert.False(Overlap(title, control), row.Title + ": title and control overlap");
                Assert.True(rowBounds.Contains(title) && rowBounds.Contains(control), row.Title + ": leaves its row");

                // The (i) icon is inside its row and the footer starts below the title and the control.
                var icon = IconOf(row);
                if (icon.Visibility == Visibility.Visible)
                {
                    Assert.True(rowBounds.Contains(rig.BoundsOf(icon)), row.Title + ": icon leaves its row");
                }

                if (row.Footer is FrameworkElement { ActualHeight: > 0 } footer)
                {
                    Assert.True(rig.BoundsOf(footer).Top >= Math.Max(title.Bottom, control.Bottom) - 0.5, row.Title + ": footer overlaps");
                }
            }

            Assert.True(page.MouseLockHotkeyHintLine.ActualHeight > 0);
            Assert.True(rig.BoundsOf(page.MouseLockHotkeyHintLine).Top >= rig.BoundsOf(page.MouseLockHotkeyBox).Bottom);
            Assert.True(rig.BoundsOf(page.TripleCtrlRow).Top >= rig.BoundsOf(page.MouseLockHotkeyHintLine).Bottom);
        });
    }

    [Fact]
    public void Render_the_page_at_every_width_for_a_look()
    {
        WpfTestHost.Run(() =>
        {
            foreach (var width in Widths)
            {
                using var rig = new Rig(width);
                rig.Snapshot("settings-" + (int)width);
            }
        });
    }

    [Fact]
    public void The_info_icons_keep_their_tooltips_and_only_the_rows_that_had_one_show_it()
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig(1100);
            var page = rig.Page;

            foreach (var row in new[] { page.MouseLockRow, page.MouseLockHotkeyRow, page.TripleCtrlRow, page.QuickPanelHotkeyRow, page.ClearHistoryRow, page.AutoUpdateRow })
            {
                var icon = IconOf(row);
                Assert.Equal(Visibility.Visible, icon.Visibility);
                Assert.Equal("Dicas", AutomationProperties.GetName(icon));
                Assert.False(string.IsNullOrWhiteSpace(row.Info));
                Assert.Equal(row.Info, (string)icon.ToolTip);
            }

            foreach (var row in new[] { page.VersionRow, page.HideFromLiveRow, page.SiteStatusRow })
            {
                Assert.Equal(Visibility.Collapsed, IconOf(row).Visibility);
            }
        });
    }

    [Theory]
    [InlineData(420, false)]
    [InlineData(300, false)]
    [InlineData(200, true)]
    [InlineData(160, true)]
    public void The_split_row_panel_wraps_the_title_and_drops_the_control_under_it_instead_of_clipping(double width, bool stacked)
    {
        WpfTestHost.Run(() =>
        {
            var title = new TextBlock { Text = "Um titulo bem comprido que precisa quebrar em varias linhas", TextWrapping = TextWrapping.Wrap };
            var control = new Border { Width = 150, Height = 30 };
            var panel = new SplitRowPanel { Children = { title, control } };

            panel.Measure(new Size(width, double.PositiveInfinity));
            panel.Arrange(new Rect(0, 0, width, panel.DesiredSize.Height));

            var titleBox = new Rect(title.TranslatePoint(default, panel), title.RenderSize);
            var controlBox = new Rect(control.TranslatePoint(default, panel), control.RenderSize);
            Assert.Equal(width, panel.DesiredSize.Width, 1);
            Assert.True(titleBox.Right <= width + 0.5 && controlBox.Right <= width + 0.5, "clipped at " + width);
            Assert.Equal(width, controlBox.Right, 1); // always on the right edge
            if (stacked)
            {
                Assert.True(controlBox.Top >= titleBox.Bottom, "the control is under the title");
            }
            else
            {
                Assert.True(titleBox.Right <= controlBox.Left + 0.5, "the title ends before the control starts");
                Assert.True(titleBox.Width >= SplitRowPanel.MinTitleWidth - 0.5, "the title keeps its minimum width");
            }
        });
    }
}
