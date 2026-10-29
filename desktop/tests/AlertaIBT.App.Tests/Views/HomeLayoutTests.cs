using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AlertaIBT.App.Tests.ViewModels;
using AlertaIBT.App.ViewModels;
using AlertaIBT.App.Views;
using AlertaIBT.Core.Alerts;
using AlertaIBT.Core.Diagnostics;
using AlertaIBT.Core.Queue;
using AlertaIBT.Core.Settings;

namespace AlertaIBT.App.Tests.Views;

// The real Inicio page laid out at three window widths, with a queue, a history and the folded Correcao card closed and open. Set
// ALERTAIBT_RENDER_DIR to also get PNG pictures of the whole page to look at.
public sealed class HomeLayoutTests
{
    private static readonly double[] Widths = [900, 1100, 1600];

    private sealed class Rig : IDisposable
    {
        private readonly SettingsRig _settings = new();

        public Rig(double windowWidth, bool busy = true)
        {
            Gateway = new FakeGateway(_settings.Clock);
            var logs = Path.Combine(_settings.Folder, "logs");
            var composer = new QuickPanelViewModel(Gateway, _settings.Hub, clock: _settings.Clock);
            var home = new HomeViewModel(Gateway, composer, clock: _settings.Clock);
            var correction = new CorrectionViewModel(_settings.Settings, _settings.Protector, _settings.Hub, _settings.Saver);
            var diagnostics = new DiagnosticsViewModel(new LogFiles(logs), _ => { }, () => null);
            var updates = new FakeUpdateStatus();
            var settings = new SettingsViewModel(_settings.Settings, _settings.Protector, _settings.Hub, _settings.Saver, diagnostics, () => Task.CompletedTask, updates: updates);
            ViewModel = new MainViewModel(_settings.Hub, home, correction, settings, _settings.Saver, updates: updates);
            Window = new MainWindow(ViewModel, null);
            Root = (FrameworkElement)Window.Content;
            if (busy)
            {
                _settings.Hub.SetLedPresent(true);
                _settings.Hub.SetShowing(true);
                Gateway.Queue.Enqueue(Item("a", "CRETA PRETO | PLACA: GBR-9003 | FAROL ACESO", AlertSource.Telegram, original: "#creta preto | placa:gbr- 9003 / farol acesso", corrected: true), DuplicatePolicy.Warn);
                Gateway.Queue.StartNext();
                Gateway.Queue.Enqueue(Item("b", "GOL BRANCO | PLACA: FFO-4745 | VIDRO ABERTO COM O NOME BEM COMPRIDO PARA CABER EM DUAS LINHAS", AlertSource.Site, original: "gol branco", corrected: true), DuplicatePolicy.Warn);
                Gateway.Queue.Enqueue(Item("c", "COROLLA PRATA | PLACA: BRA-2E19", AlertSource.Pc, urgent: true), DuplicatePolicy.Warn);
                Gateway.Queue.AddRejected(Item("d", "FOX PRATA | PLACA: ABC-1D54"), HistoryReasons.TooLong);
                Gateway.Queue.AddRejected(Item("e", "UNO PRETO | PLACA: BRA-2E19 | PNEU FURADO"), HistoryReasons.TooLong);
            }

            Layout(windowWidth);
        }

        public FakeGateway Gateway { get; }

        public MainViewModel ViewModel { get; }

        public MainWindow Window { get; }

        public FrameworkElement Root { get; }

        public HomePage Page => Window.Home;

        public CorrectionPanel Card => Window.Correction;

        public double Width { get; private set; }

        // Tall enough for the whole page, so nothing is hidden behind the scroll bar.
        public void Layout(double width)
        {
            Width = width;
            var size = new Size(width, 2600);
            Root.Width = width;
            Root.Height = 2600;
            Root.InvalidateMeasure();
            Root.Measure(size);
            Root.Arrange(new Rect(new Point(0, 0), size));
            Root.UpdateLayout();
        }

        public Rect BoundsOf(FrameworkElement element) =>
            element.TransformToAncestor(Root).TransformBounds(new Rect(element.RenderSize));

        public IEnumerable<T> Find<T>() where T : DependencyObject => Descendants(Page).OfType<T>();

        public double ContentBottom => Math.Max(BoundsOf(Card).Bottom, BoundsOf(Page.HistorySection).Bottom) + 24;

        public void Snapshot(string name)
        {
            var dir = Environment.GetEnvironmentVariable("ALERTAIBT_RENDER_DIR");
            if (string.IsNullOrEmpty(dir))
            {
                return;
            }

            Directory.CreateDirectory(dir);
            var width = (int)Math.Ceiling(Root.ActualWidth);
            var height = (int)Math.Ceiling(Math.Min(ContentBottom, Root.ActualHeight));
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

    private static QueueItem Item(string id, string text, AlertSource source = AlertSource.Pc, bool urgent = false, string? original = null, bool corrected = false) =>
        new(id, text, null, source, urgent, DateTimeOffset.UnixEpoch, null, original, corrected);

    private static Brush Resource(string key) => (Brush)Application.Current.FindResource(key);

    private static double Luminance(Brush brush)
    {
        var c = ((SolidColorBrush)brush).Color;
        return (0.2126 * c.R) + (0.7152 * c.G) + (0.0722 * c.B);
    }

    // Whether the element and all its parents are Visible (the window of the tests is never shown, so IsVisible is always false).
    private static bool Shown(DependencyObject element)
    {
        for (DependencyObject? node = element; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is UIElement { Visibility: not Visibility.Visible })
            {
                return false;
            }
        }

        return true;
    }

    private static bool Overlap(Rect a, Rect b) => a.Width > 0 && a.Height > 0 && b.Width > 0 && b.Height > 0 && a.IntersectsWith(Rect.Inflate(b, -0.5, -0.5));

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

    [Theory]
    [MemberData(nameof(WindowWidths))]
    public void The_cards_go_down_two_columns_or_one_with_24_px_between_them_and_the_correction_is_under_Mensagem(double width)
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig(width);
            var page = rig.Page;
            CardSection[] left = [page.NowSection, page.MessageSection];
            CardSection[] right = [page.QueueSection, page.HistorySection];

            Assert.Equal(["No tel\U000000E3o", "Mensagem", "Fila (2)", "Hist\U000000F3rico"], left.Concat(right).Select(s => s.Header is string h ? h : s.Header.ToString()));
            Assert.All(left.Concat(right), s => Assert.NotNull(s.Icon));

            Assert.Equal(rig.BoundsOf(page.NowSection).Bottom + 24, rig.BoundsOf(page.MessageSection).Top, 1);
            Assert.Equal(rig.BoundsOf(page.MessageSection).Bottom + 24, rig.BoundsOf(rig.Card).Top, 1);
            Assert.Equal(rig.BoundsOf(page.QueueSection).Bottom + 24, rig.BoundsOf(page.HistorySection).Top, 1);
            Assert.Equal(rig.BoundsOf(page.MessageSection).Left, rig.BoundsOf(rig.Card).Left, 1);
            Assert.Equal(rig.BoundsOf(page.MessageSection).Width, rig.BoundsOf(rig.Card).Width, 1);

            if (width >= 1100)
            {
                Assert.Equal(2, page.Columns.Columns);
                Assert.True(rig.BoundsOf(page.RightColumn).Left >= rig.BoundsOf(page.LeftColumn).Right + 24 - 0.5);
                Assert.Equal(rig.BoundsOf(page.NowSection).Top, rig.BoundsOf(page.QueueSection).Top, 1);
            }
            else
            {
                Assert.Equal(1, page.Columns.Columns);
                Assert.True(rig.BoundsOf(page.QueueSection).Top >= rig.BoundsOf(rig.Card).Bottom);
            }
        });
    }

    [Fact]
    public void The_correction_card_is_closed_when_the_app_opens_and_the_arrow_opens_it_smoothly_in_place()
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig(1100);
            var card = rig.Card;

            Assert.False(card.CorrectionExpander.IsExpanded);
            var closed = rig.BoundsOf(card);
            Assert.True(closed.Height < 90, "closed it is one header line");

            card.CorrectionExpander.IsExpanded = true;
            rig.Layout(1100);
            var open = rig.BoundsOf(card);
            Assert.Equal(closed.Top, open.Top, 1);
            Assert.True(open.Height > closed.Height + 250);

            card.CorrectionExpander.IsExpanded = false;
            rig.Layout(1100);
            Assert.Equal(closed.Height, rig.BoundsOf(card).Height, 1);
        });
    }

    [Fact]
    public void The_cards_are_lighter_than_the_page_with_a_thin_border_and_the_correction_blocks_lighter_again()
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig(1100);
            var page = rig.Page;
            Assert.True(Luminance(Resource("IbtSurfaceBrush")) >= Luminance(Resource("IbtBackgroundBrush")) + 8);

            foreach (var section in new[] { page.NowSection, page.MessageSection, page.QueueSection, page.HistorySection })
            {
                var border = (Border)VisualTreeHelper.GetChild(section, 0);
                Assert.Same(Resource("IbtSurfaceBrush"), border.Background);
                Assert.Equal(new Thickness(1), border.BorderThickness);
                Assert.Equal(12, border.CornerRadius.TopLeft);
                Assert.Equal(new Thickness(24), border.Padding);
            }

            var correction = (Border)rig.Card.Content;
            Assert.Same(Resource("IbtSurfaceBrush"), correction.Background);
            Assert.Equal(12, correction.CornerRadius.TopLeft);

            // The queue and history rows are blocks too.
            var rows = rig.Find<Border>().Where(b => b.Style is not null && ReferenceEquals(b.Background, Resource("IbtSurfaceRaisedBrush")) && AncestorOf<ItemsControl>(b) is not null).ToList();
            Assert.True(rows.Count >= 4);
        });
    }

    [Theory]
    [MemberData(nameof(WindowWidths))]
    public void Nothing_overlaps_or_leaves_its_card_or_the_page_and_nothing_needs_a_horizontal_scroll(double width)
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig(width);
            rig.Card.CorrectionExpander.IsExpanded = true;
            rig.Card.RulesBox.Text = "farol acesso => farol aceso\nlixo";
            rig.Layout(width);

            var page = rig.Page;
            var scroll = page.PageScroll;
            Assert.True(scroll.ExtentWidth <= scroll.ViewportWidth + 0.5, "horizontal overflow at " + width);
            var pageBounds = rig.BoundsOf(page);

            var sections = rig.Find<CardSection>().ToList();
            Assert.Equal(4, sections.Count);
            CardSection[] cards = [.. sections];
            for (var i = 0; i < cards.Length; i++)
            {
                Assert.True(Rect.Inflate(pageBounds, 0.5, 0.5).Contains(rig.BoundsOf(cards[i])), "card " + cards[i].Header + " leaves the page");
                for (var j = i + 1; j < cards.Length; j++)
                {
                    Assert.False(Overlap(rig.BoundsOf(cards[i]), rig.BoundsOf(cards[j])), cards[i].Header + " overlaps " + cards[j].Header);
                }

                Assert.False(Overlap(rig.BoundsOf(cards[i]), rig.BoundsOf(rig.Card)), cards[i].Header + " overlaps the correction");
            }

            // Every box, button, toggle and slider on screen sits inside its card.
            foreach (var control in rig.Find<Control>().Where(c => (c is TextBox or Button or ToggleButton) && c.ActualWidth > 0 && c.ActualHeight > 0))
            {
                var card = (FrameworkElement?)AncestorOf<CardSection>(control) ?? AncestorOf<CorrectionPanel>(control);
                if (card is not null)
                {
                    Assert.True(Rect.Inflate(rig.BoundsOf(card), 0.5, 0.5).Contains(rig.BoundsOf(control)), AutomationProperties.GetName(control) + " leaves its card at " + width);
                }
            }

            // In the correction: the key box, then the key icon, then the Gemini and the offline icons, all on one line inside one block.
            var key = rig.BoundsOf(rig.Card.KeyLockedBox.ActualWidth > 0 ? rig.Card.KeyLockedBox : rig.Card.KeyEntryBox);
            var gemini = rig.BoundsOf(rig.Card.GeminiOption);
            var offline = rig.BoundsOf(rig.Card.OfflineOption);
            Assert.True(key.Right <= gemini.Left);
            Assert.True(gemini.Right <= offline.Left);
            Assert.Equal(key.Top + (key.Height / 2), gemini.Top + (gemini.Height / 2), 1);
            Assert.Equal(gemini.Top, offline.Top, 1);
            Assert.True(key.Width >= 120, "the key box keeps a usable width at " + width);
        });
    }

    [Fact]
    public void Every_icon_only_control_has_a_tooltip_and_an_accessible_name_and_the_header_is_pause_only()
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig(1100);
            var page = rig.Page;
            rig.Card.CorrectionExpander.IsExpanded = true;
            rig.Layout(1100);

            FrameworkElement[] iconOnly =
            [
                page.NextButton, page.EndButton, page.ClearQueueButton, page.ClearHistoryButton, rig.Card.GeminiOption, rig.Card.OfflineOption,
            ];
            Assert.All(iconOnly, control => Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(control)), control.GetType().Name));
            Assert.All(iconOnly, control => Assert.False(string.IsNullOrWhiteSpace(control.ToolTip as string), AutomationProperties.GetName(control) + " has no tooltip"));

            // The row buttons of the queue and the history (up, down, remove, replay) have both too.
            var rowButtons = rig.Find<Button>().Where(b => b.ActualWidth > 0 && AncestorOf<ItemsControl>(b) is not null).ToList();
            Assert.True(rowButtons.Count >= 6);
            Assert.All(rowButtons, b => Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(b))));
            Assert.All(rowButtons, b => Assert.False(string.IsNullOrWhiteSpace(b.ToolTip as string)));

            // No explanation paragraph anywhere (the long words are tooltips).
            var longTexts = rig.Find<TextBlock>().Where(t => Shown(t) && t.Text.Length > 70 && !t.Text.StartsWith("Corrigido", StringComparison.Ordinal) && AncestorOf<ItemsControl>(t) is null).Select(t => t.Text).ToList();
            Assert.True(longTexts.Count == 0, string.Join(" | ", longTexts));
        });
    }

    [Fact]
    public void Nothing_on_the_bar_says_so_and_the_message_shows_when_something_is()
    {
        WpfTestHost.Run(() =>
        {
            using (var rig = new Rig(1100, busy: false))
            {
                Assert.True(rig.Page.NowEmpty.ActualHeight > 0);
                Assert.Equal("Nada no tel\U000000E3o agora.", rig.Page.NowEmpty.Text);
                rig.Snapshot("home-1100-vazio");
            }

            using (var rig = new Rig(1100))
            {
                Assert.Equal(Visibility.Collapsed, rig.Page.NowEmpty.Visibility);
                Assert.Equal("CRETA PRETO | PLACA: GBR-9003 | FAROL ACESO", rig.Page.CurrentMessage.Text);
            }
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
                rig.Snapshot("home-" + (int)width + "-fechado");

                rig.Card.CorrectionExpander.IsExpanded = true;
                rig.Card.RulesBox.Text = "farol acesso => farol aceso\nlixo";
                rig.ViewModel.Correction.GeminiKey.Entry = string.Empty;
                rig.Layout(width);
                rig.Snapshot("home-" + (int)width + "-correcao-aberta");

                rig.ViewModel.Correction.UseGemini = false;
                rig.Layout(width);
                rig.Snapshot("home-" + (int)width + "-correcao-offline");
            }
        });
    }
}
