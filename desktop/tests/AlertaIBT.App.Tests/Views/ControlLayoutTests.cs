using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AlertaIBT.App.Tests.ViewModels;
using AlertaIBT.App.Theme;
using AlertaIBT.App.ViewModels;
using AlertaIBT.App.Views;
using AlertaIBT.Core.Control;
using AlertaIBT.Core.Diagnostics;
using AlertaIBT.Core.Settings;

namespace AlertaIBT.App.Tests.Views;

// The real Controle page laid out at three window widths. Set ALERTAIBT_RENDER_DIR to also get PNG pictures of it in every state.
public sealed class ControlLayoutTests
{
    private static readonly double[] Widths = [900, 1100, 1600];

    public static TheoryData<double> WindowWidths => new(Widths);

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

    private sealed class Rig : IDisposable
    {
        private readonly ControlRig _control;

        public Rig(double windowWidth, Action<ControlRig>? setup = null)
        {
            _control = new ControlRig();
            setup?.Invoke(_control);
            var chrome = _control.Chrome;
            var gateway = new FakeGateway(chrome.Clock);
            var composer = new QuickPanelViewModel(gateway, chrome.Hub, clock: chrome.Clock);
            var home = new HomeViewModel(gateway, composer, clock: chrome.Clock);
            var correction = new CorrectionViewModel(chrome.Settings, chrome.Protector, chrome.Hub, chrome.Saver);
            var diagnostics = new DiagnosticsViewModel(new LogFiles(System.IO.Path.Combine(chrome.Folder, "logs")), _ => { }, () => null);
            var updates = new FakeUpdateStatus();
            var page = new SettingsViewModel(chrome.Settings, chrome.Protector, chrome.Hub, chrome.Saver, diagnostics, () => Task.CompletedTask, updates: updates);
            ViewModel = new MainViewModel(chrome.Hub, home, correction, page, chrome.Saver, updates: updates, control: _control.Vm);
            Window = new MainWindow(ViewModel, null);
            Root = (FrameworkElement)Window.Content;
            ViewModel.SelectTabCommand.Execute(MainTab.Control);
            Layout(windowWidth);
        }

        public ControlRig Engine => _control;

        public ControlViewModel Vm => _control.Vm;

        public MainViewModel ViewModel { get; }

        public MainWindow Window { get; }

        public FrameworkElement Root { get; }

        public ControlPage Page => Window.Control;

        public void Layout(double width)
        {
            var size = new Size(width, 4000);
            Root.InvalidateMeasure();
            Root.Measure(size);
            Root.Arrange(new Rect(new Point(0, 0), size));
            Root.UpdateLayout();
        }

        public Rect BoundsOf(FrameworkElement element) =>
            element.TransformToAncestor(Root).TransformBounds(new Rect(element.RenderSize));

        public T Named<T>(string name) where T : FrameworkElement => (T)Page.FindName(name) ?? Descendants(Page).OfType<T>().First(e => e.Name == name);

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
                context.DrawRectangle(new VisualBrush(Root) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, new Rect(0, 0, width, height));
            }

            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(System.IO.Path.Combine(dir, name + ".png"));
            encoder.Save(stream);
        }

        public void Dispose()
        {
            ViewModel.Dispose();
            _control.Chrome.Dispose();
        }
    }

    private static void WithToken(ControlRig engine)
    {
        engine.Vm.PasteHolyricsToken("TOKEN-FOR-LAYOUT-TESTS");
        engine.Vm.ApplyPendingToken();
        engine.TurnOn();
        engine.Vm.SetActive(true);
    }

    [Theory]
    [MemberData(nameof(WindowWidths))]
    public void The_holyrics_card_sits_between_the_header_and_the_map_and_nothing_runs_past_it(double width)
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig(width, WithToken);

            var header = rig.BoundsOf(rig.Named<CardSection>("HeaderCard"));
            var card = rig.BoundsOf(rig.Named<CardSection>("HolyricsCard"));
            var map = rig.BoundsOf(rig.Named<CardSection>("MapCard"));
            Assert.True(header.Bottom <= card.Top && card.Bottom <= map.Top);
            Assert.Equal(header.Width, card.Width, 1);

            var blocks = new[] { "HolyricsAddressBlock", "HolyricsTokenBlock", "HolyricsArrowsBlock" }.Select(n => rig.BoundsOf(rig.Named<Border>(n))).ToList();
            Assert.All(blocks, b => Assert.True(b.Width >= 320 && b.Left >= card.Left - 0.5 && b.Right <= card.Right + 0.5, $"a block {b}"));
            var grid = rig.Named<AdaptiveGrid>("HolyricsGrid");
            if (width >= 1100)
            {
                Assert.Equal(2, grid.Columns);
            }

            foreach (var name in new[] { "HolyricsHostBox", "HolyricsPortBox", "TokenLockedBox", "TokenLockButton", "HolyricsTestButton", "HolyricsArrowsSwitch", "HolyricsChip" })
            {
                var element = rig.Named<FrameworkElement>(name);
                var bounds = rig.BoundsOf(element);
                Assert.True(bounds.Width > 0 && bounds.Right <= card.Right + 0.5, name + " " + bounds);
            }

            // The paste and Trocar buttons are 32 px and keep inside their block.
            Assert.True(rig.BoundsOf(rig.Named<Button>("TokenPasteButton")).Height >= 32);
            Assert.True(rig.BoundsOf(rig.Named<Button>("TokenPasteButton")).Right <= blocks[1].Right);
            rig.Snapshot($"holyrics-{width}-guardado");
        });
    }

    [Theory]
    [MemberData(nameof(WindowWidths))]
    public void The_holyrics_card_without_a_token_and_when_the_test_fails_render(double width)
    {
        WpfTestHost.Run(() =>
        {
            using (var empty = new Rig(width, e => e.Vm.SetActive(true)))
            {
                Assert.True(empty.BoundsOf(empty.Named<TextBox>("TokenEntryBox")).Width > 0);
                empty.Snapshot($"holyrics-{width}-sem-token");
            }

            using var failed = new Rig(width, e =>
            {
                WithToken(e);
                e.HolyricsHandler.Down = true;
                e.Vm.TestHolyricsCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            });
            Assert.Equal("sem resposta", failed.Vm.HolyricsStatusText);
            failed.Snapshot($"holyrics-{width}-sem-resposta");

            using var denied = new Rig(width, e =>
            {
                WithToken(e);
                e.HolyricsHandler.Reply = _ => (System.Net.HttpStatusCode.Unauthorized, "{\"status\":\"error\",\"error\":{\"unauthorized_actions\":\"SetLogo,MediaPlayerAction\"}}");
                e.Vm.TestHolyricsCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            });
            denied.Snapshot($"holyrics-{width}-sem-permissao");

            using var ok = new Rig(width, e =>
            {
                WithToken(e);
                e.HolyricsHandler.Reply = a => (System.Net.HttpStatusCode.OK, a == "GetMediaPlayerInfo" ? "{\"status\":\"ok\",\"data\":{\"playing\":false}}" : "{\"status\":\"ok\"}");
                e.Vm.TestHolyricsCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            });
            ok.Snapshot($"holyrics-{width}-conectado");
        });
    }

    // A console that is on: the first packet (baseline), then channel 7 ON, camera 2 ON and the faders at different places.
    private static void Live(ControlRig engine)
    {
        engine.Targets.Levels[ControlAction.VolumePlayer] = 30;
        engine.Targets.Levels[ControlAction.VolumeWindows] = 80;
        engine.TurnOn();
        engine.Socket.Send(ControlRig.Levels());
        engine.Socket.Send(ControlRig.Levels((2, 255), (7, 255), (17, 128), (18, 40), (19, 204), (20, 90), (21, 200)));
        engine.Vm.SetActive(true);
    }

    private static void NoSignal(ControlRig engine)
    {
        engine.TurnOn();
        engine.Socket.Send(ControlRig.Levels());
        engine.Chrome.Clock.Advance(TimeSpan.FromSeconds(5));
        engine.Vm.SetActive(true);
    }

    [Theory]
    [MemberData(nameof(WindowWidths))]
    public void The_page_shows_the_header_then_the_map_then_the_help_in_that_order(double width)
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig(width, Live);

            var header = rig.BoundsOf(rig.Named<CardSection>("HeaderCard"));
            var map = rig.BoundsOf(rig.Named<CardSection>("MapCard"));
            var help = rig.BoundsOf(rig.Named<Border>("HelpCard"));

            Assert.True(header.Bottom <= map.Top, "header above the map");
            Assert.True(map.Bottom <= help.Top, "map above the help");
            Assert.Equal(header.Width, map.Width, 1);
            Assert.Equal(map.Width, help.Width, 1);
            Assert.Equal(21, rig.Vm.Rows.Count);
            rig.Snapshot($"controle-{width}-ao-vivo");
        });
    }

    [Theory]
    [MemberData(nameof(WindowWidths))]
    public void The_three_header_blocks_sit_in_a_row_when_wide_and_stack_when_narrow(double width)
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig(width, Live);

            var blocks = new[] { "SignalBlock", "UniverseBlock", "SourceBlock" }.Select(n => rig.BoundsOf(rig.Named<Border>(n))).ToList();

            Assert.All(blocks, b => Assert.True(b.Width >= 232, $"a block is {b.Width} wide"));
            var grid = rig.Named<AdaptiveGrid>("HeaderGrid");
            if (width >= 1100)
            {
                Assert.Equal(3, grid.Columns);
                Assert.Equal(blocks[0].Top, blocks[1].Top, 1);
                Assert.Equal(blocks[1].Top, blocks[2].Top, 1);
            }
            else
            {
                Assert.True(grid.Columns >= 1);
            }
        });
    }

    [Theory]
    [MemberData(nameof(WindowWidths))]
    public void Nothing_runs_past_the_card_in_a_row_of_the_map(double width)
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig(width, Live);

            var card = rig.BoundsOf(rig.Named<CardSection>("MapCard"));
            var list = rig.Named<ItemsControl>("RowList");
            Assert.Equal(21, list.Items.Count);
            foreach (var button in list.Descendants<ButtonBase>())
            {
                var bounds = rig.BoundsOf(button);
                if (bounds.Width > 0)
                {
                    Assert.True(bounds.Right <= card.Right + 0.5, $"a control ends at {bounds.Right} but the card at {card.Right} (window {width})");
                    Assert.True(bounds.Left >= card.Left, "a control starts left of the card");
                }
            }

            foreach (var box in list.Descendants<ComboBox>())
            {
                var bounds = rig.BoundsOf(box);
                if (bounds.Width > 0)
                {
                    Assert.True(bounds.Right <= card.Right + 0.5, "a list ends right of the card");
                }
            }
        });
    }

    [Fact]
    public void The_rows_line_up_in_columns_and_are_tall_enough_to_click()
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig(1100, Live);

            var list = rig.Named<ItemsControl>("RowList");
            var tests = list.Descendants<Button>().Where(b => AutomationProperties.GetName(b) == "Testar").Select(rig.BoundsOf).ToList();
            var switches = list.Descendants<ToggleButton>().Where(b => AutomationProperties.GetName(b) == "Ligar esta linha").Select(rig.BoundsOf).ToList();

            Assert.Equal(21, tests.Count);
            Assert.All(tests, t => Assert.Equal(tests[0].Left, t.Left, 1));
            Assert.All(switches, s => Assert.Equal(switches[0].Left, s.Left, 1));
            Assert.All(tests, t => Assert.True(t.Height >= 32));
            var tops = tests.Select(t => t.Top).ToList();
            Assert.True(tops.Zip(tops.Skip(1), (a, b) => b - a).All(step => step >= 48), "rows are at least 48 px apart");
        });
    }

    [Fact]
    public void Cards_are_lighter_than_the_page_with_a_thin_border_and_the_page_has_a_title()
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig(1100, Live);

            var page = ((SolidColorBrush)rig.Page.FindResource("IbtBackgroundBrush")).Color;
            foreach (var card in rig.Find<Border>().Where(b => b.BorderThickness == new Thickness(1) && b.CornerRadius.TopLeft == 12))
            {
                var color = ((SolidColorBrush)card.Background).Color;
                Assert.True(color.R + color.G + color.B > page.R + page.G + page.B, "a card is lighter than the page");
                Assert.NotNull(card.BorderBrush);
            }

            Assert.Contains(rig.Find<TextBlock>(), t => t.Text == "Controle");
            Assert.Contains(rig.Find<TextBlock>(), t => t.Text == "Controle pela MA2");
            Assert.Contains(rig.Find<TextBlock>(), t => t.Text == "Mapa de canais");
        });
    }

    [Fact]
    public void Every_button_and_switch_has_a_tooltip_and_a_name()
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig(1100, Live);

            foreach (var button in rig.Find<ButtonBase>().Where(b => b.Name != "DropButton" && b.TemplatedParent is not ComboBox && b.TemplatedParent is not Expander))
            {
                if (button.Visibility != Visibility.Visible)
                {
                    continue;
                }

                Assert.False(string.IsNullOrEmpty(AutomationProperties.GetName(button)), "a button without a name: " + button.Name);
                Assert.True(button.ToolTip is not null, "a button without a tooltip: " + button.Name + " " + AutomationProperties.GetName(button));
            }
        });
    }

    [Fact]
    public void The_menu_has_Controle_between_NDI_and_Configuracoes()
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig(1100);

            var names = new[] { "HomeTab", "SomTab", "NdiTab", "ControlTab", "SettingsTab" }
                .Select(n => rig.BoundsOf((Button)Descendants(rig.Root).OfType<Button>().First(b => b.Name == n)).Top)
                .ToList();

            Assert.Equal(names.OrderBy(top => top), names);
            Assert.Equal("Controle", AutomationProperties.GetName(Descendants(rig.Root).OfType<Button>().First(b => b.Name == "ControlTab")));
            Assert.True(rig.ViewModel.IsControlSelected);
            Assert.False(rig.ViewModel.IsSettingsSelected);
        });
    }

    [Fact]
    public void No_row_shows_em_breve_now_that_the_holyrics_actions_exist()
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig(1100, Live);

            var list = rig.Named<ItemsControl>("RowList");
            var chips = list.Descendants<Border>().Where(b => b.Name == "SoonChip" && b.Visibility == Visibility.Visible).ToList();

            Assert.Empty(chips);
        });
    }

    [Theory]
    [MemberData(nameof(WindowWidths))]
    public void The_states_render(double width)
    {
        WpfTestHost.Run(() =>
        {
            using (var off = new Rig(width))
            {
                off.Snapshot($"controle-{width}-desligado");
            }

            using (var none = new Rig(width, NoSignal))
            {
                none.Snapshot($"controle-{width}-sem-sinal");
            }

            using (var busy = new Rig(width, e =>
            {
                e.TurnOn();
                e.Socket.SetState(ArtNetState.PortInUse);
                e.Update(c => c with { SourceIp = "2.0.0.2" });
            }))
            {
                busy.Snapshot($"controle-{width}-porta-em-uso");
            }

            using (var dup = new Rig(width, e =>
            {
                Live(e);
                e.Update(c => c with { Rows = c.Rows.Select(r => r.Channel == 9 ? r with { Channel = 8 } : r).ToList() });
            }))
            {
                dup.Snapshot($"controle-{width}-canal-repetido");
            }

            using (var help = new Rig(width, Live))
            {
                help.Named<Expander>("HelpExpander").IsExpanded = true;
                help.Layout(width);
                help.Snapshot($"controle-{width}-ajuda-aberta");
                Assert.True(help.BoundsOf(help.Named<ItemsControl>("HelpMap")).Width > 200);
            }
        });
    }
}
