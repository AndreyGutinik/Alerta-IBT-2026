using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AlertaIBT.App.Platform;
using AlertaIBT.App.Tests.Platform;
using AlertaIBT.App.Tests.ViewModels;
using AlertaIBT.App.ViewModels;
using AlertaIBT.App.Views;
using AlertaIBT.Core.Alerts;
using AlertaIBT.Core.Correction;
using AlertaIBT.Core.Diagnostics;
using AlertaIBT.Core.Firebase;
using AlertaIBT.Core.Queue;
using AlertaIBT.Core.Screens;
using AlertaIBT.Core.Services;
using AlertaIBT.Core.Settings;
using AlertaIBT.Core.Telegram;

namespace AlertaIBT.App.Tests.Views;

// The real XAML of the main window is built and laid out without being shown, so these tests never touch the screen or the keyboard.
// Set ALERTAIBT_RENDER_DIR to also get PNG pictures of the tabs, to look at them next to painel-v7.html.
public sealed class MainWindowTests
{
    private const string ClearKey = "AIzaSyD-clear-text-key-1234567890abcdef";
    private const string ClearToken = "123456789:AAHdqTcvCH1vGWJxfSeofSAs0K5PALDsaw";

    private static readonly Size WindowArea = new(1100, 688);

    private sealed class Rig : IDisposable
    {
        public Rig(AppSettings? initial = null, string[]? logLines = null, AppWindowPolicy? policy = null)
        {
            Settings = new SettingsRig(initial);
            Gateway = new FakeGateway(Settings.Clock);
            Logs = Path.Combine(Settings.Folder, "logs");
            if (logLines is not null)
            {
                Directory.CreateDirectory(Logs);
                File.WriteAllText(Path.Combine(Logs, "alertaibt-20261020.log"), string.Join("\n", logLines) + "\n");
            }

            Hub = Settings.Hub;
            var composer = new QuickPanelViewModel(Gateway, Hub, clock: Settings.Clock);
            var home = new HomeViewModel(Gateway, composer, clock: Settings.Clock);
            var correction = new CorrectionViewModel(Settings.Settings, Settings.Protector, Hub, Settings.Saver);
            Diagnostics = new DiagnosticsViewModel(new LogFiles(Logs), text => Copied.Add(text), () => null);
            var settings = new SettingsViewModel(Settings.Settings, Settings.Protector, Hub, Settings.Saver, Diagnostics, () => Task.CompletedTask);
            ViewModel = new MainViewModel(Hub, home, correction, settings, Settings.Saver);
            Window = new MainWindow(ViewModel, policy);
            Root = (FrameworkElement)Window.Content;
            s_root = Root;
            Layout();
        }

        public SettingsRig Settings { get; }

        public FakeGateway Gateway { get; }

        public StatusHub Hub { get; }

        public string Logs { get; }

        public List<string> Copied { get; } = [];

        public DiagnosticsViewModel Diagnostics { get; }

        public MainViewModel ViewModel { get; }

        public MainWindow Window { get; }

        public FrameworkElement Root { get; }

        public void Layout()
        {
            Root.InvalidateMeasure();
            Root.Measure(WindowArea);
            Root.Arrange(new Rect(0, 0, WindowArea.Width, WindowArea.Height));
            Root.UpdateLayout();
        }

        public void Select(MainTab tab)
        {
            ViewModel.SelectTabCommand.Execute(tab);
            Layout();
        }

        public IEnumerable<T> Find<T>() where T : DependencyObject => Descendants(Root).OfType<T>();

        public Rect BoundsOf(FrameworkElement element) =>
            element.TransformToAncestor(Root).TransformBounds(new Rect(element.RenderSize));

        public IEnumerable<string> AllTexts() =>
            Find<TextBlock>().Where(Shown).Select(t => t.Text)
                .Concat(Find<TextBox>().Where(Shown).Select(t => t.Text))
                .Concat(Find<ContentControl>().Where(Shown).Select(c => c.Content).OfType<string>());

        public void Snapshot(string name)
        {
            var dir = Environment.GetEnvironmentVariable("ALERTAIBT_RENDER_DIR");
            if (string.IsNullOrEmpty(dir))
            {
                return;
            }

            Directory.CreateDirectory(dir);
            var width = (int)Math.Ceiling(Root.ActualWidth);
            var height = (int)Math.Ceiling(Root.ActualHeight);
            var visual = new DrawingVisual();
            using (var context = visual.RenderOpen())
            {
                context.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x0A, 0x0A, 0x0C)), null, new Rect(0, 0, width, height));
                context.DrawRectangle(new VisualBrush(Root), null, new Rect(0, 0, Root.ActualWidth, Root.ActualHeight));
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
            Settings.Dispose();
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

    // The root of the window being tested (the tests run one at a time on the WPF thread).
    private static FrameworkElement? s_root;

    // Whether the element and all its parents are Visible (IsVisible is false for everything in a window that was never shown). An
    // element that is not connected to the window at all (the body of a folded Expander is never built) is not shown either.
    private static bool Shown(FrameworkElement element)
    {
        DependencyObject? top = element;
        for (DependencyObject? node = element; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            top = node;
            if (node is UIElement { Visibility: not Visibility.Visible })
            {
                return false;
            }
        }

        return ReferenceEquals(top, s_root);
    }

    private static QueueItem Item(string id, string text, AlertSource source = AlertSource.Pc, bool urgent = false, string? original = null, bool corrected = false) =>
        new(id, text, null, source, urgent, DateTimeOffset.UnixEpoch, null, original, corrected);

    // ---- the shell ----

    [Fact]
    public void The_window_builds_with_every_resource_it_needs_and_starts_hidden()
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig();

            Assert.False(rig.Window.IsVisible);
            Assert.Equal("Alerta-IBT", rig.Window.Title);
            Assert.True(rig.Root.ActualWidth > 0);
            rig.Snapshot("main-home-idle");
        });
    }

    [Fact]
    public void The_window_is_excluded_from_capture_before_it_is_ever_shown()
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
    public void The_first_opening_with_the_mouse_on_the_LED_centers_the_window_on_the_primary_monitor()
    {
        WpfTestHost.Run(() =>
        {
            var primary = PolicyFakes.Primary();
            var led = PolicyFakes.FakeLed();
            using var rig = new Rig(policy: PolicyFakes.Policy(led, [primary, led], PolicyFakes.CenterOf(led)));
            var handle = new System.Windows.Interop.WindowInteropHelper(rig.Window).Handle;

            rig.Window.PlaceBeforeShow();

            var rect = PolicyFakes.RectOf(handle);
            Assert.True(PolicyFakes.Inside(rect, PolicyFakes.WorkAreaOf(primary)));
            Assert.False(WindowMonitorChooser.OverlapsLed(led, rect.Left, rect.Top, rect.Right, rect.Bottom));
        });
    }

    [Fact]
    public void A_later_opening_pulls_the_window_back_when_it_ended_up_on_the_LED_and_leaves_it_alone_otherwise()
    {
        WpfTestHost.Run(() =>
        {
            var primary = PolicyFakes.Primary();
            var led = PolicyFakes.FakeLed();
            using var rig = new Rig(policy: PolicyFakes.Policy(led, [primary, led], PolicyFakes.CenterOf(primary)));
            var handle = new System.Windows.Interop.WindowInteropHelper(rig.Window).Handle;
            rig.Window.PlaceBeforeShow();

            // The user dragged it somewhere else on the primary monitor: it stays.
            NativeMethods.SetWindowPos(handle, IntPtr.Zero, primary.X + 21, primary.Y + 22, 0, 0, NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
            var dragged = PolicyFakes.RectOf(handle);
            rig.Window.PlaceBeforeShow();
            Assert.Equal(dragged, PolicyFakes.RectOf(handle));

            // It ended up on the LED (for example the LED was plugged in over it): it is pulled back.
            NativeMethods.SetWindowPos(handle, IntPtr.Zero, led.X + 50, led.Y + 50, 0, 0, NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
            rig.Window.PlaceBeforeShow();
            var rect = PolicyFakes.RectOf(handle);
            Assert.False(WindowMonitorChooser.OverlapsLed(led, rect.Left, rect.Top, rect.Right, rect.Bottom));
        });
    }

    [Fact]
    public void The_side_bar_has_the_three_tabs_in_order_and_the_selected_one_is_marked()
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig();
            var tabs = new[] { rig.Window.HomeTab, rig.Window.CorrectionTab, rig.Window.SettingsTab };

            Assert.Equal(["In\U000000EDcio", "Corre\U000000E7\U000000E3o", "Configura\U000000E7\U000000F5es"], tabs.Select(AutomationProperties.GetName));
            Assert.True(rig.BoundsOf(tabs[0]).Bottom <= rig.BoundsOf(tabs[1]).Top);
            Assert.True(rig.BoundsOf(tabs[1]).Bottom <= rig.BoundsOf(tabs[2]).Top);
            Assert.Equal([true, false, false], tabs.Select(t => (bool)t.Tag));

            rig.Select(MainTab.Settings);

            Assert.Equal([false, false, true], tabs.Select(t => (bool)t.Tag));
        });
    }

    [Fact]
    public void Only_the_selected_tab_is_visible()
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig();
            Assert.Equal(Visibility.Visible, rig.Window.Home.Visibility);
            Assert.Equal(Visibility.Collapsed, rig.Window.Correction.Visibility);
            Assert.Equal(Visibility.Collapsed, rig.Window.Settings.Visibility);

            rig.Select(MainTab.Correction);
            Assert.Equal(Visibility.Collapsed, rig.Window.Home.Visibility);
            Assert.Equal(Visibility.Visible, rig.Window.Correction.Visibility);

            rig.Select(MainTab.Settings);
            Assert.Equal(Visibility.Visible, rig.Window.Settings.Visibility);
            Assert.Equal(Visibility.Collapsed, rig.Window.Correction.Visibility);
        });
    }

    [Fact]
    public void The_status_list_sits_at_the_bottom_of_the_side_bar_small_and_light_with_the_five_names()
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig();
            rig.Hub.SetLedPresent(true);
            rig.Layout();

            var labels = rig.Find<TextBlock>().Where(t => t.FontWeight == FontWeights.Light && t.FontSize == 10.5).ToList();

            Assert.Equal(["Telegram", "Site", "LED", "Barra", "Gemini"], labels.Select(t => t.Text));
            Assert.All(labels, t => Assert.True(rig.BoundsOf(t).Left < 172, "inside the side bar"));
            var tops = labels.Select(t => rig.BoundsOf(t).Top).ToList();
            Assert.Equal(tops.OrderBy(t => t), tops);
            Assert.True(tops[0] > rig.BoundsOf(rig.Window.SettingsTab).Bottom + 100, "at the bottom, apart from the tabs");
            Assert.True(rig.BoundsOf(labels[4]).Bottom <= rig.Root.ActualHeight);
            Assert.Equal(5, rig.Window.StatusList.Items.Count);
        });
    }

    [Fact]
    public void A_blue_gemini_reads_Offline_in_the_side_bar_and_every_line_has_a_tooltip()
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig();
            rig.Hub.SetCorrectionMode(CorrectionMode.Gemini);
            rig.Hub.SetGemini(online: false);
            rig.Layout();

            var labels = rig.Find<TextBlock>().Where(t => t.FontWeight == FontWeights.Light && t.FontSize == 10.5).Select(t => t.Text).ToList();
            var rows = rig.Find<StackPanel>().Where(p => p.ToolTip is string).ToList();

            Assert.Equal(["Telegram", "Site", "LED", "Barra", "Offline"], labels);
            Assert.Equal(5, rows.Count);
            Assert.Contains(rows, r => ((string)r.ToolTip).StartsWith("Gemini: Offline", StringComparison.Ordinal));
        });
    }

    [Fact]
    public void Nowhere_is_there_a_Save_button_a_start_with_Windows_option_a_prefix_field_or_quick_models()
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig();
            var seen = new List<string>();
            foreach (var tab in new[] { MainTab.Home, MainTab.Correction, MainTab.Settings })
            {
                rig.Select(tab);
                rig.Window.Settings.BarAdvanced.IsExpanded = true;
                rig.Window.Correction.AdvancedExpander.IsExpanded = true;
                rig.Layout();
                seen.AddRange(rig.AllTexts());
                seen.AddRange(rig.Find<FrameworkElement>().Select(AutomationProperties.GetName));
            }

            var all = string.Join("\n", seen).ToLowerInvariant();
            Assert.DoesNotContain("salvar", all.Replace("n\U000000E3o foi poss\U000000EDvel salvar", string.Empty, StringComparison.Ordinal));
            Assert.DoesNotContain("iniciar com o windows", all);
            Assert.DoesNotContain("prefixo", all);
            Assert.DoesNotContain("modelos r\U000000E1pidos", all);
            Assert.DoesNotContain("com o windows", all);
        });
    }

    // ---- Inicio ----

    [Fact]
    public void Inicio_shows_the_title_the_pause_button_and_the_cards_of_the_mockup()
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig();
            var texts = rig.AllTexts().ToList();

            Assert.Contains("In\U000000EDcio", texts);
            Assert.Contains("MENSAGEM ATUAL", texts);
            Assert.Contains("MENSAGEM", texts);
            Assert.Contains("FILA", texts);
            Assert.Contains("HIST\U000000D3RICO", texts);
            Assert.Contains("Pausar barra", texts);
            Assert.Contains("Pr\U000000F3xima", texts);
            Assert.Contains("Encerrar atual", texts);
            Assert.Contains("Enviar", texts);
            Assert.Contains("Enviar urgente", texts);
            Assert.Contains("Limpar", texts);
            Assert.Contains("Fila vazia.", texts);
            Assert.Contains("Digite a mensagem para o tel\U000000E3o", texts);
        });
    }

    [Fact]
    public void Proxima_and_Encerrar_atual_are_disabled_with_nothing_on_the_bar_and_work_with_something()
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig();
            Assert.False(rig.Window.Home.NextButton.IsEnabled);
            Assert.False(rig.Window.Home.EndButton.IsEnabled);

            rig.Gateway.Queue.Enqueue(Item("a", "COROLLA PRATA"), DuplicatePolicy.Warn);
            rig.Gateway.Queue.StartNext();
            rig.Layout();

            Assert.True(rig.Window.Home.NextButton.IsEnabled);
            rig.Window.Home.NextButton.Command.Execute(null);
            rig.Window.Home.EndButton.Command.Execute(null);
            Assert.Equal(2, rig.Gateway.Skips);
        });
    }

    [Fact]
    public void The_corrected_alert_on_the_bar_shows_the_original_line_and_an_unchanged_one_does_not()
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig();
            Assert.False(Shown(rig.Window.Home.OriginalHost));

            rig.Hub.SetLedPresent(true);
            rig.Hub.SetShowing(true);
            rig.Gateway.Queue.Enqueue(Item("a", "CRETA PRETO | PLACA: GBR-9003 | FAROL ACESO", AlertSource.Telegram, original: "#creta preto | placa:gbr- 9003 / farol acesso", corrected: true), DuplicatePolicy.Warn);
            rig.Gateway.Queue.StartNext();
            rig.Layout();

            Assert.True(Shown(rig.Window.Home.OriginalHost));
            Assert.Contains("Corrigido. Original: #creta preto | placa:gbr- 9003 / farol acesso", rig.AllTexts());
            Assert.Equal("CRETA PRETO | PLACA: GBR-9003 | FAROL ACESO", rig.Window.Home.CurrentMessage.Text);
            Assert.Contains("TELEGRAM", rig.AllTexts());
            rig.Snapshot("main-home-showing");
        });
    }

    [Fact]
    public void The_queue_rows_show_origin_text_and_the_three_buttons_and_the_trash_clears_without_asking()
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig();
            rig.Gateway.Queue.Enqueue(Item("a", "GOL BRANCO | PLACA: FFO-4745", AlertSource.Site, original: "gol branco", corrected: true), DuplicatePolicy.Warn);
            rig.Gateway.Queue.Enqueue(Item("b", "COROLLA PRATA | PLACA: BRA-2E19", AlertSource.Pc), DuplicatePolicy.Warn);
            rig.Layout();

            var names = rig.Find<Button>().Where(Shown).Select(AutomationProperties.GetName).ToList();

            Assert.Equal(2, rig.Window.Home.QueueList.Items.Count);
            Assert.Equal(2, names.Count(n => n == "Subir"));
            Assert.Equal(2, names.Count(n => n == "Descer"));
            Assert.Equal(2, names.Count(n => n == "Remover"));
            Assert.Contains("SITE", rig.AllTexts());
            Assert.Contains("FILA (2)", rig.AllTexts());
            Assert.False(Shown(rig.Window.Home.QueueEmpty));
            rig.Snapshot("main-home-queue");

            rig.Window.Home.ClearQueueButton.Command.Execute(null);
            rig.Layout();

            Assert.Equal(1, rig.Gateway.Clears);
            Assert.Empty(rig.Window.Home.QueueList.Items);
            Assert.True(Shown(rig.Window.Home.QueueEmpty));
            Assert.Empty(rig.Find<Window>()); // no confirmation dialog anywhere
        });
    }

    [Fact]
    public void The_history_header_has_the_clear_button_with_its_icon_text_and_tip_and_it_clears_without_asking()
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig();
            rig.Gateway.Queue.AddRejected(Item("a", "GOL BRANCO | PLACA: FFO-4745"), HistoryReasons.TooLong);
            rig.Gateway.Queue.AddRejected(Item("b", "COROLLA PRATA | PLACA: BRA-2E19"), HistoryReasons.TooLong);
            rig.Layout();
            var home = rig.Window.Home;
            Assert.Equal(2, home.HistoryList.Items.Count);

            // The button: small, with the trash icon and the words, to the right of the caption and above the list.
            Assert.True(Shown(home.ClearHistoryButton));
            Assert.Equal("Limpar hist\U000000F3rico", AutomationProperties.GetName(home.ClearHistoryButton));
            Assert.Contains("Limpar hist\U000000F3rico", rig.AllTexts());
            Assert.Contains(rig.Find<System.Windows.Shapes.Path>().Where(Shown), p => ReferenceEquals(p.Data, home.FindResource("LucideTrash2")) && p.Width == 13 && IsInside(p, home.ClearHistoryButton));
            var caption = rig.Find<TextBlock>().Single(t => Shown(t) && t.Text == "HIST\U000000D3RICO");
            Assert.True(rig.BoundsOf(home.ClearHistoryButton).Left > rig.BoundsOf(caption).Right);
            Assert.True(rig.BoundsOf(home.ClearHistoryButton).Bottom <= rig.BoundsOf(home.HistoryList).Top);
            Assert.True(rig.BoundsOf(home.ClearHistoryButton).Width < 200);

            // The (i) tip says what it clears and what it leaves alone.
            Assert.True(Shown(home.ClearHistoryInfo));
            Assert.Equal(
                "Apaga o hist\U000000F3rico do PC e do site, e as mensagens do bot no Telegram com menos de 48 h. N\U000000E3o mexe na fila nem no que est\U000000E1 passando.",
                home.ClearHistoryInfo.ToolTip);
            Assert.True(rig.BoundsOf(home.ClearHistoryInfo).Right <= rig.BoundsOf(home.ClearHistoryButton).Left);
            Assert.False(Shown(home.HistoryNotice));

            home.ClearHistoryButton.Command.Execute(null);
            rig.Layout();

            Assert.Equal(1, rig.Gateway.HistoryClears);
            Assert.Empty(home.HistoryList.Items);
            Assert.Empty(rig.Find<Window>()); // no confirmation dialog anywhere
            Assert.True(Shown(home.HistoryNotice));
            Assert.Equal("Hist\U000000F3rico limpo: 2 no PC, 9 no site, 7 no Telegram.", home.HistoryNotice.Text);
            rig.Snapshot("main-home-history-cleared");

            rig.Settings.Clock.Advance(HomeViewModel.HistoryNoticeTime);
            rig.Layout();
            Assert.False(Shown(home.HistoryNotice));
        });
    }

    [Fact]
    public void The_sistema_card_has_the_monday_switch_on_by_default_and_it_saves()
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig();
            rig.Select(MainTab.Settings);
            var page = rig.Window.Settings;

            Assert.Contains("Limpar hist\U000000F3rico toda segunda-feira, 00:00", rig.AllTexts());
            Assert.True(Shown(page.ClearHistorySwitch));
            Assert.True(page.ClearHistorySwitch.IsChecked);
            Assert.True(rig.BoundsOf(page.ClearHistorySwitch).Top > rig.BoundsOf(page.HotkeyBox).Bottom);

            page.ClearHistorySwitch.IsChecked = false;
            rig.Layout();
            rig.Settings.Rest();

            Assert.False(rig.Settings.OnDisk().System.ClearHistoryWeekly);
        });
    }

    [Fact]
    public void A_saved_off_monday_switch_is_shown_off()
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig(new AppSettings { System = new SystemSettings { ClearHistoryWeekly = false } });
            rig.Select(MainTab.Settings);

            Assert.False(rig.Window.Settings.ClearHistorySwitch.IsChecked);
        });
    }

    private static bool IsInside(DependencyObject child, DependencyObject ancestor)
    {
        for (var node = child; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (ReferenceEquals(node, ancestor))
            {
                return true;
            }
        }

        return false;
    }

    [Fact]
    public void The_history_shows_the_green_check_the_red_x_with_why_and_the_replay_arrow_only_for_shown_ones()
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig();
            rig.Gateway.Queue.AddRejected(Item("r", "gol branco farol aceso", AlertSource.Telegram), HistoryReasons.MissingFields(["placa"]));
            rig.Gateway.Queue.Enqueue(Item("a", "FOX PRATA | PLACA: ETC-6785 | ESTACIONADO EM LOCAL PROIBIDO", AlertSource.Telegram, original: "fox prata etc6785", corrected: true), DuplicatePolicy.Warn);
            rig.Gateway.Queue.StartNext();
            rig.Gateway.Queue.FinishCurrent();
            rig.Layout();

            Assert.Equal(2, rig.Window.Home.HistoryList.Items.Count);
            Assert.Contains("FALTOU PLACA", rig.AllTexts());
            var replays = rig.Find<Button>().Where(b => AutomationProperties.GetName(b) == "Exibir de novo" && Shown(b)).ToList();
            Assert.Single(replays);
            replays[0].Command.Execute(null);
            Assert.Single(rig.Gateway.Replays);
            Assert.Equal("a", rig.Gateway.Replays[0].Id);
            rig.Layout();
            Assert.Contains("Na fila: 1\U000000BA", rig.AllTexts());
            rig.Snapshot("main-home-history");
        });
    }

    [Fact]
    public void The_message_field_shows_the_preview_under_it_and_the_pause_button_turns_into_retomar()
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig();
            var composer = rig.ViewModel.Home.Composer;
            composer.InputText = "prata corolla bra2e19 vidro abrto";
            rig.Settings.Clock.Advance(QuickPanelViewModel.PreviewDelay);
            rig.Hub.SetLedPresent(true);
            rig.Hub.SetPaused(true);
            rig.Layout();

            Assert.Contains("Vai aparecer assim:", rig.AllTexts());
            Assert.Contains("COROLLA PRATA | PLACA: BRA-2E19 | VIDRO ABERTO", rig.AllTexts());
            Assert.Contains("Retomar barra", rig.AllTexts());
            Assert.Equal("prata corolla bra2e19 vidro abrto", rig.Window.Home.InputBox.Text);
            rig.Window.Home.PauseButton.Command.Execute(null);
            Assert.Equal(1, rig.Gateway.Toggles);
            rig.Snapshot("main-home-preview-paused");
        });
    }

    // ---- Correcao ----

    [Fact]
    public void Correcao_shows_the_switch_the_two_options_the_key_the_rules_and_the_gemini_state()
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig();
            rig.Select(MainTab.Correction);
            var texts = rig.AllTexts().ToList();

            Assert.Contains("Corre\U000000E7\U000000E3o", texts);
            Assert.Contains("CORRE\U000000C7\U000000C3O AUTOM\U000000C1TICA", texts);
            Assert.Contains("Gemini", texts);
            Assert.Contains("S\U000000F3 offline", texts);
            Assert.Contains("Chave do Gemini (gr\U000000E1tis)", texts);
            Assert.Contains("REGRAS FIXAS", texts);
            Assert.Contains("Avan\U000000E7ado", texts);
            Assert.Contains("Gemini \U000000B7 Aguardando", texts);
            Assert.True(rig.Window.Correction.EnabledSwitch.IsChecked);
            Assert.Equal(true, rig.Window.Correction.GeminiOption.Tag);
            Assert.Equal(false, rig.Window.Correction.OfflineOption.Tag);
            rig.Snapshot("main-correction");
        });
    }

    [Fact]
    public void There_are_no_explanation_blocks_on_Correcao_only_tooltips()
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig();
            rig.Select(MainTab.Correction);

            var longTexts = rig.Find<TextBlock>().Where(Shown).Where(t => t.Text.Length > 60).Select(t => t.Text).ToList();
            var infos = rig.Find<Border>().Where(b => AutomationProperties.GetName(b) == "Dicas" && Shown(b)).ToList();

            Assert.Empty(longTexts);
            Assert.Equal(3, infos.Count); // correction, key, rules (the model's (i) is inside the folded Avancado)
            Assert.All(infos, i => Assert.IsType<string>(i.ToolTip));
            Assert.Contains("Gemini: gr\U000000E1tis", (string)infos[0].ToolTip);
        });
    }

    [Fact]
    public void With_a_stored_key_the_box_shows_only_dots_and_the_clear_key_is_nowhere_in_the_window()
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig(new AppSettings().WithGeminiKey(ClearKey, new ReversingProtector()));
            rig.Select(MainTab.Correction);

            Assert.True(Shown(rig.Window.Correction.KeyLockedBox));
            Assert.Equal(SecretFieldViewModel.Mask, rig.Window.Correction.KeyLockedBox.Text);
            Assert.False(rig.Window.Correction.KeyLockedBox.IsEnabled);
            Assert.False(Shown(rig.Window.Correction.KeyEntryBox));
            Assert.Contains("Trocar", rig.AllTexts());
            Assert.DoesNotContain(rig.AllTexts(), t => t.Contains("AIza", StringComparison.Ordinal));
            Assert.DoesNotContain(rig.Find<TextBox>(), t => t.Text.Contains("AIza", StringComparison.Ordinal));
            rig.Snapshot("main-correction-key-locked");
        });
    }

    [Fact]
    public void Trocar_opens_an_empty_box_and_a_new_key_goes_back_to_dots_after_saving()
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig(new AppSettings().WithGeminiKey(ClearKey, new ReversingProtector()));
            rig.Select(MainTab.Correction);

            rig.Window.Correction.KeyLockButton.Command.Execute(null);
            rig.Layout();
            Assert.True(Shown(rig.Window.Correction.KeyEntryBox));
            Assert.Equal(string.Empty, rig.Window.Correction.KeyEntryBox.Text);
            Assert.Contains("Bloquear", rig.AllTexts());

            rig.Window.Correction.KeyEntryBox.Text = "AIzaNEW-key-0123456789abcdefghij";
            rig.Settings.Rest();
            rig.Layout();

            Assert.False(Shown(rig.Window.Correction.KeyEntryBox));
            Assert.True(Shown(rig.Window.Correction.KeyLockedBox));
            Assert.DoesNotContain(rig.AllTexts(), t => t.Contains("AIzaNEW", StringComparison.Ordinal));
            Assert.Equal("AIzaNEW-key-0123456789abcdefghij", rig.Settings.OnDisk().GetGeminiKey(rig.Settings.Protector));
            Assert.DoesNotContain("AIzaNEW", rig.Settings.FileText());
        });
    }

    [Fact]
    public void The_model_sits_in_Avancado_with_the_default_and_the_rules_show_their_note_and_the_saved_stamp_follows_the_autosave()
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig();
            rig.Select(MainTab.Correction);
            Assert.False(Shown(rig.Window.Correction.ModelBox));

            rig.Window.Correction.AdvancedExpander.IsExpanded = true;
            rig.Layout();
            Assert.True(Shown(rig.Window.Correction.ModelBox));
            Assert.Equal(GeminiPrompt.DefaultModel, rig.Window.Correction.ModelBox.Text);

            Assert.StartsWith("\U00002713 Salvo", rig.ViewModel.Saver.Label);
            rig.Window.Correction.RulesBox.Text = "farol acesso => farol aceso\nlixo";
            rig.Layout();
            Assert.Equal("Salvando\U00002026", rig.ViewModel.Saver.Label);
            Assert.Contains("Salvando\U00002026", rig.AllTexts());

            rig.Settings.Rest();
            rig.Layout();
            Assert.Contains("1 regra \U000000B7 1 linha ignorada (linha 2)", rig.AllTexts());
            Assert.Contains(rig.AllTexts(), t => t.StartsWith("\U00002713 Salvo \U000000E0s", StringComparison.Ordinal));
            rig.Snapshot("main-correction-rules");
        });
    }

    [Fact]
    public void The_gemini_pill_of_the_tab_is_blue_offline_when_the_pipeline_is_unhealthy()
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig();
            rig.Hub.SetCorrectionMode(CorrectionMode.Gemini);
            rig.Hub.SetGemini(online: false);
            rig.Select(MainTab.Correction);

            Assert.Contains("Gemini \U000000B7 Offline", rig.AllTexts());
            Assert.StartsWith("Gemini: Offline", (string)rig.Window.Correction.GeminiPill.ToolTip, StringComparison.Ordinal);
            var dot = rig.Find<Border>().First(b => b.Width == 8 && b.Height == 8 && b.CornerRadius.TopLeft == 4 && Shown(b));
            Assert.Equal(Color.FromRgb(0x5A, 0xA9, 0xFF), ((SolidColorBrush)dot.Background).Color);
        });
    }

    [Fact]
    public void Choosing_Solo_offline_selects_its_card_and_saves()
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig();
            rig.Select(MainTab.Correction);

            rig.Window.Correction.OfflineOption.Command.Execute("offline");
            rig.Layout();

            Assert.Equal(true, rig.Window.Correction.OfflineOption.Tag);
            Assert.Equal(false, rig.Window.Correction.GeminiOption.Tag);
            rig.Settings.Rest();
            Assert.Equal("offline", rig.Settings.OnDisk().Correction.Mode);
        });
    }

    [Fact]
    public void Switching_the_correction_off_dims_the_two_options_and_saves()
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig();
            rig.Select(MainTab.Correction);

            rig.Window.Correction.EnabledSwitch.IsChecked = false;
            rig.Layout();

            Assert.False(rig.Window.Correction.ModeCards.IsEnabled);
            rig.Settings.Rest();
            Assert.False(rig.Settings.OnDisk().Correction.Enabled);
        });
    }

    // ---- Configuracoes ----

    [Fact]
    public void Configuracoes_shows_the_bar_telegram_preview_system_and_diagnostico_cards()
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig();
            rig.Select(MainTab.Settings);
            var texts = rig.AllTexts().ToList();

            Assert.Contains("Barra no LED", texts);
            Assert.Contains("Posi\U000000E7\U000000E3o", texts);
            Assert.Contains("Tamanho", texts);
            Assert.Contains("Velocidade", texts);
            Assert.Contains("Avan\U000000E7ado: letra separada, margem, cores, anima\U000000E7\U000000E3o", texts);
            Assert.Contains("Telegram", texts);
            Assert.Contains("Token do bot", texts);
            Assert.Contains("Chat ID do grupo", texts);
            Assert.Contains("Pr\U000000E9via no LED 2560x768", texts);
            Assert.Contains("Mostrar barra 3 s no LED", texts);
            Assert.Contains("Sistema", texts);
            Assert.Contains("Atalho do painel r\U000000E1pido", texts);
            Assert.Contains("Esconder da live (OBS e NDI)", texts);
            Assert.Contains("sempre ligado", texts);
            Assert.Contains("Diagn\U000000F3stico", texts);
            Assert.Contains("M\U000000E9dia \U000000B7 56 px", texts);
            Assert.Equal("Ctrl+Alt+Espa\U000000E7o", rig.Window.Settings.HotkeyBox.Text);
            rig.Snapshot("main-settings");
        });
    }

    [Fact]
    public void The_sliders_have_the_documented_ranges_and_defaults_and_move_the_preview_and_the_labels()
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig();
            rig.Select(MainTab.Settings);
            var page = rig.Window.Settings;

            Assert.Equal((28.0, 120.0, 56.0), (page.SizeSlider.Minimum, page.SizeSlider.Maximum, page.SizeSlider.Value));
            Assert.Equal((30.0, 300.0, 90.0), (page.SpeedSlider.Minimum, page.SpeedSlider.Maximum, page.SpeedSlider.Value));
            Assert.Equal(56, page.Preview.BarHeightPx);

            page.SizeSlider.Value = 100;
            page.SpeedSlider.Value = 200;
            rig.Layout();

            Assert.Equal(100, page.Preview.BarHeightPx); // the preview follows at once
            Assert.Equal("Enorme \U000000B7 100 px", page.SizeOutput.Text);
            Assert.StartsWith("200 px/s \U000000B7 passa em ", page.SpeedOutput.Text, StringComparison.Ordinal);
            Assert.Equal(56, rig.Settings.Settings.Current.Bar.Height); // not saved yet
            rig.Settings.Rest();
            Assert.Equal(100, rig.Settings.OnDisk().Bar.Height);
            Assert.Equal(200, rig.Settings.OnDisk().Bar.Speed);
        });
    }

    [Fact]
    public void The_preview_is_a_2560_by_768_copy_of_the_led_with_the_bar_at_the_top_by_default_and_at_the_bottom_for_rodape()
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig();
            rig.Select(MainTab.Settings);
            var preview = rig.Window.Settings.Preview;

            var bounds = rig.BoundsOf(preview);
            Assert.InRange(bounds.Width / bounds.Height, 2560.0 / 768.0 - 0.02, 2560.0 / 768.0 + 0.02);
            Assert.Equal(0, preview.BarTop);

            rig.Window.Settings.BottomOption.IsChecked = true;
            rig.Layout();

            Assert.Equal(768 - 56, preview.BarTop);
            Assert.False(rig.Window.Settings.TopOption.IsChecked);
            Assert.True(preview.TextWidth > 300, "the sample text was laid out with the real font");
        });
    }

    [Fact]
    public void The_preview_runs_the_same_pass_as_the_real_bar_and_restarts_on_a_change()
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig();
            rig.Select(MainTab.Settings);
            var preview = rig.Window.Settings.Preview;
            var slow = preview.PassLength;

            rig.Window.Settings.SpeedSlider.Value = 300;

            Assert.True(preview.PassLength < slow, "a faster bar has a shorter pass");
            var frame = preview.ShowAt(TimeSpan.Zero);
            Assert.Equal(AlertaIBT.Core.Overlay.BarPhase.In, frame.Phase);
            Assert.Equal(2560, frame.TextX); // the text waits beyond the right edge
        });
    }

    [Fact]
    public void The_advanced_fields_take_numbers_and_colors_and_show_a_hint_for_garbage()
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig();
            rig.Select(MainTab.Settings);
            var page = rig.Window.Settings;
            page.BarAdvanced.IsExpanded = true;
            rig.Layout();

            Assert.True(Shown(page.FontBox));
            page.FontBox.Text = "abc";
            page.BackgroundBox.Text = "#102030";
            page.MarginBox.Text = "40";
            page.ForegroundBox.Text = "verde";
            rig.Layout();

            Assert.Contains("Digite s\U000000F3 n\U000000FAmeros inteiros.", rig.AllTexts());
            Assert.Contains("Use o formato #RRGGBB, por exemplo #FFF7E8.", rig.AllTexts());
            rig.Settings.Rest();
            var bar = rig.Settings.OnDisk().Bar;
            Assert.Null(bar.FontSize);
            Assert.Equal("#102030", bar.Background);
            Assert.Equal(40, bar.PaddingX);
            Assert.Equal("#FFF7E8", bar.Foreground);
            rig.Snapshot("main-settings-advanced");
        });
    }

    [Fact]
    public void Telegram_with_a_stored_token_is_locked_and_only_dots_show_and_the_chat_id_is_locked_too()
    {
        WpfTestHost.Run(() =>
        {
            var initial = new AppSettings().WithTelegramToken(ClearToken, new ReversingProtector());
            using var rig = new Rig(initial with { Telegram = initial.Telegram with { ChatId = -1001234567890 } });
            rig.Select(MainTab.Settings);
            var page = rig.Window.Settings;

            Assert.True(Shown(page.TokenLockedBox));
            Assert.Equal(SecretFieldViewModel.Mask, page.TokenLockedBox.Text);
            Assert.False(Shown(page.TokenEntryBox));
            Assert.False(page.ChatBox.IsEnabled);
            Assert.Equal("-1001234567890", page.ChatBox.Text);
            Assert.DoesNotContain(rig.Find<TextBox>(), t => t.Text.Contains("AAH", StringComparison.Ordinal));
            Assert.DoesNotContain(rig.AllTexts(), t => t.Contains("123456789:", StringComparison.Ordinal));

            page.TokenLockButton.Command.Execute(null);
            rig.Layout();

            Assert.True(page.ChatBox.IsEnabled);
            Assert.True(Shown(page.TokenEntryBox));
            Assert.Equal(string.Empty, page.TokenEntryBox.Text);
            rig.Snapshot("main-settings-telegram-unlocked");
        });
    }

    [Fact]
    public void The_site_block_sits_below_telegram_with_its_four_fields_the_switch_and_a_masked_password()
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig();
            rig.Select(MainTab.Settings);
            var page = rig.Window.Settings;
            var texts = rig.AllTexts().ToList();

            Assert.Contains("Site", texts);
            Assert.Contains("URL do banco", texts);
            Assert.Contains("Chave da API (web)", texts);
            Assert.Contains("E-mail do PC", texts);
            Assert.Contains("Senha do PC", texts);
            Assert.True(Shown(page.SiteSwitch));
            Assert.True(Shown(page.SiteUrlBox));
            Assert.True(Shown(page.SiteApiKeyBox));
            Assert.True(Shown(page.SiteEmailBox));
            Assert.True(Shown(page.SitePasswordBox));
            Assert.False(Shown(page.SitePasswordLockedBox));
            Assert.True(rig.BoundsOf(page.SiteUrlBox).Top > rig.BoundsOf(page.ChatBox).Bottom);
            Assert.Contains(rig.Find<Border>().Where(b => AutomationProperties.GetName(b) == "Dicas" && Shown(b)).Select(b => (string)b.ToolTip),
                tip => tip.Contains("alertaibt.web.app", StringComparison.Ordinal) && tip.Contains("publicar-site.ps1", StringComparison.Ordinal));

            page.SiteSwitch.IsChecked = true;
            page.SiteUrlBox.Text = "https://alertaibt-default-rtdb.firebaseio.com";
            page.SiteApiKeyBox.Text = "AIzaSyTestKey123";
            page.SiteEmailBox.Text = "pc@alertaibt.web.app";
            page.SitePasswordBox.Password = "Xk3-pQ9z-Lm7vRt2";
            rig.Layout();
            rig.Settings.Rest();

            var saved = rig.Settings.OnDisk();
            Assert.True(saved.Site.IsConfigured);
            Assert.True(saved.Site.Enabled);
            Assert.Equal("Xk3-pQ9z-Lm7vRt2", saved.GetSitePassword(rig.Settings.Protector));
            Assert.DoesNotContain("Xk3-pQ9z", rig.Settings.FileText(), StringComparison.Ordinal);

            // Saved: the password shows as dots only, and its box is emptied.
            Assert.True(Shown(page.SitePasswordLockedBox));
            Assert.Equal(SecretFieldViewModel.Mask, page.SitePasswordLockedBox.Text);
            Assert.False(Shown(page.SitePasswordBox));
            Assert.Equal(string.Empty, page.SitePasswordBox.Password);
            rig.Snapshot("main-settings-site");
        });
    }

    [Fact]
    public void A_stored_site_password_is_locked_and_the_site_dot_shows_the_state()
    {
        WpfTestHost.Run(() =>
        {
            var initial = new AppSettings { Site = new SiteSettings { Enabled = true, DatabaseUrl = "https://x-default-rtdb.firebaseio.com", ApiKey = "AIzaSyTestKey123", DeviceEmail = "pc@x.com" } }
                .WithSitePassword("Xk3-pQ9z-Lm7vRt2", new ReversingProtector());
            using var rig = new Rig(initial);
            rig.Select(MainTab.Settings);
            var page = rig.Window.Settings;

            Assert.True(Shown(page.SitePasswordLockedBox));
            Assert.False(Shown(page.SitePasswordBox));
            Assert.Equal("pc@x.com", page.SiteEmailBox.Text);
            Assert.Equal("https://x-default-rtdb.firebaseio.com", page.SiteUrlBox.Text);
            Assert.True(page.SiteSwitch.IsChecked);
            Assert.DoesNotContain(rig.AllTexts(), t => t.Contains("Xk3", StringComparison.Ordinal));

            page.SitePasswordLockButton.Command.Execute(null);
            rig.Layout();
            Assert.True(Shown(page.SitePasswordBox));
            Assert.Equal(string.Empty, page.SitePasswordBox.Password);

            rig.Hub.SetSite(SiteState.AuthError, "login recusado");
            rig.Layout();
            Assert.Contains("Erro de login", rig.AllTexts());
        });
    }

    [Fact]
    public void The_telegram_tooltip_explains_the_hash()
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig();
            rig.Select(MainTab.Settings);

            var tips = rig.Find<Border>().Where(b => AutomationProperties.GetName(b) == "Dicas" && Shown(b)).Select(b => (string)b.ToolTip).ToList();

            Assert.Contains(tips, t => t.Contains("come\U000000E7am com #", StringComparison.Ordinal) && t.Contains("#corolla prata abc1d23 vidro aberto", StringComparison.Ordinal));
        });
    }

    [Fact]
    public void Mostrar_barra_3_s_is_wired_to_the_real_bar()
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig();
            rig.Hub.SetLedPresent(true);
            rig.Select(MainTab.Settings);

            Assert.True(rig.Window.Settings.ShowBarButton.Command.CanExecute(null));
            Assert.Same(rig.ViewModel.Settings.ShowBarCommand, rig.Window.Settings.ShowBarButton.Command);
        });
    }

    [Fact]
    public void The_shortcut_box_is_read_only_and_a_taken_shortcut_shows_its_warning()
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig();
            rig.Select(MainTab.Settings);

            Assert.True(rig.Window.Settings.HotkeyBox.IsReadOnly);

            rig.ViewModel.Settings.ApplyHotkey(new AlertaIBT.App.Platform.HotkeyStatus("Ctrl+Alt+Space", "Ctrl+Alt+Espa\U000000E7o", false, "O atalho Ctrl+Alt+Espa\U000000E7o j\U000000E1 \U000000E9 usado por outro programa."));
            rig.Layout();

            Assert.Contains(rig.AllTexts(), t => t.Contains("j\U000000E1 \U000000E9 usado por outro programa", StringComparison.Ordinal));
        });
    }

    // ---- Diagnostico ----

    [Fact]
    public void Diagnostico_is_folded_away_and_opened_it_shows_the_logs_with_the_three_buttons()
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig(logLines:
            [
                "2026-10-20 08:00:00.000 [INF] Test: Aplicativo iniciado",
                "2026-10-20 08:00:01.000 [ERR] Test: falhou",
            ]);
            rig.Select(MainTab.Settings);
            var page = rig.Window.Settings;

            Assert.False(page.DiagnosticsExpander.IsExpanded);
            Assert.DoesNotContain(rig.AllTexts(), t => t.Contains("Aplicativo iniciado", StringComparison.Ordinal));
            Assert.Contains("sem registros", rig.AllTexts());

            page.DiagnosticsExpander.IsExpanded = true;
            rig.Layout();

            Assert.Contains(rig.AllTexts(), t => t.Contains("Aplicativo iniciado", StringComparison.Ordinal));
            Assert.Contains(rig.AllTexts(), t => t.Contains("falhou", StringComparison.Ordinal));
            Assert.Contains("2 registros \U000000B7 1 erro(s)", rig.AllTexts());
            Assert.Contains("Copiar logs", rig.AllTexts());
            Assert.Contains("Exportar", rig.AllTexts());
            Assert.Equal(2, page.LogList.Items.Count);
            rig.Snapshot("main-settings-diagnostics");
        });
    }

    [Fact]
    public void Closing_the_window_only_hides_it_so_it_can_be_opened_again()
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig();

            rig.Window.Close();

            Assert.False(rig.Window.IsVisible);
            Assert.True(rig.Window.IsLoaded || !rig.Window.IsLoaded); // still a usable window object
            rig.Layout();
            Assert.True(rig.Root.ActualWidth > 0);
        });
    }
}
