using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AlertaIBT.App.Tests.ViewModels;
using AlertaIBT.App.Theme;
using AlertaIBT.App.ViewModels;
using AlertaIBT.App.Views;
using AlertaIBT.Core.Diagnostics;

namespace AlertaIBT.App.Tests.Views;

// The presentation-mode blocks of the Holyrics card, laid out at three window widths. Set ALERTAIBT_RENDER_DIR to also get PNG pictures.
public sealed class PresentationLayoutTests
{
    public static TheoryData<double> WindowWidths => new(new[] { 900.0, 1100.0, 1600.0 });

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
        private readonly PresentationRig _rig;

        public Rig(double width, bool configured = true, Action<PresentationRig>? setup = null)
        {
            _rig = new PresentationRig(configured: configured);
            setup?.Invoke(_rig);
            var chrome = _rig.Chrome;
            var gateway = new FakeGateway(chrome.Clock);
            var composer = new QuickPanelViewModel(gateway, chrome.Hub, clock: chrome.Clock);
            var home = new HomeViewModel(gateway, composer, clock: chrome.Clock);
            var correction = new CorrectionViewModel(chrome.Settings, chrome.Protector, chrome.Hub, chrome.Saver);
            var diagnostics = new DiagnosticsViewModel(new LogFiles(System.IO.Path.Combine(chrome.Folder, "logs")), _ => { }, () => null);
            var updates = new FakeUpdateStatus();
            var page = new SettingsViewModel(chrome.Settings, chrome.Protector, chrome.Hub, chrome.Saver, diagnostics, () => Task.CompletedTask, updates: updates);
            ViewModel = new MainViewModel(chrome.Hub, home, correction, page, chrome.Saver, updates: updates, control: _rig.Vm);
            Window = new MainWindow(ViewModel, null);
            Root = (FrameworkElement)Window.Content;
            ViewModel.SelectTabCommand.Execute(MainTab.Control);
            Layout(width);
        }

        public PresentationRig Engine => _rig;

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

        public Rect BoundsOf(FrameworkElement element) => element.TransformToAncestor(Root).TransformBounds(new Rect(element.RenderSize));

        public T Named<T>(string name) where T : FrameworkElement => (T)Page.FindName(name) ?? Descendants(Page).OfType<T>().First(e => e.Name == name);

        public IEnumerable<T> Find<T>() where T : DependencyObject => Descendants(Page).OfType<T>();

        public void Snapshot(string name)
        {
            var dir = Environment.GetEnvironmentVariable("ALERTAIBT_RENDER_DIR");
            if (string.IsNullOrEmpty(dir))
            {
                return;
            }

            Directory.CreateDirectory(dir);
            var card = Named<CardSection>("HolyricsCard");
            var bounds = BoundsOf(card);
            var width = (int)Math.Ceiling(Root.ActualWidth);
            var top = (int)Math.Max(0, bounds.Top - 12);
            var height = (int)Math.Ceiling(bounds.Height + 24);
            var visual = new DrawingVisual();
            using (var context = visual.RenderOpen())
            {
                context.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x0A, 0x0A, 0x0C)), null, new Rect(0, 0, width, height));
                context.DrawRectangle(
                    new VisualBrush(Root) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top, ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(0, top, width, height), ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, width, height) },
                    null,
                    new Rect(0, 0, width, height));
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
            _rig.Dispose();
        }
    }

    [Theory]
    [MemberData(nameof(WindowWidths))]
    public void The_presentation_blocks_sit_inside_the_holyrics_card_and_nothing_is_cut(double width)
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig(width, setup: r => r.Vm.SetActive(true));
            var card = rig.BoundsOf(rig.Named<CardSection>("HolyricsCard"));
            var grid = rig.Named<BalancedColumns>("HolyricsGrid");
            var address = rig.BoundsOf(rig.Named<Border>("HolyricsAddressBlock"));
            var token = rig.BoundsOf(rig.Named<Border>("HolyricsTokenBlock"));
            var arrows = rig.BoundsOf(rig.Named<Border>("HolyricsArrowsBlock"));
            var switchBlock = rig.BoundsOf(rig.Named<Border>("PresentationSwitchBlock"));
            var keysBlock = rig.BoundsOf(rig.Named<Border>("PresentationKeysBlock"));

            Assert.All(new[] { switchBlock, keysBlock }, b => Assert.True(b.Width >= 320 && b.Left >= card.Left - 0.5 && b.Right <= card.Right + 0.5, $"block {b}"));
            if (width >= 1100)
            {
                Assert.Equal(2, grid.Columns);
                // Left: address, arrows, presentation. Right: token, keys. Bottoms within 8 px and nothing alone beside an empty cell.
                Assert.True(Math.Abs(address.Left - switchBlock.Left) < 0.5 && Math.Abs(arrows.Left - switchBlock.Left) < 0.5, "left column");
                Assert.True(Math.Abs(token.Left - keysBlock.Left) < 0.5 && token.Left > address.Right, "right column");
                Assert.True(Math.Abs(switchBlock.Bottom - keysBlock.Bottom) <= 8, $"bottoms {switchBlock.Bottom} {keysBlock.Bottom}");
                Assert.True(address.Top < arrows.Top && arrows.Top < switchBlock.Top && token.Top < keysBlock.Top, "order inside the columns");
                Assert.True(keysBlock.Height > token.Height && switchBlock.Height >= 56, "no collapsed block");
            }
            else
            {
                Assert.Equal(1, grid.Columns);
                var order = new[] { address, token, arrows, switchBlock, keysBlock };
                for (var i = 1; i < order.Length; i++)
                {
                    Assert.True(order[i].Top >= order[i - 1].Bottom - 0.5, $"one column, block {i} below block {i - 1}");
                    Assert.Equal(order[0].Left, order[i].Left, 1);
                }
            }

            foreach (var name in new[] { "PresentationSwitch", "PresentationHotkeyChip", "PresentationRestoreButton", "PresentationAddNextButton", "PresentationAddPreviousButton", "PresentationNextList", "PresentationPreviousList" })
            {
                var bounds = rig.BoundsOf(rig.Named<FrameworkElement>(name));
                Assert.True(bounds.Width > 0 && bounds.Height > 0 && bounds.Right <= card.Right + 0.5, name + " " + bounds);
            }

            Assert.True(rig.BoundsOf(rig.Named<Button>("PresentationAddNextButton")).Height >= 32);
            Assert.Equal(4, rig.Find<TextBlock>().Count(t => t.Text is "PgDn" or "PgUp" or "\U00002192" or "\U00002190"));
            rig.Snapshot($"presentation-{width}-desligado");
        });
    }

    [Theory]
    [MemberData(nameof(WindowWidths))]
    public void The_countdown_stop_block_shows_the_switch_the_seconds_and_a_live_chip_only_while_a_countdown_is_shown(double width)
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig(width, setup: r => r.Vm.SetActive(true));
            var card = rig.BoundsOf(rig.Named<CardSection>("HolyricsCard"));
            var block = rig.Named<Border>("CountdownStopBlock");
            var chip = rig.Named<Border>("CountdownLiveChip");

            Assert.Equal(Visibility.Collapsed, chip.Visibility); // nothing is shown by Holyrics: no chip
            var blockBounds = rig.BoundsOf(block);
            Assert.True(blockBounds.Width >= 320 && blockBounds.Height >= 56 && blockBounds.Left >= card.Left - 0.5 && blockBounds.Right <= card.Right + 0.5, $"block {blockBounds}");
            rig.Snapshot($"countdown-{width}-sem-cronometro");

            // A countdown of 7 s is on display in Holyrics: the chip appears with 00:07.
            rig.Engine.CountdownSource.Next = new AlertaIBT.Core.Holyrics.CountdownReading(AlertaIBT.Core.Holyrics.HolyricsOutcome.Ok, AlertaIBT.Core.Holyrics.CountdownKind.Panel, 7);
            rig.Engine.Look();
            rig.Layout(width);
            Assert.Equal(Visibility.Visible, chip.Visibility);
            Assert.Equal("00:07", rig.Named<TextBlock>("CountdownLiveText").Text);

            var names = new[] { "CountdownStopLabel", "CountdownLiveChip", "CountdownStopAtCombo", "CountdownStopSwitch" };
            var bounds = names.Select(n => rig.BoundsOf(rig.Named<FrameworkElement>(n))).ToList();
            var inner = rig.BoundsOf(block);
            Assert.All(bounds, b => Assert.True(b.Width > 0 && b.Left >= inner.Left && b.Right <= inner.Right + 0.5, $"{b} in {inner}"));
            for (var i = 1; i < bounds.Count; i++)
            {
                Assert.True(bounds[i].Left >= bounds[i - 1].Right - 0.5, $"{names[i]} does not overlap {names[i - 1]}");
            }

            var label = rig.Named<TextBlock>("CountdownStopLabel");
            var shown = label.ActualWidth;
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Assert.True(shown >= label.DesiredSize.Width - 0.5, "the label is not trimmed: " + shown + " of " + label.DesiredSize.Width);
            rig.Layout(width);
            Assert.True(rig.BoundsOf(rig.Named<ComboBox>("CountdownStopAtCombo")).Height >= 32);
            rig.Snapshot($"countdown-{width}-ao-vivo");

            // The seconds list: 1 to 10, 2 chosen; the switch off freezes the list and hides the chip.
            var combo = rig.Named<ComboBox>("CountdownStopAtCombo");
            Assert.Equal(10, combo.Items.Count);
            Assert.Equal(2, combo.SelectedItem);
            rig.Engine.Vm.CountdownStopEnabled = false;
            rig.Engine.Look();
            rig.Layout(width);
            Assert.False(combo.IsEnabled);
            Assert.Equal(Visibility.Collapsed, chip.Visibility);
            rig.Snapshot($"countdown-{width}-desligado");

            // The token may not read the countdown: the reason shows whole under the card.
            rig.Engine.Vm.CountdownStopEnabled = true;
            rig.Engine.CountdownSource.Next = new AlertaIBT.Core.Holyrics.CountdownReading(AlertaIBT.Core.Holyrics.HolyricsOutcome.Unauthorized, Message: "token sem permissão para GetCPInfo");
            rig.Engine.Look();
            rig.Layout(width);
            var problem = rig.Named<TextBlock>("CountdownProblemLine");
            Assert.Equal(Visibility.Visible, problem.Visibility);
            var pb = rig.BoundsOf(problem);
            Assert.True(pb.Right <= card.Right + 0.5, "problem " + pb + " card " + card);
            rig.Snapshot($"countdown-{width}-sem-permissao");
        });
    }

    [Theory]
    [MemberData(nameof(WindowWidths))]
    public void The_states_on_without_token_refused_and_many_keys_render(double width)
    {
        WpfTestHost.Run(() =>
        {
            using (var on = new Rig(width, setup: r =>
            {
                r.Vm.SetActive(true);
                r.Vm.PresentationOn = true;
            }))
            {
                Assert.True(on.Named<System.Windows.Controls.Primitives.ToggleButton>("PresentationSwitch").IsChecked);
                on.Snapshot($"presentation-{width}-ligado");
            }

            using (var refused = new Rig(width, configured: false, setup: r =>
            {
                r.Vm.SetActive(true);
                r.Vm.PresentationOn = true;
            }))
            {
                var toggle = refused.Named<System.Windows.Controls.Primitives.ToggleButton>("PresentationSwitch");
                Assert.False(toggle.IsEnabled);
                Assert.False(toggle.IsChecked);
                Assert.True(refused.Named<TextBlock>("PresentationReasonLine").Visibility == Visibility.Visible);
                var card = refused.BoundsOf(refused.Named<CardSection>("HolyricsCard"));
                Assert.True(refused.BoundsOf(refused.Named<TextBlock>("PresentationReasonLine")).Right <= card.Right + 0.5);
                refused.Snapshot($"presentation-{width}-sem-token");
            }

            using var many = new Rig(width, setup: r =>
            {
                r.Vm.SetActive(true);
                foreach (var key in new[] { 0x41, 0x42, 0x43, 0x44, 0x45, 0x46 })
                {
                    r.Vm.AddPresentationKey(next: true, key);
                }

                r.Vm.AddPresentationKey(next: false, 0x21);
            });
            many.Layout(width);
            var keys = many.BoundsOf(many.Named<Border>("PresentationKeysBlock"));
            var list = many.BoundsOf(many.Named<ItemsControl>("PresentationNextList"));
            Assert.True(list.Right <= keys.Right + 0.5 && list.Height > 32, "eight chips wrap to a second line inside the block");
            Assert.True(many.Named<TextBlock>("PresentationKeyHintLine").Visibility == Visibility.Visible);
            many.Snapshot($"presentation-{width}-teclas-cheias");
        });
    }
}
