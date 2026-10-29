using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AlertaIBT.App.Tests.ViewModels;
using AlertaIBT.App.ViewModels;
using AlertaIBT.App.Views;
using AlertaIBT.Core.Correction;
using AlertaIBT.Core.Diagnostics;
using AlertaIBT.Core.Input;
using AlertaIBT.Core.Settings;

namespace AlertaIBT.App.Tests.Views;

// The real Configuracoes page laid out at three window widths (the window is 900 wide at the least). Set ALERTAIBT_RENDER_DIR to also
// get PNG pictures of the whole page (every width, the folded areas open too) to look at.
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

    private static CardSection? SectionOf(DependencyObject element) => AncestorOf<CardSection>(element);

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

    private static Brush Resource(string key) => (Brush)Application.Current.FindResource(key);

    private static double Luminance(Brush brush)
    {
        var c = ((SolidColorBrush)brush).Color;
        return (0.2126 * c.R) + (0.7152 * c.G) + (0.0722 * c.B);
    }

    private static Border CardBorder(CardSection section) => (Border)VisualTreeHelper.GetChild(section, 0);

    private static Border BlockOf(SettingRow row) => (Border)VisualTreeHelper.GetChild(row, 0);

    private static TextBlock TitleOf(SettingRow row) => (TextBlock)row.Template.FindName("RowTitle", row);

    private static Border IconOf(SettingRow row) => (Border)row.Template.FindName("InfoIcon", row);

    private static bool Overlap(Rect a, Rect b) => a.Width > 0 && a.Height > 0 && b.Width > 0 && b.Height > 0 && a.IntersectsWith(Rect.Inflate(b, -0.5, -0.5));

    private static CardSection[] LeftCards(SettingsPage page) => [page.BarSection, page.PreviewSection, page.TelegramSection, page.SiteSection];

    private static CardSection[] RightCards(SettingsPage page) =>
        [page.AlertsSection, page.MouseSection, page.QuickPanelSection, page.HistorySection, page.UpdatesSection, page.GeminiSection, page.SystemSection];

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
    public void The_eleven_cards_exist_each_with_a_header_and_an_icon_and_go_down_two_columns_or_one(double width)
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig(width);
            var page = rig.Page;
            var left = LeftCards(page);
            var right = RightCards(page);

            Assert.Equal(
                ["Barra no LED", "Pr\U000000E9via no LED", "Telegram", "Site", "Alertas", "Mouse", "Painel r\U000000E1pido", "Hist\U000000F3rico", "Atualiza\U000000E7\U000000F5es", "Modelo do Gemini", "Sistema"],
                left.Concat(right).Select(s => (string)s.Header));
            Assert.All(left.Concat(right), s => Assert.NotNull(s.Icon));

            // The title and its icon are drawn at the top of the card.
            foreach (var section in left.Concat(right))
            {
                var title = (TextBlock)section.Template.FindName("HeaderText", section);
                var icon = (System.Windows.Shapes.Path)section.Template.FindName("HeaderIcon", section);
                Assert.Equal((string)section.Header, title.Text);
                Assert.True(rig.BoundsOf(title).Top >= rig.BoundsOf(section).Top);
                Assert.True(rig.BoundsOf(title).Bottom <= rig.BoundsOf(section).Top + 64);
                Assert.True(rig.BoundsOf(icon).Right <= rig.BoundsOf(title).Left);
            }

            // Each column goes down in order, with 24 px between the cards.
            foreach (var column in new[] { left, right })
            {
                for (var i = 1; i < column.Length; i++)
                {
                    Assert.Equal(rig.BoundsOf(column[i - 1]).Bottom + 24, rig.BoundsOf(column[i]).Top, 1);
                }
            }

            // Two columns from about 1000 px of window; one under it (the left column, then the right).
            if (width >= 1100)
            {
                Assert.Equal(2, page.Columns.Columns);
                Assert.True(rig.BoundsOf(page.RightColumn).Left >= rig.BoundsOf(page.LeftColumn).Right + 24 - 0.5);
                Assert.Equal(rig.BoundsOf(page.BarSection).Top, rig.BoundsOf(page.AlertsSection).Top, 1);
            }
            else
            {
                Assert.Equal(1, page.Columns.Columns);
                Assert.True(rig.BoundsOf(page.AlertsSection).Top >= rig.BoundsOf(page.SiteSection).Bottom);
            }

            // The diagnostics card is folded at the bottom, under both columns.
            Assert.True(rig.BoundsOf(page.DiagnosticsCard).Top >= Math.Max(rig.BoundsOf(left[^1]).Bottom, rig.BoundsOf(right[^1]).Bottom) + 24 - 0.5);
        });
    }

    [Fact]
    public void The_cards_are_lighter_than_the_page_with_a_thin_border_and_the_settings_are_blocks_lighter_again()
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig(1100);
            var page = rig.Page;

            Assert.True(Luminance(Resource("IbtSurfaceBrush")) >= Luminance(Resource("IbtBackgroundBrush")) + 8);
            Assert.True(Luminance(Resource("IbtSurfaceRaisedBrush")) > Luminance(Resource("IbtSurfaceBrush")) + 4);

            foreach (var section in LeftCards(page).Concat(RightCards(page)))
            {
                var border = CardBorder(section);
                Assert.Same(Resource("IbtSurfaceBrush"), border.Background);
                Assert.Equal(new Thickness(1), border.BorderThickness);
                Assert.Equal(12, border.CornerRadius.TopLeft);
                Assert.Equal(new Thickness(24), border.Padding);
            }

            foreach (var row in rig.Find<SettingRow>())
            {
                var block = BlockOf(row);
                Assert.Same(Resource("IbtSurfaceRaisedBrush"), block.Background);
                Assert.Equal(new Thickness(1), block.BorderThickness);
                Assert.Equal(10, block.CornerRadius.TopLeft);
            }

            Assert.Same(Resource("IbtSurfaceBrush"), page.DiagnosticsCard.Background);
        });
    }

    [Fact]
    public void Every_control_lives_in_the_card_it_belongs_to_and_every_row_has_an_icon()
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig(1100);
            var page = rig.Page;

            var expected = new (FrameworkElement Control, CardSection Section)[]
            {
                (page.TopOption, page.BarSection),
                (page.BottomOption, page.BarSection),
                (page.SizeSlider, page.BarSection),
                (page.SpeedSlider, page.BarSection),
                (page.BarAdvanced, page.BarSection),
                (page.Preview, page.PreviewSection),
                (page.ShowBarButton, page.PreviewSection),
                (page.TokenLockedBox, page.TelegramSection),
                (page.TokenLockButton, page.TelegramSection),
                (page.ChatBox, page.TelegramSection),
                (page.SiteSwitch, page.SiteSection),
                (page.SiteUrlBox, page.SiteSection),
                (page.SiteApiKeyBox, page.SiteSection),
                (page.SiteEmailBox, page.SiteSection),
                (page.SitePasswordLockedBox, page.SiteSection),
                (page.AlertsSwitch, page.AlertsSection),
                (page.MouseLockSwitch, page.MouseSection),
                (page.MouseLockHotkeyBox, page.MouseSection),
                (page.TripleCtrlSwitch, page.MouseSection),
                (page.HotkeyBox, page.QuickPanelSection),
                (page.ClearHistorySwitch, page.HistorySection),
                (page.AutoUpdateSwitch, page.UpdatesSection),
                (page.CheckUpdateButton, page.UpdatesSection),
                (page.UpdateResultLine, page.UpdatesSection),
                (page.ModelBox, page.GeminiSection),
                (page.ResetModelButton, page.GeminiSection),
                (page.HideFromLiveRow, page.SystemSection),
                (page.SiteStatusRow, page.SystemSection),
            };
            foreach (var (control, section) in expected)
            {
                Assert.Same(section, SectionOf(control));
            }

            // The settings that are a row, in the order of the page, each in its card and each with an icon.
            var rows = rig.Find<SettingRow>().ToList();
            Assert.Equal(11, rows.Count);
            Assert.Equal(
                ["Barra no LED", "Alertas", "Mouse", "Mouse", "Mouse", "Painel r\U000000E1pido", "Hist\U000000F3rico", "Atualiza\U000000E7\U000000F5es", "Atualiza\U000000E7\U000000F5es", "Sistema", "Sistema"],
                rows.Select(r => (string)SectionOf(r)!.Header));
            Assert.All(rows, r => Assert.NotNull(r.Icon));
        });
    }

    [Fact]
    public void The_model_of_gemini_is_its_own_card_with_the_default_and_a_reset_icon()
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig(1100);
            var page = rig.Page;

            Assert.Equal(GeminiPrompt.DefaultModel, page.ModelBox.Text);
            Assert.Equal("Voltar ao modelo padr\U000000E3o", page.ResetModelButton.ToolTip);
            Assert.Equal("Voltar ao modelo padr\U000000E3o", AutomationProperties.GetName(page.ResetModelButton));

            page.ModelBox.Text = "gemini-9-teste";
            rig.Layout(1100);
            Assert.Equal("gemini-9-teste", rig.ViewModel.Correction.Model);
            page.ResetModelButton.Command.Execute(null);
            Assert.Equal(GeminiPrompt.DefaultModel, rig.ViewModel.Correction.Model);
            Assert.Equal(GeminiPrompt.DefaultModel, page.ModelBox.Text);
        });
    }

    [Fact]
    public void The_keyboard_goes_through_the_controls_top_to_bottom_and_rows_and_cards_take_no_focus()
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig(1600);
            var page = rig.Page;

            // The visual tree order is the tab order (no TabIndex is set): the left column, then the right one.
            Control[] order =
            [
                page.TopOption, page.SizeSlider, page.SpeedSlider, page.ShowBarButton, page.ChatBox, page.SiteSwitch, page.SiteUrlBox, page.SiteApiKeyBox, page.SiteEmailBox,
                page.AlertsSwitch, page.MouseLockSwitch, page.MouseLockHotkeyBox, page.TripleCtrlSwitch, page.HotkeyBox, page.ClearHistorySwitch, page.AutoUpdateSwitch,
                page.CheckUpdateButton, page.ModelBox, page.ResetModelButton,
            ];

            Assert.Equal(order, Descendants(page).OfType<Control>().Where(c => order.Contains(c)).ToList());
            Assert.All(order, c => Assert.True(c.Focusable && c.IsTabStop));
            Assert.All(rig.Find<SettingRow>(), row => Assert.False(row.Focusable || row.IsTabStop));
            Assert.All(rig.Find<CardSection>(), section => Assert.False(section.Focusable || section.IsTabStop));
        });
    }

    [Theory]
    [MemberData(nameof(WindowWidths))]
    public void Every_row_has_the_same_structure_and_the_same_edges(double width)
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig(width);
            var page = rig.Page;
            var rows = rig.Find<SettingRow>().ToList();

            foreach (var row in rows)
            {
                // One template: a block with 16 px at the sides, a height floor, and the title with its own text.
                var block = BlockOf(row);
                Assert.Equal(new Thickness(16, 12, 16, 12), block.Padding);
                Assert.True(block.ActualHeight >= 56, row.Title + " is too short");
                Assert.Equal(row.Title, MainWindowTests.TextOf(TitleOf(row)));
                Assert.Equal(TextWrapping.Wrap, TitleOf(row).TextWrapping);

                // The title starts 17 px inside the row (16 of padding, 1 of border) and the control ends 17 px inside it, for every row.
                var bounds = rig.BoundsOf(row);
                var (title, control) = PartsOf(rig, row);
                Assert.Equal(bounds.Left + 17, rig.BoundsOf(title).Left, 1);
                Assert.Equal(bounds.Right - 17, rig.BoundsOf(control).Right, 1);
                var card = rig.BoundsOf(SectionOf(row)!);
                Assert.True(bounds.Left >= card.Left + 24 && bounds.Right <= card.Right - 24);
            }

            // The rows of a card are as wide as each other, and each starts 8 px under the one before.
            foreach (var section in RightCards(page).Concat(LeftCards(page)))
            {
                var inSection = rows.Where(r => ReferenceEquals(SectionOf(r), section)).ToList();
                Assert.True(inSection.Select(r => Math.Round(rig.BoundsOf(r).Width, 1)).Distinct().Count() <= 1);
                for (var i = 1; i < inSection.Count; i++)
                {
                    Assert.True(rig.BoundsOf(inSection[i]).Top >= rig.BoundsOf(inSection[i - 1]).Bottom + 8 - 0.5);
                }
            }

            // The switches share one right edge in a column, and every shortcut box has one width.
            Assert.Equal(rig.BoundsOf(page.MouseLockSwitch).Right, rig.BoundsOf(page.AlertsSwitch).Right, 1);
            Assert.Equal(rig.BoundsOf(page.MouseLockSwitch).Right, rig.BoundsOf(page.TripleCtrlSwitch).Right, 1);
            Assert.Equal(rig.BoundsOf(page.MouseLockSwitch).Right, rig.BoundsOf(page.ClearHistorySwitch).Right, 1);
            Assert.Equal(rig.BoundsOf(page.MouseLockSwitch).Right, rig.BoundsOf(page.AutoUpdateSwitch).Right, 1);
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

            // A hint under the lock shortcut makes one footer show; the advanced fields are open.
            rig.ViewModel.Settings.TryCaptureMouseLockHotkey(HotkeyModifiers.Control, 0x56);
            rig.Page.BarAdvanced.IsExpanded = true;
            rig.Layout(width);

            var page = rig.Page;
            var scroll = (ScrollViewer)page.Content;
            Assert.True(scroll.ExtentWidth <= scroll.ViewportWidth + 0.5, "horizontal overflow at " + width);
            var pageBounds = rig.BoundsOf(page);

            var sections = rig.Find<CardSection>().ToList();
            for (var i = 0; i < sections.Count; i++)
            {
                var card = rig.BoundsOf(sections[i]);
                Assert.True(card.Left >= pageBounds.Left - 0.5 && card.Right <= pageBounds.Right + 0.5, "card " + sections[i].Header + " leaves the page sideways");
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

            // Every box, button, toggle and slider that is on screen sits inside the card it belongs to.
            foreach (var control in rig.Find<Control>().Where(c => (c is TextBox or Button or System.Windows.Controls.Primitives.ToggleButton or Slider) && c.ActualWidth > 0 && c.ActualHeight > 0))
            {
                if (SectionOf(control) is { } card)
                {
                    Assert.True(Rect.Inflate(rig.BoundsOf(card), 0.5, 0.5).Contains(rig.BoundsOf(control)), AutomationProperties.GetName(control) + " leaves its card at " + width);
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

                rig.Page.BarAdvanced.IsExpanded = true;
                rig.ViewModel.Settings.Diagnostics.IsOpen = true;
                rig.ViewModel.Settings.TryCaptureMouseLockHotkey(HotkeyModifiers.Control, 0x56);
                rig.Layout(width);
                rig.Snapshot("settings-" + (int)width + "-aberto");
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

            foreach (var row in new[] { page.AlertsRow, page.MouseLockRow, page.MouseLockHotkeyRow, page.TripleCtrlRow, page.QuickPanelHotkeyRow, page.ClearHistoryRow, page.AutoUpdateRow })
            {
                var icon = IconOf(row);
                Assert.Equal(Visibility.Visible, icon.Visibility);
                Assert.Equal("Dicas", AutomationProperties.GetName(icon));
                Assert.False(string.IsNullOrWhiteSpace(row.Info));
                Assert.Equal(row.Info, (string)icon.ToolTip);
            }

            foreach (var row in new[] { page.PositionRow, page.VersionRow, page.HideFromLiveRow, page.SiteStatusRow })
            {
                Assert.Equal(Visibility.Collapsed, IconOf(row).Visibility);
            }
        });
    }

    [Fact]
    public void There_are_no_explanation_blocks_and_every_icon_only_control_has_a_tooltip_and_an_accessible_name()
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig(1100);
            var page = rig.Page;

            // The long words are in the (i) tooltips; nothing on the page is a paragraph.
            var longTexts = rig.Find<TextBlock>().Where(t => t.ActualHeight > 0 && t.Text.Length > 60).Select(t => t.Text).ToList();
            Assert.Empty(longTexts);

            FrameworkElement[] iconOnly = [page.ShowBarButton, page.TokenLockButton, page.SiteSwitch, page.CheckUpdateButton, page.ResetModelButton];
            Assert.All(iconOnly, control => Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(control))));
            Assert.All(iconOnly.Where(c => c != page.TokenLockButton), c => Assert.IsType<string>(c.ToolTip));
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
