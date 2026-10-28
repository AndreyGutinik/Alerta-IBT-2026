using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AlertaIBT.App.Tests.ViewModels;
using AlertaIBT.App.ViewModels;
using AlertaIBT.App.Views;
using AlertaIBT.Core.Audio;
using AlertaIBT.Core.Diagnostics;
using AlertaIBT.Core.Settings;

namespace AlertaIBT.App.Tests.Views;

// The real Som page laid out at three window widths (the window is 900 wide at the least). Set ALERTAIBT_RENDER_DIR to also get PNG pictures of
// the whole page (every width) to look at.
public sealed class SomLayoutTests
{
    private static readonly double[] Widths = [900, 1100, 1600];

    private sealed class Rig : IDisposable
    {
        private readonly SomRig _som;

        public Rig(double windowWidth, bool playing = false, bool unplugged = false, bool alertsOff = false)
        {
            var initial = new AppSettings
            {
                Audio = new AudioSettings
                {
                    OutputDeviceId = unplugged ? "{gone}" : null,
                    OutputDeviceName = unplugged ? "Alto-falantes (WING-USB Out 1-2)" : null,
                    OutputMatch = unplugged ? ["Alto-falantes (WING-USB Out 1-2)"] : AudioSettings.DefaultOutputWords, // chosen by hand: no lookalike stands in
                },
                Player = new PlayerSettings
                {
                    Playlists = [new PlaylistEntry("Igreja", @"C:\Users\gutni\Music\Mornye\Igreja"), new PlaylistEntry("Louvor", @"D:\Louvor")],
                    Alarms =
                    [
                        AlarmEntry.CreateDefault("domingo", "Igreja"),
                        AlarmEntry.CreateDefault("quarta", "Louvor") with { Start = "19:30", Latest = "19:55", Days = 0b0001000 },
                    ],
                },
                System = new SystemSettings { AlertsEnabled = !alertsOff },
            };
            _som = new SomRig(initial);
            if (!unplugged)
            {
                _som.Audio.Devices.Add(new AudioDevice("id-2", "Alto-falantes (WING-USB Out 1-2)"));
            }

            _som.Audio.Devices.Add(new AudioDevice("id-3", "Alto-falantes (WING-USB Out 3-4)"));
            _som.PlayerView.ReloadOutputDevices();
            _som.BootAudio();
            if (playing)
            {
                _som.Player.Play();
                var block = new float[96000]; // one second of 48 kHz stereo
                for (var i = 0; i < 75; i++)
                {
                    _som.Backend.Outputs[0].Source.Read(block, 0, block.Length);
                }

                _som.Settings.Clock.Advance(TimeSpan.FromMilliseconds(600));
            }

            var settings = _som.Settings;
            var gateway = new FakeGateway(settings.Clock);
            var composer = new QuickPanelViewModel(gateway, settings.Hub, clock: settings.Clock);
            var home = new HomeViewModel(gateway, composer, clock: settings.Clock);
            var correction = new CorrectionViewModel(settings.Settings, settings.Protector, settings.Hub, settings.Saver);
            var diagnostics = new DiagnosticsViewModel(new LogFiles(Path.Combine(settings.Folder, "logs")), _ => { }, () => null);
            var updates = new FakeUpdateStatus();
            var page = new SettingsViewModel(settings.Settings, settings.Protector, settings.Hub, settings.Saver, diagnostics, () => Task.CompletedTask, updates: updates);
            ViewModel = new MainViewModel(settings.Hub, home, correction, page, settings.Saver, updates: updates, som: _som.Som);
            Window = new MainWindow(ViewModel, null);
            Root = (FrameworkElement)Window.Content;
            ViewModel.SelectTabCommand.Execute(MainTab.Som);
            Layout(windowWidth);
        }

        public SomRig Som => _som;

        public MainViewModel ViewModel { get; }

        public MainWindow Window { get; }

        public FrameworkElement Root { get; }

        public SomPage Page => Window.Som;

        public void Layout(double width)
        {
            var size = new Size(width, 3000);
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
            _som.Dispose();
        }
    }

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
    public void The_four_cards_exist_in_order_with_their_titles_and_the_rows_the_owner_asked_for(double width)
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig(width);
            var page = rig.Page;

            Assert.Equal(
                ["Volumes", "Sa\U000000EDdas", "Player", "Despertadores"],
                new[] { page.VolumesSection, page.OutputsSection, page.PlayerSection, page.AlarmsSection }.Select(s => (string)s.Header));
            Assert.Equal(
                [
                    "Ajustar o som ao iniciar", "Volume geral do Windows", "Holyrics", "Player", "Sons do Windows (notifica\U000000E7\U000000F5es e afins)", "Aplicar agora",
                    "Sa\U000000EDda padr\U000000E3o do Windows", "Travar a sa\U000000EDda padr\U000000E3o neste dispositivo", "Player", "Holyrics", "Definir a sa\U000000EDda do Holyrics",
                    "Player ligado", "Lista de m\U000000FAsicas", "Teclas de m\U000000EDdia controlam o player", "Ao parar", "Ao come\U000000E7ar pelo despertador",
                    "Despertadores ligados",
                ],
                rig.Find<SettingRow>().Select(r => r.Title));

            // Left column: Volumes above Sa\U000000EDdas; the player at its right; the alarms under both.
            Assert.True(rig.BoundsOf(page.OutputsSection).Top > rig.BoundsOf(page.VolumesSection).Bottom);
            Assert.True(rig.BoundsOf(page.PlayerSection).Left > rig.BoundsOf(page.VolumesSection).Right);
            Assert.True(rig.BoundsOf(page.AlarmsSection).Top > rig.BoundsOf(page.PlayerSection).Bottom);
            Assert.True(rig.BoundsOf(page.AlarmsSection).Left > rig.BoundsOf(page.OutputsSection).Right);

            // The Spotify row is not on the screen.
            Assert.DoesNotContain(rig.Find<SettingRow>(), r => r.Title.Contains("Spotify", StringComparison.OrdinalIgnoreCase));
        });
    }

    [Theory]
    [MemberData(nameof(WindowWidths))]
    public void Every_row_is_inside_its_card_and_nothing_leaves_the_page_or_needs_a_horizontal_scroll(double width)
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig(width, playing: true);
            rig.Som.Som.AddAlarmCommand.Execute(null);
            rig.Layout(width);
            var scroll = (ScrollViewer)rig.Page.Content;

            Assert.Equal(0, scroll.ScrollableWidth, 1);
            var pageBounds = rig.BoundsOf(rig.Page);
            foreach (var row in rig.Find<SettingRow>())
            {
                var section = AncestorOf<SettingsSection>(row)!;
                var bounds = rig.BoundsOf(row);
                Assert.True(bounds.Left >= rig.BoundsOf(section).Left + 12 && bounds.Right <= rig.BoundsOf(section).Right - 12, row.Title);
            }

            foreach (var control in rig.Find<Control>().Where(c => c is TextBox or Button or ComboBox or ToggleButton or Slider or RadioButton && c.IsVisible))
            {
                var bounds = rig.BoundsOf(control);
                Assert.True(bounds.Left >= pageBounds.Left - 0.5 && bounds.Right <= pageBounds.Right + 0.5, $"{control.GetType().Name} {AutomationProperties.GetName(control)} {bounds}");
                var section = AncestorOf<SettingsSection>(control);
                Assert.NotNull(section);
                Assert.True(bounds.Right <= rig.BoundsOf(section!).Right - 8, $"{AutomationProperties.GetName(control)} {bounds} leaves its card");
            }

            // The text of a title or a status line is never cut off by the card: each one is as wide as it needs or wraps.
            foreach (var text in rig.Find<TextBlock>().Where(t => t.IsVisible && t.Text.Length > 0 && t.TextWrapping == TextWrapping.Wrap))
            {
                Assert.True(text.ActualHeight > 0, text.Text);
            }
        });
    }

    [Theory]
    [MemberData(nameof(WindowWidths))]
    public void The_cards_of_a_column_are_as_wide_as_each_other_and_the_switches_share_a_right_edge(double width)
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig(width);
            var page = rig.Page;

            Assert.Equal(rig.BoundsOf(page.VolumesSection).Width, rig.BoundsOf(page.OutputsSection).Width, 1);
            Assert.Equal(rig.BoundsOf(page.VolumesSection).Width, rig.BoundsOf(page.PlayerSection).Width, 1);
            Assert.Equal(rig.BoundsOf(page.PlayerSection).Left, rig.BoundsOf(page.AlarmsSection).Left, 1);
            Assert.Equal(rig.BoundsOf(page.PlayerSection).Width, rig.BoundsOf(page.AlarmsSection).Width, 1);

            // Rows touch each other inside a card (one template, one line).
            foreach (var section in new[] { page.VolumesSection, page.OutputsSection })
            {
                var rows = rig.Find<SettingRow>().Where(r => ReferenceEquals(AncestorOf<SettingsSection>(r), section)).ToList();
                for (var i = 1; i < rows.Count; i++)
                {
                    Assert.Equal(rig.BoundsOf(rows[i - 1]).Bottom, rig.BoundsOf(rows[i]).Top, 1);
                }
            }

            Assert.Equal(rig.BoundsOf(page.AdjustSwitch).Right, rig.BoundsOf(page.LockSwitch).Right, 1);
            Assert.Equal(rig.BoundsOf(page.AdjustSwitch).Right, rig.BoundsOf(page.HolyricsRouteSwitch).Right, 1);
            Assert.Equal(rig.BoundsOf(page.PlayerOnSwitch).Right, rig.BoundsOf(page.MediaKeysSwitch).Right, 1);
            Assert.Equal(rig.BoundsOf(page.DefaultOutputCombo).Width, rig.BoundsOf(page.HolyricsOutputCombo).Width, 1);
        });
    }

    [Fact]
    public void The_device_lists_show_the_active_outputs_and_the_remembered_one_marked_desconectado()
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig(1100, unplugged: true);
            var page = rig.Page;

            var choices = ((IEnumerable<DeviceChoice>)page.DefaultOutputCombo.ItemsSource).Select(c => c.Display).ToList();

            Assert.Equal("Autom\U000000E1tico (WING)", choices[0]);
            Assert.Contains("Alto-falantes (WING-USB Out 3-4)", choices);
            Assert.Equal("Alto-falantes (WING-USB Out 1-2) (desconectado)", choices[^1]);
            Assert.Equal("Alto-falantes (WING-USB Out 1-2) (desconectado)", ((DeviceChoice)page.DefaultOutputCombo.SelectedItem).Display);
            Assert.Equal("Autom\U000000E1tico (WING + 3-4)", ((IEnumerable<DeviceChoice>)page.PlayerOutputCombo.ItemsSource).First().Display);
            Assert.Equal("Autom\U000000E1tico (WING + 1-2)", ((IEnumerable<DeviceChoice>)page.HolyricsOutputCombo.ItemsSource).First().Display);
        });
    }

    [Fact]
    public void The_alarms_show_their_time_weekdays_limit_and_playlist_and_the_next_alarm_line_is_there()
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig(1100);
            var page = rig.Page;

            Assert.Equal(2, rig.Som.Som.Alarms.Count);
            Assert.Equal(2, page.AlarmList.Items.Count);
            Assert.StartsWith("Pr\U000000F3ximo despertador:", page.NextAlarmText.Text, StringComparison.Ordinal);
            var chips = rig.Find<ToggleButton>().Where(t => t.Content is string { Length: 1 } letter && "DSTQ".Contains(letter, StringComparison.Ordinal)).ToList();
            Assert.Equal(14, chips.Count);
            Assert.Equal(["D", "S", "T", "Q", "Q", "S", "S"], chips.Take(7).Select(c => (string)c.Content));
            Assert.Equal([true, false, false, false, false, false, false], chips.Take(7).Select(c => c.IsChecked == true));
            Assert.Equal([false, false, false, true, false, false, false], chips.Skip(7).Select(c => c.IsChecked == true));
        });
    }

    [Fact]
    public void Render_the_tab_for_the_owner_to_look_at()
    {
        WpfTestHost.Run(() =>
        {
            foreach (var width in Widths)
            {
                using var idle = new Rig(width);
                idle.Snapshot($"som-{width}");
                using var playing = new Rig(width, playing: true, unplugged: true);
                playing.Snapshot($"som-{width}-tocando");
            }
        });
    }
}
