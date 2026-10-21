using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AlertaIBT.App.Platform;
using AlertaIBT.App.Tests.Platform;
using AlertaIBT.App.Tests.ViewModels;
using AlertaIBT.App.Tray;
using AlertaIBT.App.ViewModels;
using AlertaIBT.Core.Alerts;
using AlertaIBT.Core.Queue;
using AlertaIBT.Core.Screens;
using AlertaIBT.Core.Services;
using Microsoft.Extensions.Time.Testing;

namespace AlertaIBT.App.Tests.Tray;

// The window is built and laid out without being shown, so these tests never touch the screen (or the keyboard focus).
// Set ALERTAIBT_RENDER_DIR to also get PNG pictures of the states, to look at them.
public sealed class QuickPanelWindowTests
{
    private static readonly Size PanelSize = new(354, double.PositiveInfinity);

    private sealed class Rig : IDisposable
    {
        public Rig(AppWindowPolicy? policy = null)
        {
            Clock = new FakeTimeProvider();
            Hub = new StatusHub();
            Gateway = new FakeGateway(Clock);
            ViewModel = new QuickPanelViewModel(Gateway, Hub, clock: Clock);
            Window = new QuickPanel(ViewModel, policy);
            Root = (FrameworkElement)Window.Content;
            Layout();
        }

        public FakeTimeProvider Clock { get; }

        public StatusHub Hub { get; }

        public FakeGateway Gateway { get; }

        public QuickPanelViewModel ViewModel { get; }

        public QuickPanel Window { get; }

        public FrameworkElement Root { get; }

        public void Layout()
        {
            Root.InvalidateMeasure();
            Root.Measure(PanelSize);
            Root.Arrange(new Rect(0, 0, PanelSize.Width, Root.DesiredSize.Height)); // as the window does: its fixed width, the content height
            Root.UpdateLayout();
        }

        public void Dispose() => ViewModel.Dispose();

        public IEnumerable<T> Find<T>() where T : DependencyObject => Descendants(Root).OfType<T>();

        public Rect BoundsOf(FrameworkElement element) =>
            element.TransformToAncestor(Root).TransformBounds(new Rect(element.RenderSize));

        public void Snapshot(string name)
        {
            var dir = Environment.GetEnvironmentVariable("ALERTAIBT_RENDER_DIR");
            if (string.IsNullOrEmpty(dir))
            {
                return;
            }

            Directory.CreateDirectory(dir);
            var width = (int)Math.Ceiling(Root.ActualWidth) + 40;
            var height = (int)Math.Ceiling(Root.ActualHeight) + 40;
            var visual = new DrawingVisual();
            using (var context = visual.RenderOpen())
            {
                context.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x1B, 0x2A, 0x4A)), null, new Rect(0, 0, width, height));
                context.DrawRectangle(new VisualBrush(Root), null, new Rect(20, 20, Root.ActualWidth, Root.ActualHeight));
            }

            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(dir, name + ".png"));
            encoder.Save(stream);
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

    [Fact]
    public void The_panel_is_excluded_from_capture_before_it_is_ever_shown()
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig();

            var handle = new System.Windows.Interop.WindowInteropHelper(rig.Window).Handle;
            Assert.NotEqual(IntPtr.Zero, handle);
            Assert.False(rig.Window.IsVisible);
            Assert.True(CaptureExclusion.IsExcluded(handle));
        });
    }

    [Fact]
    public void With_the_mouse_on_the_LED_the_panel_opens_at_the_tray_of_the_primary_monitor_not_on_the_LED()
    {
        WpfTestHost.Run(() =>
        {
            var primary = PolicyFakes.Primary();
            var led = PolicyFakes.FakeLed();
            using var rig = new Rig(PolicyFakes.Policy(led, [primary, led], PolicyFakes.CenterOf(led)));
            var handle = new System.Windows.Interop.WindowInteropHelper(rig.Window).Handle;

            rig.Window.PositionNearTray();

            var rect = PolicyFakes.RectOf(handle);
            var work = PolicyFakes.WorkAreaOf(primary);
            Assert.True(PolicyFakes.Inside(rect, work));
            Assert.Equal(work.Right, rect.Right); // bottom right of the work area
            Assert.Equal(work.Bottom, rect.Bottom);
            Assert.False(WindowMonitorChooser.OverlapsLed(led, rect.Left, rect.Top, rect.Right, rect.Bottom));
        });
    }

    [Fact]
    public void With_the_mouse_on_an_ordinary_monitor_the_panel_opens_on_that_monitor()
    {
        WpfTestHost.Run(() =>
        {
            var primary = PolicyFakes.Primary();
            using var rig = new Rig(PolicyFakes.Policy(null, [primary], PolicyFakes.CenterOf(primary)));
            var handle = new System.Windows.Interop.WindowInteropHelper(rig.Window).Handle;

            rig.Window.PositionNearTray();

            Assert.True(PolicyFakes.Inside(PolicyFakes.RectOf(handle), PolicyFakes.WorkAreaOf(primary)));
        });
    }

    [Fact]
    public void The_window_builds_with_every_resource_it_needs_and_is_not_a_taskbar_window()
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig();

            Assert.False(rig.Window.ShowInTaskbar);
            Assert.True(rig.Window.Topmost);
            Assert.Equal(WindowStyle.None, rig.Window.WindowStyle);
            Assert.Equal(ResizeMode.NoResize, rig.Window.ResizeMode);
            Assert.False(rig.Window.IsVisible);
            Assert.Equal(354, rig.Window.Width);
            rig.Snapshot("quick-panel-idle");
        });
    }

    [Fact]
    public void The_four_buttons_share_one_line_and_only_Enviar_has_a_label()
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig();

            var buttons = rig.Find<Button>().ToList();

            Assert.Equal(4, buttons.Count);
            var bounds = buttons.Select(rig.BoundsOf).ToList();
            Assert.All(bounds, b => Assert.InRange(b.Top, bounds[0].Top - 0.5, bounds[0].Top + 0.5));
            Assert.All(bounds, b => Assert.InRange(b.Height, bounds[0].Height - 0.5, bounds[0].Height + 0.5));
            Assert.True(bounds[0].Right <= bounds[1].Left && bounds[1].Right <= bounds[2].Left && bounds[2].Right <= bounds[3].Left, "left to right, no overlap");
            Assert.True(bounds[0].Width > 2 * bounds[1].Width, "Enviar is the wide one");

            var labelled = buttons.Where(b => Descendants(b).OfType<TextBlock>().Any(t => t.Text.Length > 0)).ToList();
            Assert.Single(labelled);
            Assert.Equal("Enviar", Descendants(labelled[0]).OfType<TextBlock>().Single().Text);
            Assert.Equal(["Urgente", "Pausar barra", "Abrir janela"], buttons.Skip(1).Select(AutomationProperties.GetName));
        });
    }

    [Fact]
    public void The_three_status_icons_sit_on_the_line_of_the_state_pill_with_their_tooltips()
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig();
            rig.Hub.SetLedPresent(true);
            rig.Layout();

            var chips = rig.Find<Grid>().Where(g => AutomationProperties.GetName(g) is "Telegram" or "Site" or "LED").ToList();
            var pill = rig.Find<TextBlock>().Single(t => t.Text == "Pronta");

            Assert.Equal(["Telegram", "Site", "LED"], chips.Select(AutomationProperties.GetName));
            var pillMiddle = rig.BoundsOf(pill).Top + (rig.BoundsOf(pill).Height / 2);
            Assert.All(chips, c => Assert.InRange(rig.BoundsOf(c).Top + (rig.BoundsOf(c).Height / 2), pillMiddle - 6, pillMiddle + 6));
            Assert.All(chips, c => Assert.StartsWith(AutomationProperties.GetName(c), (string)c.ToolTip, StringComparison.Ordinal));
            Assert.True(rig.BoundsOf(chips[2]).Right < rig.BoundsOf(pill).Left, "chips on the left, pill on the right");
            rig.Snapshot("quick-panel-ready");
        });
    }

    [Fact]
    public void With_nothing_on_the_bar_the_message_area_has_no_text()
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig();

            var message = rig.Find<TextBlock>().Single(t => t.FontSize == 13.5 && t.FontWeight == FontWeights.Bold);
            var source = rig.Find<TextBlock>().Single(t => t.FontSize == 9.5);

            Assert.Equal(string.Empty, message.Text);
            Assert.False(Shown(source)); // no source chip either
            Assert.True(rig.BoundsOf(message).Height >= 36, "the area keeps its height so the panel does not jump");
        });
    }

    [Fact]
    public void An_alert_on_the_bar_fills_the_message_area_with_its_text_and_source()
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig();
            rig.Hub.SetLedPresent(true);
            rig.Hub.SetShowing(true);
            rig.Gateway.Queue.Enqueue(Item("a", "COROLLA PRATA | PLACA: ABC-1D23 | VIDRO ABERTO", AlertSource.Telegram), DuplicatePolicy.Warn);
            rig.Gateway.Queue.Enqueue(Item("b", "ONIX PRETO | PLACA: BBB-2222"), DuplicatePolicy.Warn);
            rig.Gateway.Queue.StartNext();
            rig.Layout();

            var message = rig.Find<TextBlock>().Single(t => t.FontSize == 13.5 && t.FontWeight == FontWeights.Bold);

            Assert.Equal("COROLLA PRATA | PLACA: ABC-1D23 | VIDRO ABERTO", message.Text);
            Assert.Contains(rig.Find<TextBlock>(), t => t.Text == "TELEGRAM" && Shown(t));
            Assert.Contains(rig.Find<TextBlock>(), t => t.Text == "Exibindo");
            rig.Snapshot("quick-panel-showing");
        });
    }

    [Fact]
    public void The_hint_tooltip_lists_the_keys_and_the_preview_block_shows_the_typed_text_corrected()
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig();
            rig.ViewModel.InputText = "prata corolla bra2e19 vidro abrto";
            rig.Clock.Advance(QuickPanelViewModel.PreviewDelay);
            rig.Layout();

            var info = rig.Find<Border>().Single(b => AutomationProperties.GetName(b) == "Dicas");
            var input = rig.Find<TextBox>().Single();

            Assert.Contains("Enter: envia", (string)info.ToolTip);
            Assert.Contains("Ctrl+Enter: urgente", (string)info.ToolTip);
            Assert.Contains("Esc: limpa", (string)info.ToolTip);
            Assert.Equal("prata corolla bra2e19 vidro abrto", input.Text);
            Assert.Contains(rig.Find<TextBlock>(), t => t.Text == "Vai aparecer assim:" && Shown(t));
            Assert.Contains(rig.Find<TextBlock>(), t => t.Text == "COROLLA PRATA | PLACA: BRA-2E19 | VIDRO ABERTO" && Shown(t));
            rig.Snapshot("quick-panel-preview");
        });
    }

    [Fact]
    public void A_hotkey_problem_and_a_paused_bar_are_visible_in_the_window()
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig();
            var shortcut = new AlertaIBT.App.Platform.QuickPanelHotkey(new TakenRegistrar());
            shortcut.Apply("Ctrl+Alt+Space");
            rig.ViewModel.ApplyHotkey(shortcut.Status);
            rig.Hub.SetLedPresent(true);
            rig.Hub.SetPaused(true);
            rig.ViewModel.InputText = "gol branco abc1234";
            rig.Layout();

            Assert.Contains(rig.Find<TextBlock>(), t => t.Text.Contains("j\U000000E1 \U000000E9 usado por outro programa") && Shown(t));
            Assert.Contains(rig.Find<TextBlock>(), t => t.Text == "Pausada");
            Assert.Contains(rig.Find<Button>(), b => AutomationProperties.GetName(b) == "Retomar barra");
            rig.Snapshot("quick-panel-paused-hotkey-taken");
        });
    }

    // Whether the element and all its parents are Visible (IsVisible is false for everything in a window that was never shown).
    private static bool Shown(FrameworkElement element)
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

    private static QueueItem Item(string id, string text, AlertSource source = AlertSource.Pc) =>
        new(id, text, null, source, false, DateTimeOffset.UnixEpoch, null, null);

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

    private sealed class TakenRegistrar : AlertaIBT.App.Platform.IHotkeyRegistrar
    {
        public bool Register(AlertaIBT.Core.Input.HotkeyGesture gesture) => false;

        public void Unregister()
        {
        }
    }
}
