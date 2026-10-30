using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Shape = System.Windows.Shapes.Path;
using AlertaIBT.App.Services;
using AlertaIBT.App.Tests.ViewModels;
using AlertaIBT.App.Theme;
using AlertaIBT.App.ViewModels;
using AlertaIBT.App.Views;
using AlertaIBT.Core.Audio;
using AlertaIBT.Core.Diagnostics;
using AlertaIBT.Core.Player;
using AlertaIBT.Core.Services;
using AlertaIBT.Core.Settings;

namespace AlertaIBT.App.Tests.Views;

// The real Som page laid out at three window widths (the window is 900 wide at the least). Set ALERTAIBT_RENDER_DIR to also get PNG pictures of
// the whole page (every width and every state) to look at.
public sealed class SomLayoutTests
{
    private static readonly double[] Widths = [900, 1100, 1600];

    private enum Mode
    {
        Idle,
        Playing,
        Paused,
        Off,
    }

    // A cover that looks like one: a deep blue and gold gradient with a cross.
    private sealed class FakeCover(bool hasArt) : ICoverArtLoader
    {
        public Task<ImageSource?> LoadAsync(string trackPath)
        {
            if (!hasArt)
            {
                return Task.FromResult<ImageSource?>(null);
            }

            var visual = new DrawingVisual();
            using (var context = visual.RenderOpen())
            {
                var brush = new LinearGradientBrush(Color.FromRgb(0x1B, 0x2A, 0x5C), Color.FromRgb(0xE0, 0x9F, 0x3E), 45);
                context.DrawRectangle(brush, null, new Rect(0, 0, 336, 336));
                var cream = new SolidColorBrush(Color.FromRgb(0xFF, 0xF3, 0xDC));
                context.DrawRectangle(cream, null, new Rect(156, 70, 24, 196));
                context.DrawRectangle(cream, null, new Rect(108, 116, 120, 24));
            }

            var bitmap = new RenderTargetBitmap(336, 336, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            bitmap.Freeze();
            return Task.FromResult<ImageSource?>(bitmap);
        }
    }

    private sealed class NameMetadata : ITrackMetadataReader
    {
        public TrackInfo Read(string path)
        {
            var (title, artist) = FileTrackMetadataReader.FromFileName(path);
            return new TrackInfo(path, System.IO.Path.GetFileName(path), title, artist);
        }
    }

    private sealed class Rig : IDisposable
    {
        private readonly SomRig _som;

        public Rig(
            double windowWidth,
            Mode mode = Mode.Idle,
            bool unplugged = false,
            bool alertsOff = false,
            bool art = true,
            int alarms = 2,
            bool longNames = false,
            bool alarmsOff = false,
            double crossfade = 0,
            CrossfadeCurve curve = CrossfadeCurve.Linear,
            int scanning = 0,
            bool tagsOff = false)
        {
            var playlists = longNames
                ? new[]
                {
                    new PlaylistEntry("Louvores de domingo de manh\U000000E3 do culto da fam\U000000EDlia e das crian\U000000E7as (vers\U000000F5es longas)", "C:\\Users\\gutni\\Music\\Mornye\\Igreja\\Cultos de domingo\\Manh\U000000E3\\Louvores e adora\U000000E7\U000000E3o\\Vers\U000000F5es"),
                    new PlaylistEntry("Louvor", @"D:\Louvor"),
                }
                : [new PlaylistEntry("Igreja", @"C:\Users\gutni\Music\Mornye\Igreja"), new PlaylistEntry("Louvor", @"D:\Louvor")];
            var alarmList = new List<AlarmEntry>();
            if (alarms >= 1)
            {
                alarmList.Add(AlarmEntry.CreateDefault("domingo", playlists[0].Name));
            }

            if (alarms >= 2)
            {
                alarmList.Add(AlarmEntry.CreateDefault("quarta", "Louvor") with { Start = "19:30", Latest = "19:55", Days = 0b0001000 });
            }

            for (var i = 3; i <= alarms; i++)
            {
                alarmList.Add(AlarmEntry.CreateDefault("extra" + i, "Louvor") with { Start = $"{6 + i}:00", Latest = $"{6 + i}:25", Days = 0b0100010 });
            }

            var deviceName = longNames ? "Alto-falantes (WING-USB Out 1-2 com um nome muito longo para testar o corte)" : "Alto-falantes (WING-USB Out 1-2)";
            var initial = new AppSettings
            {
                Audio = new AudioSettings
                {
                    OutputDeviceId = unplugged ? "{gone}" : null,
                    OutputDeviceName = unplugged ? deviceName : null,
                    OutputMatch = unplugged ? [deviceName] : AudioSettings.DefaultOutputWords, // chosen by hand: no lookalike stands in
                },
                Player = new PlayerSettings
                {
                    Playlists = playlists,
                    Alarms = alarmList,
                    Enabled = mode != Mode.Off,
                    AlarmsEnabled = !alarmsOff,
                    FadeOutSeconds = mode == Mode.Paused ? 0 : 5,
                    CrossfadeSeconds = crossfade,
                    CrossfadeCurve = curve,
                    WriteLoudnessTags = !tagsOff,
                },
                System = new SystemSettings { AlertsEnabled = !alertsOff },
            };
            _som = new SomRig(initial, new FakeCover(art), new NameMetadata(), withLoudness: true);
            if (scanning > 0)
            {
                // The chip shows while files wait to be measured: this many that do not exist (so none has a measurement) and no thread runs.
                _som.Loudness!.Scanner.Configure(Enumerable.Range(1, scanning).Select(i => $"C:/no-such-folder/track {i}.flac").ToList());
            }
            if (!unplugged)
            {
                _som.Audio.Devices.Add(new AudioDevice("id-2", deviceName));
            }

            _som.Audio.Devices.Add(new AudioDevice("id-3", "Alto-falantes (WING-USB Out 3-4)"));
            _som.PlayerView.ReloadOutputDevices();
            _som.BootAudio();
            if (mode is Mode.Playing or Mode.Paused)
            {
                _som.Player.Play();
                Listen(75);
                if (mode == Mode.Paused)
                {
                    _som.Player.Pause();
                    Listen(0.2);
                }

                _som.Settings.Clock.Advance(TimeSpan.FromMilliseconds(600));
            }

            var settings = _som.Settings;
            var gateway = new FakeGateway(settings.Clock);
            var composer = new QuickPanelViewModel(gateway, settings.Hub, clock: settings.Clock);
            var home = new HomeViewModel(gateway, composer, clock: settings.Clock);
            var correction = new CorrectionViewModel(settings.Settings, settings.Protector, settings.Hub, settings.Saver);
            var diagnostics = new DiagnosticsViewModel(new LogFiles(System.IO.Path.Combine(settings.Folder, "logs")), _ => { }, () => null);
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

        public PlayerViewModel Player => _som.PlayerView;

        // The sound card pulls `seconds` of sound.
        public void Listen(double seconds)
        {
            var block = new float[9600]; // 100 ms of 48 kHz stereo
            for (var i = 0; i < (int)(seconds * 10); i++)
            {
                _som.Backend.Outputs[0].Source.Read(block, 0, block.Length);
            }
        }

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
            using var stream = File.Create(System.IO.Path.Combine(dir, name + ".png"));
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

    // The window is 900 wide at the least; from about 1000 on the bottom row is two cards side by side, under it one column.
    private static bool TwoColumns(double width) => width >= 1000;

    // The window of the tests is never shown, so IsVisible is always false: this follows the Visibility of the element and its parents instead.
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

    // A size of the design (32 px controls, 52 px boxes) may come out 1 px larger when its top or left falls between two device pixels (layout rounding).
    private static void Near(double expected, double actual) => Assert.InRange(actual, expected - 0.6, expected + 1.1);

    private static double CenterX(Rect rect) => rect.Left + (rect.Width / 2);

    private static double CenterY(Rect rect) => rect.Top + (rect.Height / 2);

    private static bool IsInside(DependencyObject element, DependencyObject ancestor)
    {
        for (var node = VisualTreeHelper.GetParent(element); node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (ReferenceEquals(node, ancestor))
            {
                return true;
            }
        }

        return false;
    }

    private static double Luminance(Brush brush)
    {
        var c = ((SolidColorBrush)brush).Color;
        return (0.2126 * c.R) + (0.7152 * c.G) + (0.0722 * c.B);
    }

    private static Brush Resource(string key) => (Brush)Application.Current.FindResource(key);

    private static List<FrameworkElement> AlarmBlocks(Rig rig) =>
        rig.Page.AlarmList.Items.Cast<object>()
            .Where(item => item is AlarmRow)
            .Select(item => (FrameworkElement)Descendants(rig.Page.AlarmList.ItemContainerGenerator.ContainerFromItem(item)).OfType<Border>().First())
            .ToList();

    // ---- the structure ----

    [Theory]
    [MemberData(nameof(WindowWidths))]
    public void The_order_is_the_player_then_the_alarms_then_one_bottom_row_with_volumes_and_outputs_side_by_side_or_stacked_when_narrow(double width)
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig(width);
            var page = rig.Page;
            var hero = rig.BoundsOf(page.PlayerHero);
            var alarms = rig.BoundsOf(page.AlarmsSection);
            var volumes = rig.BoundsOf(page.VolumesSection);
            var outputs = rig.BoundsOf(page.OutputsSection);

            Assert.Equal(
                ["Despertadores", "Volumes ao iniciar", "Sa\U000000EDdas de \U000000E1udio"],
                new[] { page.AlarmsSection, page.VolumesSection, page.OutputsSection }.Select(s => (string)s.Header));

            // Player on top, as wide as the page; the alarms under it; the bottom row under the alarms.
            Assert.True(hero.Bottom <= alarms.Top);
            Assert.True(alarms.Bottom <= volumes.Top);
            Assert.True(alarms.Bottom <= outputs.Top);
            Assert.Equal(hero.Left, alarms.Left, 1);
            Assert.Equal(hero.Right, alarms.Right, 1);
            Assert.Equal(hero.Left, volumes.Left, 1);
            Assert.Equal(hero.Width, alarms.Width, 1);
            Assert.True(hero.Width >= Math.Min(1280, width - 172 - (24 * 2) - 20), $"{hero.Width}"); // the page minus the side bar and its margins (at most 1280): it is the whole width

            // The bottom row is the end of the page: nothing under it.
            Assert.True(rig.ContentBottom - Math.Max(volumes.Bottom, outputs.Bottom) <= 24 + 1);

            Assert.Equal(TwoColumns(width), !page.IsSingleColumn);
            if (TwoColumns(width))
            {
                Assert.True(outputs.Left >= volumes.Right + 16, "side by side with a gap");
                Assert.Equal(volumes.Top, outputs.Top, 1);
                Assert.Equal(volumes.Height, outputs.Height, 1); // the two cards end on one line
                Assert.Equal(hero.Right, outputs.Right, 1);
            }
            else
            {
                Assert.True(outputs.Top >= volumes.Bottom + 16, "stacked: Volumes first, then Sa\U000000EDdas");
                Assert.Equal(volumes.Left, outputs.Left, 1);
                Assert.Equal(volumes.Width, outputs.Width, 1);
            }

            // The old cards and their texts are gone.
            var texts = rig.Find<TextBlock>().Select(t => t.Text).ToList();
            Assert.DoesNotContain("Playlist", texts);
            Assert.DoesNotContain("Playlists", texts);
            Assert.DoesNotContain("Comportamento do player", texts);
            Assert.DoesNotContain(texts, t => t.StartsWith("De onde vem a m\U000000FAsica", StringComparison.Ordinal));
            Assert.DoesNotContain(rig.Find<SettingRow>(), _ => true);
            Assert.DoesNotContain(rig.Find<RadioButton>(), r => AutomationProperties.GetName(r) is "Com som" or "Mudo");
        });
    }

    [Fact]
    public void The_cards_are_lighter_than_the_page_with_a_thin_border_a_12_px_radius_and_24_px_inside()
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig(1100, Mode.Playing);
            var page = Luminance(Resource("IbtBackgroundBrush"));
            var card = Luminance(Resource("IbtSurfaceBrush"));
            var block = Luminance(Resource("IbtSurfaceRaisedBrush"));
            var inset = Luminance(Resource("IbtInsetBrush"));

            Assert.True(card >= page + 8, "a card is visibly lighter than the page");
            Assert.True(block > card + 4, "a block inside a card is lighter again");
            Assert.True(inset < card, "an input is a darker well");

            var hero = rig.Page.PlayerHero;
            Assert.Same(Resource("IbtSurfaceBrush"), hero.Background);
            Assert.Equal(new Thickness(1), hero.BorderThickness);
            Assert.Equal(12, hero.CornerRadius.TopLeft);
            Assert.Equal(new Thickness(24), hero.Padding);

            foreach (var section in new[] { rig.Page.AlarmsSection, rig.Page.VolumesSection, rig.Page.OutputsSection })
            {
                var border = Descendants(section).OfType<Border>().First();
                Assert.Same(Resource("IbtSurfaceBrush"), border.Background);
                Assert.Equal(new Thickness(1), border.BorderThickness);
                Assert.Equal(12, border.CornerRadius.TopLeft);
                Assert.Equal(new Thickness(24), border.Padding);
            }

            // the strip and the alarm blocks are the lighter blocks
            Assert.Same(Resource("IbtSurfaceRaisedBrush"), rig.Page.PlaylistStrip.Background);
            Assert.All(AlarmBlocks(rig), b => Assert.Same(Resource("IbtSurfaceRaisedBrush"), ((Border)b).Background));
        });
    }

    [Fact]
    public void The_window_opens_around_1100_by_860_and_works_from_900()
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig(1100);
            Assert.Equal(1100, rig.Window.Width);
            Assert.Equal(860, rig.Window.Height);
            Assert.Equal(900, rig.Window.MinWidth);
        });
    }

    [Theory]
    [MemberData(nameof(WindowWidths))]
    public void The_hero_has_the_art_on_the_left_the_timeline_full_width_and_the_transport_centred_under_it(double width)
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig(width, Mode.Playing);
            var page = rig.Page;
            var hero = rig.BoundsOf(page.PlayerHero);
            var art = rig.BoundsOf(page.ArtHost);
            var title = rig.BoundsOf(page.HeroTitleText);
            var slider = rig.BoundsOf(page.SeekSlider);
            var transport = rig.BoundsOf(page.TransportCenter);

            // The art: 168 square, at the left edge of the card, its own width; everything else is at its right.
            Assert.Equal(168, art.Width, 1);
            Assert.Equal(168, art.Height, 1);
            Assert.Equal(hero.Left + 24 + 1, art.Left, 1); // card padding and border
            Assert.True(title.Left >= art.Right + 20);
            Assert.True(slider.Left >= art.Right + 20);
            Assert.True(rig.BoundsOf(page.PlayPauseButton).Left > art.Right);
            Assert.InRange(art.Top, hero.Top, hero.Bottom - 168);

            // Title, then the timeline, then the transport, top to bottom.
            Assert.True(slider.Top >= title.Bottom);
            Assert.True(transport.Top >= slider.Bottom);

            // The timeline: the elapsed label, the slider and the length share the line; the labels are the same width.
            var elapsed = rig.BoundsOf(page.ElapsedLabel);
            var length = rig.BoundsOf(page.DurationLabel);
            Assert.Equal(elapsed.Width, length.Width, 1);
            Assert.True(elapsed.Right <= slider.Left + 0.5);
            Assert.True(length.Left >= slider.Right - 0.5);
            Assert.Equal(CenterY(elapsed), CenterY(slider), 2);
            Assert.Equal(CenterY(length), CenterY(slider), 2);
            Assert.Equal(rig.BoundsOf(page.HeroInfo).Right, length.Right, 1);

            // The transport is centred under the timeline, and the play button is the 56 px green disc.
            Assert.Equal(CenterX(slider), CenterX(transport), 1.5);
            var play = rig.BoundsOf(page.PlayPauseButton);
            Assert.Equal(56, play.Width, 1);
            Assert.Equal(56, play.Height, 1);
            Assert.Equal(CenterX(transport), CenterX(play), 1.5);
            var order = new FrameworkElement[] { page.ShuffleToggle, page.PreviousButton, page.PlayPauseButton, page.StopButton, page.NextButton, page.RepeatButton };
            for (var i = 1; i < order.Length; i++)
            {
                Assert.True(rig.BoundsOf(order[i]).Left >= rig.BoundsOf(order[i - 1]).Right);
            }

            // The corner: only the switch (the next alarm line lives in the alarms header, never twice).
            var corner = rig.BoundsOf(page.HeroCorner);
            Assert.Equal(rig.BoundsOf(page.HeroInfo).Right, corner.Right, 1);
            Assert.True(corner.Top <= title.Top + 4);
            Assert.True(rig.BoundsOf(page.PlayerOnSwitch).Right <= hero.Right);
            Assert.DoesNotContain(rig.Find<TextBlock>(), t => t.Text.StartsWith("Pr\U000000F3ximo despertador", StringComparison.Ordinal));

            // No volume in the hero any more (the Player row of the volumes card is the only one): the timeline is the only slider of the hero.
            Assert.Single(rig.Find<Slider>(), c => Shown(c) && IsInside(c, page.PlayerHero));
            Assert.DoesNotContain(rig.Find<Control>().Where(c => IsInside(c, page.PlayerHero)), c => c.ToolTip is string tip && tip.Contains("Volume do player", StringComparison.Ordinal));

            // Stop: a smaller round outline button right next to Play (a ring, not the big filled disc), centred on the same line, with its automation name.
            var stop = rig.BoundsOf(page.StopButton);
            Assert.Equal(32, stop.Width, 1);
            Assert.Equal(stop.Width, stop.Height, 1);
            Assert.True(stop.Width < play.Width - 16, "smaller than Play");
            Assert.Equal(CenterY(play), CenterY(stop), 1.5);
            Assert.InRange(stop.Left - play.Right, 4, 24); // next to Play
            Assert.Equal("Parar", System.Windows.Automation.AutomationProperties.GetName(page.StopButton));
            Assert.Equal("Parar (com fade)", page.StopButton.ToolTip);
            var ring = (Border)page.StopButton.Template.FindName("Ring", page.StopButton);
            Assert.Equal(new Thickness(1.5), ring.BorderThickness);
            Assert.Equal(16, ring.CornerRadius.TopLeft, 1); // round
            Assert.True(((SolidColorBrush)ring.BorderBrush).Color.A > 0x60, "a visible ring");
            Assert.Equal(Brushes.Transparent, ring.Background); // outline, not filled

            // Nothing is cut: the title may have an ellipsis, but no control of the card leaves it.
            foreach (var control in rig.Find<Control>().Where(c => Shown(c) && IsInside(c, page.PlayerHero)))
            {
                var bounds = rig.BoundsOf(control);
                Assert.True(bounds.Left >= hero.Left - 0.5 && bounds.Right <= hero.Right + 0.5, $"{control.GetType().Name} {bounds}");
            }
        });
    }

    [Theory]
    [MemberData(nameof(WindowWidths))]
    public void The_curve_selector_is_live_with_a_crossfade_and_each_button_picks_its_curve(double width)
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig(width, Mode.Playing, crossfade: 8, curve: CrossfadeCurve.Intermediate);
            var page = rig.Page;
            Assert.True(page.CurveFrame.IsEnabled);
            Assert.True(page.CurveIntermediateOption.IsChecked);
            Assert.False(page.CurveLinearOption.IsChecked);
            page.CurveConstantOption.IsChecked = true;
            Assert.Equal(CrossfadeCurve.ConstantPower, rig.Som.PlayerView.CrossfadeCurve);
            page.CurveLinearOption.IsChecked = true;
            Assert.Equal(CrossfadeCurve.Linear, rig.Som.PlayerView.CrossfadeCurve);
            Assert.True(page.CurveLinearOption.IsChecked);
            Assert.False(page.CurveConstantOption.IsChecked);
            rig.Page.CrossfadeCombo.SelectedValue = 0.0;
            Assert.False(page.CurveFrame.IsEnabled);
        });
    }

    [Theory]
    [MemberData(nameof(WindowWidths))]
    public void The_playlist_strip_is_inside_the_hero_at_its_bottom_the_list_and_the_folder_side_by_side_when_they_fit_and_the_behaviour_line_under_them(double width)
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig(width, Mode.Playing);
            var page = rig.Page;
            var hero = rig.BoundsOf(page.PlayerHero);
            var strip = rig.BoundsOf(page.PlaylistStrip);
            var left = rig.BoundsOf(page.PlaylistHalf);
            var folderHalf = rig.BoundsOf(page.FolderHalf);
            var behavior = rig.BoundsOf(page.BehaviorHalf);

            Assert.True(IsInside(page.PlaylistStrip, page.PlayerHero));
            Assert.True(strip.Left >= hero.Left && strip.Right <= hero.Right && strip.Bottom <= hero.Bottom);
            Assert.True(strip.Top >= rig.BoundsOf(page.TransportRow).Bottom, "the strip is under the transport");
            Assert.Equal(hero.Bottom - 24 - 1, strip.Bottom, 1); // at the bottom of the hero, its padding under it

            // The list controls and the folder controls sit side by side when there is room (each needs 300 px), stacked (list first) when there is not.
            Assert.Equal(width >= 1100, page.StripHalves.Columns == 2);
            Assert.True(left.Left >= strip.Left + 16 && folderHalf.Right <= strip.Right - 16 + 0.5);
            if (page.StripHalves.Columns == 2)
            {
                Assert.True(folderHalf.Left >= left.Right + 16);
                Assert.Equal(left.Top, folderHalf.Top, 1);
            }
            else
            {
                Assert.True(folderHalf.Top >= left.Bottom + 8 - 0.5);
                Assert.Equal(left.Left, folderHalf.Left, 1);
            }

            // The list with three icon buttons right next to it, then the folder (small, ellipsis) with two icon buttons.
            var combo = rig.BoundsOf(page.PlaylistCombo);
            var add = rig.BoundsOf(page.AddPlaylistButton);
            var rename = rig.BoundsOf(page.RenamePlaylistButton);
            var remove = rig.BoundsOf(page.RemovePlaylistButton);
            Assert.True(add.Left >= combo.Right && add.Left - combo.Right <= 12, "the buttons sit right next to the list");
            Assert.True(rename.Left >= add.Right && remove.Left >= rename.Right);
            Assert.All(new[] { add, rename, remove }, b => Assert.Equal(32, b.Width, 1));
            Assert.All(new[] { combo, add, rename, remove }, b => Assert.Equal(CenterY(combo), CenterY(b), 1));
            var folder = rig.BoundsOf(page.PlaylistInfoText);
            var change = rig.BoundsOf(page.ChangeFolderButton);
            var refresh = rig.BoundsOf(page.RefreshButton);
            Assert.True(folder.Right <= change.Left && change.Right <= refresh.Left);
            Assert.Equal(CenterY(folder), CenterY(change), 1);
            Assert.Equal(CenterY(change), CenterY(refresh), 1);
            if (page.StripHalves.Columns == 1)
            {
                Assert.True(folder.Top >= combo.Bottom);
                Assert.Equal(remove.Right, refresh.Right, 1); // both rows end at the same edge
            }
            else
            {
                Assert.Equal(CenterY(combo), CenterY(change), 1); // one line: list on the left, folder on the right
            }

            Assert.Equal(TextTrimming.CharacterEllipsis, page.PlaylistInfoText.TextTrimming);
            Assert.Equal(@"C:\Users\gutni\Music\Mornye\Igreja", page.PlaylistInfoText.Text);
            Assert.Contains("2 m\U000000FAsicas", (string)page.PlaylistInfoText.ToolTip, StringComparison.Ordinal);
            Assert.Contains(@"C:\Users\gutni\Music\Mornye\Igreja", (string)page.PlaylistInfoText.ToolTip, StringComparison.Ordinal);

            // Under them, in their own line: fade-in, fade-out, crossfade and the media keys, each an icon and its control, no written label.
            Assert.True(behavior.Top >= Math.Max(left.Bottom, folderHalf.Bottom) + 16 - 0.5, "the behaviour line is under the playlist controls, not beside them");
            Assert.True(behavior.Left >= strip.Left + 16 - 0.5 && behavior.Right <= strip.Right - 16 + 0.5);
            var rows = new FrameworkElement[] { page.FadeInRow, page.FadeOutRow, page.CrossfadeRow, page.CurveFrame, page.MediaKeysRow };
            var dropdowns = new[] { page.FadeInCombo, page.FadeOutCombo, page.CrossfadeCombo };
            foreach (var row in rows)
            {
                var bounds = rig.BoundsOf(row);
                Assert.True(bounds.Left >= behavior.Left - 0.5 && bounds.Right <= behavior.Right + 0.5, row.Name);
                Assert.NotEmpty(Descendants(row).OfType<Shape>());
                if (row != page.CurveFrame)
                {
                    Assert.False(string.IsNullOrWhiteSpace(row.ToolTip as string), row.Name);
                }
            }

            // The two fades, the crossfade time and the three curve buttons on ONE line, left to right in this order, at every width (the curve buttons never
            // wrap); the media-keys switch follows on the same line, or wraps under them when the line is too long (900 px).
            var rowBounds = rows.Select(r => rig.BoundsOf(r)).ToList();
            var line = rowBounds.Take(4).ToList();
            Assert.All(line, b => Assert.Equal(line[0].Top + ((line[0].Height - b.Height) / 2), b.Top, 1)); // centered on one line
            for (var i = 1; i < line.Count; i++)
            {
                Assert.True(line[i - 1].Right <= line[i].Left, $"row {i} overlaps the one before");
            }

            if (width >= 1000)
            {
                Assert.Equal(CenterY(rig.BoundsOf(page.CrossfadeCombo)), CenterY(rowBounds[4]), 1);
                Assert.True(line[3].Right <= rowBounds[4].Left);
                Assert.Equal(strip.Right - 16, rowBounds[4].Right, 1.5); // the line sits at the right side of the strip
            }
            else
            {
                Assert.True(rowBounds[4].Left >= line[0].Left && rowBounds[4].Top >= line[0].Bottom - 0.5 || rowBounds[4].Left >= line[3].Right, "the media-keys switch is beside the line or under it");
            }

            // The curve selector: three icon-only buttons in one frame, 32 px high, next to the crossfade dropdown, dimmed with no crossfade.
            var curveOptions = new[] { page.CurveLinearOption, page.CurveConstantOption, page.CurveIntermediateOption };
            var frame = rowBounds[3];
            Assert.Equal(32, frame.Height, 1);
            Assert.True(frame.Left - rig.BoundsOf(page.CrossfadeCombo).Right <= 12, "the curve buttons sit right next to the crossfade dropdown");
            Assert.All(curveOptions, o => Assert.True(rig.BoundsOf(o).Left >= frame.Left && rig.BoundsOf(o).Right <= frame.Right + 0.5));
            Assert.Equal(["Linear", "Volume constante", "Intermediária"], curveOptions.Select(o => AutomationProperties.GetName(o)));
            Assert.All(curveOptions, o => Assert.False(string.IsNullOrWhiteSpace(o.ToolTip as string)));
            Assert.All(curveOptions, o => Assert.Empty(Descendants(o).OfType<TextBlock>()));
            Assert.True(page.CurveLinearOption.IsChecked == true, "Linear is the default");
            Assert.False(page.CurveFrame.IsEnabled, "no crossfade: the curve selector is dimmed");

            var combos = dropdowns.Select(c => rig.BoundsOf(c)).ToList();
            Assert.All(combos, b => Assert.Equal(32, b.Height, 1));
            Assert.All(combos, b => Assert.Equal(CenterY(combos[0]), CenterY(b), 1));
            Assert.Equal(combos[0].Width, combos[1].Width, 1); // the two fades are alike, the crossfade one is wider for "sem mescla"
            Assert.InRange(combos[0].Width, 90, 104);
            Assert.InRange(combos[2].Width, 120, 136);
            if (width >= 1000)
            {
                Assert.Equal(CenterY(combos[0]), CenterY(rig.BoundsOf(page.MediaKeysSwitch)), 1);
            }


            // What the dropdowns offer, the names and the tooltips.
            Assert.Equal(["seco", "0,5 s", "1 s", "2 s", "3 s", "4 s", "5 s", "6 s", "7 s", "8 s", "9 s", "10 s"], page.FadeInCombo.Items.Cast<SecondsChoice>().Select(o => o.Label));
            Assert.Equal(page.FadeInCombo.Items.Cast<SecondsChoice>().Select(o => o.Label), page.FadeOutCombo.Items.Cast<SecondsChoice>().Select(o => o.Label));
            Assert.Equal(["sem mescla", "0,5 s", "1 s", "2 s", "3 s", "4 s", "5 s", "6 s", "7 s", "8 s", "9 s", "10 s", "11 s", "12 s"], page.CrossfadeCombo.Items.Cast<SecondsChoice>().Select(o => o.Label));
            Assert.StartsWith("Fade-in (play e despertador)", (string)page.FadeInRow.ToolTip, StringComparison.Ordinal);
            Assert.StartsWith("Fade-out (pausar e parar)", (string)page.FadeOutRow.ToolTip, StringComparison.Ordinal);
            Assert.StartsWith("Mescla entre m\U000000FAsicas", (string)page.CrossfadeRow.ToolTip, StringComparison.Ordinal);
            Assert.Contains("Teclas de m\U000000EDdia", (string)page.MediaKeysRow.ToolTip, StringComparison.Ordinal);
            Assert.Equal("Teclas de m\U000000EDdia controlam o player", AutomationProperties.GetName(page.MediaKeysSwitch));
            Assert.Equal("Fade-in (play e despertador)", AutomationProperties.GetName(page.FadeInCombo));
            Assert.Equal("Fade-out (pausar e parar)", AutomationProperties.GetName(page.FadeOutCombo));
            Assert.Equal("Mescla entre m\U000000FAsicas", AutomationProperties.GetName(page.CrossfadeCombo));
            Assert.All(dropdowns, c => Assert.Equal(246, c.MaxDropDownHeight)); // 8 rows, then it scrolls
        });
    }

    [Fact]
    public void The_dropdowns_show_the_saved_values_and_choosing_one_saves_it()
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig(1100, Mode.Playing);
            var page = rig.Page;
            Assert.Equal(0.0, (double)page.FadeInCombo.SelectedValue);
            Assert.Equal(5.0, (double)page.FadeOutCombo.SelectedValue);
            Assert.Equal(0.0, (double)page.RampCombo.SelectedValue);
            Assert.Equal("seco", ((SecondsChoice)page.RampCombo.SelectedItem).Label);
            Assert.Equal(0.0, (double)page.CrossfadeCombo.SelectedValue);
            Assert.Equal("sem mescla", ((SecondsChoice)page.CrossfadeCombo.SelectedItem).Label);

            page.FadeOutCombo.SelectedIndex = 1; // 0,5 s
            page.FadeInCombo.SelectedIndex = 4; // 3 s
            page.CrossfadeCombo.SelectedIndex = 3; // 2 s
            page.RampCombo.SelectedIndex = 3; // 2 s

            var saved = rig.Som.Settings.Settings.Current.Player;
            Assert.Equal(0.5, saved.FadeOutSeconds);
            Assert.Equal(3, saved.FadeInSeconds);
            Assert.Equal(2, saved.CrossfadeSeconds);
            Assert.Equal(2, rig.Som.Settings.Settings.Current.Audio.VolumeRampSeconds);

            // The ramp menu: icon and tooltip only, above the volume rows inside the "Volumes ao iniciar" card (the card keeps its name).
            Assert.Equal("Volumes ao iniciar", page.VolumesSection.Header);
            Assert.StartsWith("Suavizar mudança de volume", (string)page.RampRow.ToolTip, StringComparison.Ordinal);
            Assert.Equal("Suavizar mudança de volume", AutomationProperties.GetName(page.RampCombo));
            Assert.True(rig.BoundsOf(page.RampRow).Bottom <= rig.BoundsOf(page.VolumeList).Top);
            Assert.True(IsInside(page.RampCombo, page.VolumesSection));
            Assert.Empty(page.RampRow.Children.OfType<TextBlock>());
        });
    }

    [Fact]
    public void Every_icon_only_button_and_toggle_has_a_tooltip_and_an_automation_name()
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig(1100, Mode.Playing, alarms: 3);
            var checkedCount = 0;
            foreach (var control in rig.Find<ButtonBase>().Where(Shown))
            {
                if (control is RepeatButton)
                {
                    continue; // the invisible rails of a slider
                }

                if (control.TemplatedParent is ComboBox)
                {
                    continue; // the click area inside a list box
                }

                if (control is ToggleButton { Content: string })
                {
                    continue; // the weekday dots have a letter and carry the name of the day as tooltip too
                }

                var name = AutomationProperties.GetName(control);
                Assert.False(string.IsNullOrWhiteSpace(name), $"{control.GetType().Name} {control.Name} {control.Content} without an automation name; parent {control.Parent?.GetType().Name} {control.TemplatedParent?.GetType().Name}");
                var tip = control.ToolTip;
                Assert.True(tip is not null && !string.IsNullOrWhiteSpace(tip.ToString()), $"{name} without a tooltip");
                checkedCount++;
            }

            foreach (var chip in rig.Find<ToggleButton>().Where(t => t.Content is string { Length: 1 }))
            {
                Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(chip)));
                Assert.False(string.IsNullOrWhiteSpace(chip.ToolTip?.ToString()));
            }

            Assert.True(checkedCount >= 20, checkedCount.ToString());
        });
    }

    // ---- the hero ----

    [Fact]
    public void The_art_shows_the_cover_when_there_is_one_and_the_music_tile_when_there_is_not()
    {
        WpfTestHost.Run(() =>
        {
            using (var withArt = new Rig(1100, Mode.Playing))
            {
                Assert.True(withArt.Player.HasCover);
                Assert.Equal(Visibility.Visible, withArt.Page.CoverImage.Visibility);
                Assert.Equal(Visibility.Collapsed, withArt.Page.CoverPlaceholder.Visibility);
                Assert.Equal(12, withArt.Page.CoverImage.CornerRadius.TopLeft);
            }

            using var without = new Rig(1100, Mode.Playing, art: false);
            Assert.False(without.Player.HasCover);
            Assert.Equal(Visibility.Collapsed, without.Page.CoverImage.Visibility);
            Assert.Equal(Visibility.Visible, without.Page.CoverPlaceholder.Visibility);
            Assert.Equal(12, without.Page.CoverPlaceholder.CornerRadius.TopLeft);
            var note = Descendants(without.Page.CoverPlaceholder).OfType<Shape>().Single();
            Assert.Equal(Application.Current.FindResource("LucideMusic"), note.Data);
            Assert.True(without.BoundsOf(without.Page.CoverPlaceholder).Width >= 168);
        });
    }

    [Fact]
    public void The_hero_tells_what_plays_and_where_it_is_in_the_list()
    {
        WpfTestHost.Run(() =>
        {
            using var playing = new Rig(1100, Mode.Playing);
            var page = playing.Page;

            Assert.Contains(page.HeroTitleText.Text, new[] { "Amazing Grace", "Be Still" });
            Assert.Contains(page.HeroArtistText.Text, new[] { "Josh", "Rivers" });
            Assert.Equal("Igreja", page.PlaylistChipText.Text);
            Assert.Matches(@"^[12] de 2$", page.TrackIndexLabel.Text);
            Assert.Equal("1:15", page.ElapsedLabel.Text);
            Assert.Equal("3:20", page.DurationLabel.Text);
            Assert.True(page.SeekSlider.IsEnabled);
            Assert.Equal(200, page.SeekSlider.Maximum);
            Assert.Equal(75, page.SeekSlider.Value, 0);
            Assert.Equal("Pausar a m\U000000FAsica", AutomationProperties.GetName(page.PlayPauseButton));
            Assert.False(string.IsNullOrEmpty(page.HeroTitleText.ToolTip as string));
        });
    }

    [Fact]
    public void A_paused_player_keeps_its_track_and_position_and_the_play_button_says_play()
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig(1100, Mode.Paused);
            var page = rig.Page;

            Assert.Equal(PlayerState.Paused, rig.Player.State);
            Assert.True(page.SeekSlider.IsEnabled);
            Assert.Equal("1:15", page.ElapsedLabel.Text);
            Assert.Equal("Tocar a m\U000000FAsica", AutomationProperties.GetName(page.PlayPauseButton));
            Assert.Equal(Visibility.Visible, page.CoverImage.Visibility);
        });
    }

    [Fact]
    public void With_the_player_off_the_hero_is_calm_and_dead_except_for_the_switch()
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig(1100, Mode.Off);
            var page = rig.Page;

            Assert.Equal("Player desligado", page.HeroTitleText.Text);
            Assert.False(page.PlayerOnSwitch.IsChecked);
            Assert.True(page.PlayerOnSwitch.IsEnabled);
            Assert.False(page.PlayPauseButton.IsEnabled);
            Assert.False(page.SeekSlider.IsEnabled);
            Assert.False(page.NextButton.IsEnabled);
            Assert.InRange(page.ArtHost.Opacity, 0.2, 0.6);
            Assert.Equal(Visibility.Visible, page.CoverPlaceholder.Visibility);
            Assert.Equal(StatusLight.Gray, rig.Som.Som.PlayerOutput.Light);

            rig.Player.PlayerOn = true;
            rig.Layout(1100);

            Assert.True(page.PlayPauseButton.IsEnabled);
            Assert.Equal(1, page.ArtHost.Opacity);
            Assert.Equal("Nada tocando", page.HeroTitleText.Text);
        });
    }

    // ---- the timeline control ----

    [Fact]
    public void Dragging_the_timeline_moves_the_label_but_not_the_music_and_letting_go_seeks()
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig(1100, Mode.Playing);
            var page = rig.Page;

            page.SeekSlider.BeginDrag(120);
            page.SeekSlider.DragTo(150);
            rig.Layout(1100);

            Assert.True(page.SeekSlider.IsDragging);
            Assert.True(rig.Player.IsSeeking);
            Assert.Equal("2:30", page.ElapsedLabel.Text);
            Assert.Equal(150, page.SeekSlider.Value, 0);
            Assert.InRange(rig.Som.Player.GetStatus().Position.TotalSeconds, 74, 77); // the music did not jump

            page.SeekSlider.EndDrag(150);
            rig.Listen(0.3);
            rig.Layout(1100);

            Assert.False(page.SeekSlider.IsDragging);
            Assert.False(rig.Player.IsSeeking);
            Assert.InRange(rig.Som.Player.GetStatus().Position.TotalSeconds, 150, 151.5);
        });
    }

    [Fact]
    public void The_arrow_keys_move_the_timeline_five_seconds_and_a_disabled_timeline_ignores_them()
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig(1100, Mode.Playing);
            var page = rig.Page;

            Assert.True(page.SeekSlider.HandleKey(Key.Right));
            rig.Listen(0.3);
            Assert.InRange(rig.Som.Player.GetStatus().Position.TotalSeconds, 80, 81.5);

            Assert.True(page.SeekSlider.HandleKey(Key.Left));
            rig.Listen(0.3);
            Assert.InRange(rig.Som.Player.GetStatus().Position.TotalSeconds, 75, 77);
            Assert.False(page.SeekSlider.HandleKey(Key.Up));

            using var idle = new Rig(1100);
            Assert.False(idle.Page.SeekSlider.HandleKey(Key.Right));
            Assert.False(idle.Page.SeekSlider.IsEnabled);
        });
    }

    // ---- the alarms ----

    [Theory]
    [MemberData(nameof(WindowWidths))]
    public void The_alarms_header_has_the_title_the_master_toggle_and_the_next_alarm_and_each_alarm_is_its_own_block_two_per_row_or_one_when_narrow(double width)
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig(width, alarms: 3);
            var page = rig.Page;
            var section = rig.BoundsOf(page.AlarmsSection);
            var blocks = AlarmBlocks(rig);
            var add = Descendants(page.AlarmList).OfType<Button>().Single(b => AutomationProperties.GetName(b) == "Adicionar despertador");

            // Header: the title, the next alarm (short) and the master toggle at the right end.
            Assert.Equal("Despertadores", page.AlarmsSection.Header);
            Assert.StartsWith("pr\U000000F3ximo: ", page.AlarmsNextText.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("despertador:", page.AlarmsNextText.Text, StringComparison.Ordinal);
            Assert.True(rig.BoundsOf(page.AlarmsOnSwitch).Right <= section.Right - 24);
            Assert.True(rig.BoundsOf(page.AlarmsNextText).Right <= rig.BoundsOf(page.AlarmsOnSwitch).Left);
            Near(32, rig.BoundsOf(page.AlarmsOnSwitch).Width);
            Assert.False(string.IsNullOrWhiteSpace(page.AlarmsOnSwitch.ToolTip as string));

            // Blocks: 3 alarms and the dashed plus block at the end; 2 per row when there is room, 1 per row when narrow.
            Assert.Equal(3, blocks.Count);
            Assert.Equal(4, page.AlarmList.Items.Count);
            Assert.IsType<AddAlarmTile>(page.AlarmList.Items[^1]);
            var all = blocks.Select(rig.BoundsOf).ToList();
            var addBounds = rig.BoundsOf(add);
            if (width >= 1000)
            {
                Assert.Equal(all[0].Top, all[1].Top, 1);
                Assert.True(all[1].Left >= all[0].Right + 16 - 0.5);
                Assert.Equal(all[0].Width, all[1].Width, 1);
                Assert.True(all[2].Top >= all[0].Bottom + 16 - 0.5, "the third block starts the second row");
                Assert.Equal(all[0].Left, all[2].Left, 1);
                Assert.Equal(all[1].Left, addBounds.Left, 1); // the plus block follows the third
                Assert.Equal(all[2].Top, addBounds.Top, 1);
            }
            else
            {
                Assert.All(all, b => Assert.Equal(all[0].Left, b.Left, 1));
                Assert.All(all, b => Assert.Equal(all[0].Width, b.Width, 1));
                Assert.True(all[1].Top >= all[0].Bottom + 16 - 0.5);
                Assert.True(addBounds.Top >= all[2].Bottom + 16 - 0.5);
            }

            Assert.All(all, b => Assert.True(b.Left >= section.Left + 24 && b.Right <= section.Right - 24 + 1));
            Assert.True(addBounds.Bottom <= section.Bottom - 24 + 1);
        });
    }

    [Theory]
    [MemberData(nameof(WindowWidths))]
    public void An_alarm_block_has_almost_no_text_the_switch_a_start_a_limit_seven_dots_the_list_and_the_trash_with_tooltips(double width)
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig(width);
            var blocks = AlarmBlocks(rig);
            Assert.Equal(2, blocks.Count);

            foreach (var block in blocks)
            {
                var bounds = rig.BoundsOf(block);
                var all = Descendants(block).OfType<FrameworkElement>().ToList();
                ToggleButton Switch() => all.OfType<ToggleButton>().First(t => AutomationProperties.GetName(t) == "Despertador ligado");
                TextBox Start() => all.OfType<TextBox>().First(t => AutomationProperties.GetName(t) == "Hora de in\U000000EDcio");
                TextBox Latest() => all.OfType<TextBox>().First(t => AutomationProperties.GetName(t) == "N\U000000E3o iniciar depois de");
                Button Trash() => all.OfType<Button>().First(t => AutomationProperties.GetName(t) == "Remover despertador");
                var chips = all.OfType<ToggleButton>().Where(t => t.Content is string { Length: 1 }).ToList();
                var combo = all.OfType<ComboBox>().Single();

                // Line one: switch, start, limit, trash, in that order and on one line; the trash at the right edge of the block.
                Assert.True(rig.BoundsOf(Switch()).Right < rig.BoundsOf(Start()).Left);
                Assert.True(rig.BoundsOf(Start()).Right < rig.BoundsOf(Latest()).Left);
                Assert.True(rig.BoundsOf(Latest()).Right < rig.BoundsOf(Trash()).Left);
                Assert.Equal(CenterY(rig.BoundsOf(Start())), CenterY(rig.BoundsOf(Latest())), 1);
                Assert.Equal(CenterY(rig.BoundsOf(Start())), CenterY(rig.BoundsOf(Trash())), 1);
                Assert.Equal(bounds.Right - 16 - 1, rig.BoundsOf(Trash()).Right, 1);
                Near(32, rig.BoundsOf(Start()).Height);
                Near(32, rig.BoundsOf(Trash()).Height);

                // Line two: seven dots and the list, to the right of them, ending at the right padding.
                Assert.Equal(7, chips.Count);
                Assert.True(rig.BoundsOf(chips[0]).Top > rig.BoundsOf(Start()).Bottom);
                for (var c = 1; c < 7; c++)
                {
                    Assert.Equal(rig.BoundsOf(chips[0]).Top, rig.BoundsOf(chips[c]).Top, 1);
                    Assert.True(rig.BoundsOf(chips[c]).Left > rig.BoundsOf(chips[c - 1]).Left);
                }

                Assert.True(rig.BoundsOf(combo).Left >= rig.BoundsOf(chips[6]).Right);
                Assert.Equal(bounds.Right - 16 - 1, rig.BoundsOf(combo).Right, 1);
                Assert.True(rig.BoundsOf(combo).Width >= 100, "the list is wide enough to read");

                // Almost no text: only the letters of the dots, the times, the list and the switch; the field names are tooltips.
                Assert.DoesNotContain(all.OfType<TextBlock>().Where(Shown), t => t.Text.Length > 1 && !t.Text.Contains(':', StringComparison.Ordinal) && t.Text != "Igreja" && t.Text != "Louvor");
                Assert.False(string.IsNullOrWhiteSpace(Start().ToolTip as string));
                Assert.Contains("In\U000000EDcio", ((string)Start().ToolTip), StringComparison.OrdinalIgnoreCase);
                Assert.Contains("N\U000000E3o iniciar depois de", (string)Latest().ToolTip, StringComparison.Ordinal);
                Assert.Equal("Remover este despertador", Trash().ToolTip);
                Assert.Equal(["Domingo", "Segunda", "Ter\U000000E7a", "Quarta", "Quinta", "Sexta", "S\U000000E1bado"], chips.Select(c => (string)c.ToolTip));
            }

            // The two blocks use the same columns.
            if (width >= 1000)
            {
                var a = Descendants(blocks[0]).OfType<TextBox>().First();
                var b = Descendants(blocks[1]).OfType<TextBox>().First();
                Assert.Equal(rig.BoundsOf(a).Left - rig.BoundsOf(blocks[0]).Left, rig.BoundsOf(b).Left - rig.BoundsOf(blocks[1]).Left, 1);
            }
        });
    }

    [Fact]
    public void The_alarms_show_their_time_weekdays_limit_and_playlist_and_a_switched_off_alarm_is_dimmer()
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig(1100);
            var page = rig.Page;

            Assert.Equal(2, rig.Som.Som.Alarms.Count);
            Assert.Equal(3, page.AlarmList.Items.Count); // two alarms and the plus block
            Assert.StartsWith("pr\U000000F3ximo:", page.AlarmsNextText.Text, StringComparison.Ordinal);
            var chips = rig.Find<ToggleButton>().Where(t => t.Content is string { Length: 1 } letter && "DSTQ".Contains(letter, StringComparison.Ordinal)).ToList();
            Assert.Equal(14, chips.Count);
            Assert.Equal(["D", "S", "T", "Q", "Q", "S", "S"], chips.Take(7).Select(c => (string)c.Content));
            Assert.Equal([true, false, false, false, false, false, false], chips.Take(7).Select(c => c.IsChecked == true));
            Assert.Equal([false, false, false, true, false, false, false], chips.Skip(7).Select(c => c.IsChecked == true));
            Assert.Equal(["19:30"], Descendants(page.AlarmList).OfType<TextBox>().Select(t => t.Text).Where(t => t == "19:30"));

            var blocks = AlarmBlocks(rig);
            Assert.Equal(1, blocks[1].Opacity);
            rig.Som.Som.Alarms[1].Enabled = false;
            rig.Layout(1100);
            Assert.InRange(AlarmBlocks(rig)[1].Opacity, 0.4, 0.8);
            Assert.Equal(1, AlarmBlocks(rig)[0].Opacity);
        });
    }

    [Fact]
    public void With_no_alarm_the_grid_is_only_the_dashed_plus_block_with_a_word_and_it_adds_one()
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig(1100, alarms: 0);
            var page = rig.Page;

            Assert.Empty(AlarmBlocks(rig));
            var add = Descendants(page.AlarmList).OfType<Button>().Single();
            Assert.Equal("Adicionar despertador", AutomationProperties.GetName(add));
            Assert.Equal("Adicionar despertador", add.ToolTip);
            Assert.Contains(Descendants(add).OfType<TextBlock>(), t => t.Text == "Adicionar despertador" && Shown(t));

            add.Command.Execute(null);
            rig.Layout(1100);

            Assert.Single(AlarmBlocks(rig));
            Assert.Equal(2, page.AlarmList.Items.Count);
            Assert.DoesNotContain(Descendants(page.AlarmList).OfType<TextBlock>(), t => t.Text == "Adicionar despertador" && Shown(t)); // with one alarm the plus block is only a plus
        });
    }

    [Fact]
    public void Removing_an_alarm_asks_in_a_window_and_No_keeps_it()
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig(1100);
            var trash = Descendants(rig.Page.AlarmList).OfType<Button>().First(b => AutomationProperties.GetName(b) == "Remover despertador");

            rig.Som.ConfirmAnswer = false;
            trash.Command.Execute(null);
            Assert.Equal(2, rig.Som.PlayerView.Alarms.Count);
            var (title, message) = Assert.Single(rig.Som.Confirmations);
            Assert.Equal("Remover este despertador?", title);
            Assert.Contains("Nenhuma m\U000000FAsica", message, StringComparison.Ordinal);

            rig.Som.ConfirmAnswer = true;
            trash.Command.Execute(null);
            Assert.Single(rig.Som.PlayerView.Alarms);
            rig.Layout(1100);
            Assert.Single(AlarmBlocks(rig));
        });
    }

    [Fact]
    public void The_trash_of_a_playlist_asks_first_and_the_pencil_opens_the_name_box()
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig(1100);
            var page = rig.Page;

            rig.Som.ConfirmAnswer = false;
            page.RemovePlaylistButton.Command.Execute(null);
            Assert.Equal(2, rig.Som.PlayerView.Playlists.Count);
            Assert.Single(rig.Som.Confirmations);

            rig.Som.Names.Enqueue("Domingo");
            page.RenamePlaylistButton.Command.Execute(null);
            Assert.Equal("Domingo", rig.Som.PlayerView.CurrentPlaylist);
            Assert.Equal(["Domingo", "Louvor"], rig.Som.PlayerView.Playlists.Select(p => p.Name));
        });
    }

    // ---- the bottom row ----

    [Theory]
    [MemberData(nameof(WindowWidths))]
    public void The_volumes_are_four_aligned_rows_icon_short_name_slider_percent_box_and_one_mute_toggle(double width)
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig(width);
            var page = rig.Page;
            var rows = page.VolumeList.Items.Cast<VolumeRow>().ToList();
            Assert.Equal(["Windows", "Holyrics", "Player", "Sons do sistema"], rows.Select(r => r.Label));

            var panels = rows.Select(r => (FrameworkElement)Descendants(page.VolumeList.ItemContainerGenerator.ContainerFromItem(r)).OfType<Grid>().First(g => g.Height == 32)).ToList();
            var section = rig.BoundsOf(page.VolumesSection);
            var sliders = panels.Select(p => Descendants(p).OfType<Slider>().Single()).ToList();
            var boxes = panels.Select(p => Descendants(p).OfType<TextBox>().Single()).ToList();
            var mutes = panels.Select(p => Descendants(p).OfType<ToggleButton>().Single()).ToList();

            for (var i = 0; i < 4; i++)
            {
                var slider = rig.BoundsOf(sliders[i]);
                Assert.True(slider.Width >= 64, $"{rows[i].Label}: the slider has room ({slider.Width})");
                Assert.True(slider.Right < rig.BoundsOf(boxes[i]).Left);
                Assert.True(rig.BoundsOf(boxes[i]).Right < rig.BoundsOf(mutes[i]).Left);
                Near(52, rig.BoundsOf(boxes[i]).Width);
                Near(32, rig.BoundsOf(boxes[i]).Height);
                Near(32, rig.BoundsOf(mutes[i]).Width);
                Assert.Equal(rig.BoundsOf(sliders[0]).Left, slider.Left, 1);
                Assert.Equal(rig.BoundsOf(boxes[0]).Left, rig.BoundsOf(boxes[i]).Left, 1);
                Assert.Equal(rig.BoundsOf(mutes[0]).Left, rig.BoundsOf(mutes[i]).Left, 1);
                Assert.Equal(section.Right - 24 - 1, rig.BoundsOf(mutes[i]).Right, 1);
                Assert.Equal("Mudo", AutomationProperties.GetName(mutes[i]));
                Assert.False(string.IsNullOrWhiteSpace(mutes[i].ToolTip as string));
                Assert.False(string.IsNullOrWhiteSpace(rows[i].Tip));
                if (i > 0)
                {
                    Assert.True(rig.BoundsOf(panels[i]).Top >= rig.BoundsOf(panels[i - 1]).Bottom + 8, "rows are separated by spacing");
                }
            }

            // The explanation sentences are tooltips now: no subtitle, no "Com som | Mudo".
            Assert.DoesNotContain(rig.Find<TextBlock>().Where(Shown), t => t.Text.StartsWith("Os volumes que o app p\U000000F5e", StringComparison.Ordinal));
        });
    }

    [Fact]
    public void The_mute_toggle_mutes_a_row_and_the_card_header_has_the_dot_the_apply_button_and_the_master_with_tooltips()
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig(1100);
            var page = rig.Page;
            var holyrics = rig.Som.Som.HolyricsRow;
            var panel = Descendants(page.VolumeList.ItemContainerGenerator.ContainerFromItem(holyrics)).OfType<ToggleButton>().Single();

            Assert.False(panel.IsChecked);
            panel.IsChecked = true;
            Assert.True(holyrics.Muted);
            Assert.True(holyrics.IsMuted);
            Assert.False(holyrics.WithSound);
            panel.IsChecked = false;
            Assert.False(holyrics.Muted);

            // The header: the dot, "Aplicar agora" (zap) and the master, at the right end, with the status only in tooltips.
            var section = rig.BoundsOf(page.VolumesSection);
            var dot = rig.BoundsOf(page.AdjustDot);
            var apply = rig.BoundsOf(page.ApplyNowButton);
            var master = rig.BoundsOf(page.AdjustSwitch);
            Assert.True(dot.Right <= apply.Left && apply.Right <= master.Left);
            Assert.Equal(section.Right - 24 - 1, master.Right, 1);
            Assert.Equal(CenterY(apply), CenterY(master), 1);
            Assert.Equal("Aplicar agora", AutomationProperties.GetName(page.ApplyNowButton));
            Assert.Equal("Ajustar o som ao iniciar", AutomationProperties.GetName(page.AdjustSwitch));
            Assert.StartsWith("Aplicar agora", (string)page.ApplyNowButton.ToolTip, StringComparison.Ordinal);
            Assert.StartsWith("Ajustar o som ao iniciar: ligado", (string)page.AdjustSwitch.ToolTip, StringComparison.Ordinal);
            Assert.StartsWith("Ajuste autom\U000000E1tico ligado", (string)page.AdjustDot.ToolTip, StringComparison.Ordinal);
            var dotFill = Descendants(page.AdjustDot).OfType<System.Windows.Shapes.Ellipse>().Single().Fill;
            Assert.Equal(((SolidColorBrush)Palette.Green).Color, ((SolidColorBrush)dotFill).Color);

            page.AdjustSwitch.IsChecked = false;
            rig.Layout(1100);
            Assert.StartsWith("Ajuste autom\U000000E1tico desligado", (string)page.AdjustDot.ToolTip, StringComparison.Ordinal);
            Assert.Equal(StatusLight.Gray, rig.Som.Som.AdjustLight);
        });
    }

    [Theory]
    [MemberData(nameof(WindowWidths))]
    public void The_outputs_are_three_aligned_rows_icon_name_list_dot_with_the_lock_and_the_pin_as_icon_toggles(double width)
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig(width);
            var page = rig.Page;
            var section = rig.BoundsOf(page.OutputsSection);
            var combos = new[] { page.DefaultOutputCombo, page.PlayerOutputCombo, page.HolyricsOutputCombo };
            var dots = new[] { page.DefaultOutputDot, page.PlayerOutputDot, page.HolyricsOutputDot };

            Assert.Equal(["Sa\U000000EDda padr\U000000E3o do Windows", "Sa\U000000EDda do player", "Sa\U000000EDda do Holyrics"], combos.Select(AutomationProperties.GetName));
            for (var i = 0; i < 3; i++)
            {
                var combo = rig.BoundsOf(combos[i]);
                Near(32, combo.Height);
                Assert.Equal(rig.BoundsOf(combos[0]).Left, combo.Left, 1);
                Assert.Equal(rig.BoundsOf(combos[0]).Right, combo.Right, 1);
                Assert.True(combo.Width >= 140, $"the list is wide enough ({combo.Width})");
                Assert.True(combo.Right <= rig.BoundsOf(dots[i]).Left);
                Assert.Equal(rig.BoundsOf(dots[0]).Left, rig.BoundsOf(dots[i]).Left, 1);
                Assert.InRange(Math.Abs(CenterY(combo) - CenterY(rig.BoundsOf(dots[i]))), 0, 1.01);
                if (i > 0)
                {
                    Assert.True(combo.Top >= rig.BoundsOf(combos[i - 1]).Bottom + 8, "rows are separated by spacing");
                }
            }

            // The lock (default output) and the pin (Holyrics) are 32 px icon toggles at the right edge, with tooltips and names.
            var lockBounds = rig.BoundsOf(page.LockSwitch);
            var pinBounds = rig.BoundsOf(page.HolyricsRouteSwitch);
            Near(32, lockBounds.Width);
            Assert.Equal(section.Right - 24 - 1, lockBounds.Right, 1);
            Assert.InRange(Math.Abs(lockBounds.Right - pinBounds.Right), 0, 1.01);
            Assert.True(lockBounds.Left >= rig.BoundsOf(dots[0]).Right);
            Assert.Equal("Travar a sa\U000000EDda padr\U000000E3o neste dispositivo", AutomationProperties.GetName(page.LockSwitch));
            Assert.Equal("Definir a sa\U000000EDda do Holyrics", AutomationProperties.GetName(page.HolyricsRouteSwitch));
            Assert.StartsWith("Travar a sa\U000000EDda padr\U000000E3o", (string)page.LockSwitch.ToolTip, StringComparison.Ordinal);
            Assert.StartsWith("Definir a sa\U000000EDda do Holyrics", (string)page.HolyricsRouteSwitch.ToolTip, StringComparison.Ordinal);
            Assert.True(page.LockSwitch.IsChecked);
            Assert.True(page.HolyricsRouteSwitch.IsChecked);

            // The long status sentences are in the tooltip of the dot, not on the card.
            Assert.DoesNotContain(rig.Find<TextBlock>().Where(Shown), t => t.Text.Contains("aguardando", StringComparison.Ordinal) || t.Text.StartsWith("Sa\U000000EDda atual", StringComparison.Ordinal));
            Assert.All(dots, dot => Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(dot))));
        });
    }

    [Fact]
    public void The_dot_beside_an_output_is_green_when_active_amber_while_the_device_is_missing_and_gray_when_off_and_the_sentence_is_its_tooltip()
    {
        WpfTestHost.Run(() =>
        {
            using (var ok = new Rig(1100))
            {
                Assert.Equal(StatusLight.Green, ok.Som.Som.DefaultOutput.Light);
                Assert.Equal(StatusLight.Green, ok.Som.Som.PlayerOutput.Light);
                Assert.Equal(StatusLight.Green, ok.Som.Som.HolyricsOutput.Light);
            }

            using var gone = new Rig(1100, unplugged: true);
            var page = gone.Page;
            var som = gone.Som.Som;

            Assert.Equal(StatusLight.Yellow, som.DefaultOutput.Light);
            Assert.Contains("desconectado, aguardando", som.DefaultOutput.Status, StringComparison.Ordinal);
            Assert.Equal(som.DefaultOutput.Status, page.DefaultOutputDot.ToolTip);
            var fill = ((SolidColorBrush)Descendants(page.DefaultOutputDot).OfType<System.Windows.Shapes.Ellipse>().Single().Fill).Color;
            Assert.Equal(((SolidColorBrush)Palette.Yellow).Color, fill);

            page.LockSwitch.IsChecked = false; // the rule is off: the dot goes gray at once
            gone.Layout(1100);
            Assert.Equal(StatusLight.Gray, som.DefaultOutput.Light);
            page.HolyricsRouteSwitch.IsChecked = false;
            gone.Layout(1100);
            Assert.Equal(StatusLight.Gray, som.HolyricsOutput.Light);
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
            Assert.Equal(246, page.DefaultOutputCombo.MaxDropDownHeight); // eight rows, then it scrolls
        });
    }

    // ---- nothing is cut ----

    [Theory]
    [MemberData(nameof(WindowWidths))]
    public void Nothing_leaves_the_page_or_its_card_with_long_names_and_three_alarms(double width)
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig(width, Mode.Playing, unplugged: true, alarms: 3, longNames: true);
            rig.Som.Som.AddAlarmCommand.Execute(null);
            rig.Layout(width);
            var scroll = (ScrollViewer)rig.Page.Content;

            Assert.Equal(0, scroll.ScrollableWidth, 1);
            var pageBounds = rig.BoundsOf(rig.Page);
            var cards = new FrameworkElement[] { rig.Page.PlayerHero, rig.Page.AlarmsSection, rig.Page.VolumesSection, rig.Page.OutputsSection };
            foreach (var control in rig.Find<Control>().Where(c => c is TextBox or ButtonBase or ComboBox or Slider or RadioButton && Shown(c)))
            {
                var bounds = rig.BoundsOf(control);
                var name = $"{control.GetType().Name} {AutomationProperties.GetName(control)} {bounds}";
                Assert.True(bounds.Left >= pageBounds.Left - 0.5 && bounds.Right <= pageBounds.Right + 0.5, name);
                var card = cards.First(c => IsInside(control, c));
                Assert.True(bounds.Left >= rig.BoundsOf(card).Left + 24 && bounds.Right <= rig.BoundsOf(card).Right - 24 + 0.5, $"{name} leaves its card");
            }

            // A text that is not allowed to wrap shows all of it or has an ellipsis; the rest wraps inside the page.
            foreach (var text in rig.Find<TextBlock>().Where(t => Shown(t) && t.Text.Length > 0))
            {
                Assert.True(rig.BoundsOf(text).Right <= pageBounds.Right + 0.5, text.Text);
                if (text.TextWrapping == TextWrapping.NoWrap && text.TextTrimming == TextTrimming.None)
                {
                    Assert.True(text.ActualWidth >= text.DesiredSize.Width - text.Margin.Left - text.Margin.Right - 0.5, text.Text);
                }
            }

            // The long playlist name is cut with an ellipsis inside the list box, never over the buttons next to it.
            Assert.True(rig.BoundsOf(rig.Page.PlaylistCombo).Right <= rig.BoundsOf(rig.Page.AddPlaylistButton).Left);
            Assert.True(rig.BoundsOf(rig.Page.PlaylistInfoText).Right <= rig.BoundsOf(rig.Page.ChangeFolderButton).Left);
        });
    }

    // ---- the design system pieces ----

    [Fact]
    public void The_adaptive_grid_drops_columns_when_narrow_and_keeps_the_order()
    {
        WpfTestHost.Run(() =>
        {
            var grid = new AdaptiveGrid { MinColumnWidth = 300, MaxColumns = 2, Gap = 16 };
            var items = Enumerable.Range(0, 3).Select(i => new Border { MinHeight = 40 + (i * 10) }).ToList();
            items.ForEach(i => grid.Children.Add(i));

            grid.Measure(new Size(700, 1000));
            grid.Arrange(new Rect(0, 0, 700, grid.DesiredSize.Height));
            Assert.Equal(2, grid.Columns);
            Assert.Equal(342, items[0].ActualWidth, 1); // (700 - 16) / 2
            Assert.Equal(358, items[1].RenderSize.Width + 16, 1);
            Assert.Equal(50, items[0].ActualHeight, 1); // a row is as tall as its tallest
            Assert.Equal(50 + 16, VisualTreeHelper.GetOffset(items[2]).Y, 1);

            grid.Measure(new Size(500, 1000));
            grid.Arrange(new Rect(0, 0, 500, grid.DesiredSize.Height));
            Assert.Equal(1, grid.Columns);
            Assert.Equal(500, items[0].ActualWidth, 1);
            Assert.Equal(40 + 16, VisualTreeHelper.GetOffset(items[1]).Y, 1);
        });
    }

    [Fact]
    public void A_folding_area_changes_state_at_once_when_it_is_not_on_screen_and_closed_content_leaves_the_tab_order()
    {
        WpfTestHost.Run(() =>
        {
            var content = new Border { Height = 60 };
            var host = new RevealHost { Child = content };
            host.Measure(new Size(300, 1000));
            Assert.Equal(0, host.DesiredSize.Height);

            host.IsOpen = true; // not shown: no animation, the final state at once
            host.Measure(new Size(300, 1000));
            Assert.Equal(1, host.Progress);
            Assert.Equal(60, host.DesiredSize.Height);
            Assert.Equal(1, host.Opacity);

            host.IsOpen = false;
            host.Measure(new Size(300, 1000));
            Assert.Equal(0, host.Progress);
            Assert.Equal(0, host.DesiredSize.Height);
            Assert.Equal(Visibility.Collapsed, content.Visibility);
        });
    }

    [Fact]
    public void The_expander_style_opens_through_the_folding_area()
    {
        WpfTestHost.Run(() =>
        {
            var expander = new Expander { Style = (Style)Application.Current.FindResource("IbtExpander"), Header = "x", Content = new Border { Height = 50 } };
            var window = new Window { Content = expander, Width = 300, Height = 400 };
            expander.ApplyTemplate();
            window.Measure(new Size(300, 400));
            window.Arrange(new Rect(0, 0, 300, 400));
            var host = Descendants(expander).OfType<RevealHost>().Single();
            Assert.False(host.IsOpen);
            Assert.Equal(0, host.DesiredSize.Height);
            expander.IsExpanded = true;
            Assert.True(host.IsOpen, "the body follows IsExpanded");
            host.Measure(new Size(300, 1000));
            Assert.True(host.DesiredSize.Height >= 50, host.DesiredSize.Height.ToString());
        });
    }

    [Fact]
    public void Motion_follows_the_windows_setting_and_never_animates_what_is_not_on_screen()
    {
        WpfTestHost.Run(() =>
        {
            var box = new Border();
            Motion.FadeIn(box); // not on screen: nothing happens, opacity stays
            Assert.Equal(1, box.Opacity);
            Assert.False(Motion.ShouldAnimate(box));
            Assert.Equal(SystemParameters.ClientAreaAnimation, Motion.Enabled);
            Assert.Equal(140, Motion.Fast.TimeSpan.TotalMilliseconds);
            Assert.Equal(180, Motion.Medium.TimeSpan.TotalMilliseconds);
        });
    }

    [Fact]
    public void The_three_pages_fade_in_when_their_tab_is_shown()
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig(1100);
            Assert.True(Motion.GetFadeOnShow(rig.Page));
            Assert.True(Motion.GetFadeOnShow(rig.Window.Home));
            Assert.True(Motion.GetFadeOnShow(rig.Window.Settings));
        });
    }

    [Theory]
    [MemberData(nameof(WindowWidths))]
    public void The_loudness_line_is_one_aligned_row_under_the_behaviour_line_with_the_chip_the_toggle_the_menu_and_the_tag_switch(double width)
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig(width, Mode.Playing, scanning: 237);
            var page = rig.Page;
            var strip = rig.BoundsOf(page.PlaylistStrip);
            var behaviour = rig.BoundsOf(page.BehaviorHalf);
            var row = rig.BoundsOf(page.LoudnessHalf);
            var chip = rig.BoundsOf(page.ScanChip);
            var toggle = rig.BoundsOf(page.NormalizeToggle);
            var combo = rig.BoundsOf(page.NormalizeCombo);
            var tags = rig.BoundsOf(page.WriteTagsRow);
            var tagSwitch = rig.BoundsOf(page.WriteTagsSwitch);

            Assert.True(row.Top >= behaviour.Bottom + 8, "its own line under the behaviour line, with a divider between");
            Near(32, toggle.Width);
            Near(32, toggle.Height);
            Near(32, combo.Height);
            Assert.True(combo.Width >= 100, $"the target fits ({combo.Width})");
            Assert.True(chip.Right <= toggle.Left, "chip, then toggle, then menu, then the tag switch");
            Assert.True(toggle.Right <= combo.Left);
            Assert.True(combo.Right <= tags.Left);
            foreach (var part in new[] { chip, toggle, combo, tagSwitch })
            {
                Assert.InRange(Math.Abs(CenterY(part) - CenterY(toggle)), 0, 1.5); // one line, aligned
                Assert.True(part.Left >= strip.Left + 16 && part.Right <= strip.Right - 16 + 1, $"inside the block, nothing cut: part {part.Left:F1}..{part.Right:F1} strip {strip.Left:F1}..{strip.Right:F1}");
            }

            Assert.InRange(Math.Abs(tags.Right - behaviour.Right), 0, 1.5); // the right edge lines up with the line above
            Assert.Equal("Igualar volume", AutomationProperties.GetName(page.NormalizeToggle));
            Assert.Equal("Gravar etiquetas nos arquivos", AutomationProperties.GetName(page.WriteTagsSwitch));
            Assert.Equal("medindo 0/237", page.ScanChipText.Text);
            Assert.Equal(rig.Player.NormalizeTip, (string)page.NormalizeToggle.ToolTip);
            Assert.Equal(rig.Player.NormalizeTip, (string)page.NormalizeCombo.ToolTip);
            Assert.Equal(rig.Player.WriteTagsTip, (string)page.WriteTagsSwitch.ToolTip);
            Assert.Equal(8, page.NormalizeCombo.Items.Count);
            Assert.Equal("-16", page.NormalizeCombo.SelectedValue);
            Assert.True(page.NormalizeToggle.IsChecked);
            Assert.True(page.WriteTagsSwitch.IsChecked);
        });
    }

    [Theory]
    [MemberData(nameof(WindowWidths))]
    public void The_chip_is_hidden_when_nothing_is_being_measured_and_the_row_still_fits(double width)
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig(width, Mode.Playing);
            var page = rig.Page;

            Assert.False(Shown(page.ScanChip));
            var strip = rig.BoundsOf(page.PlaylistStrip);
            var tags = rig.BoundsOf(page.WriteTagsRow);
            Assert.True(tags.Right <= strip.Right - 16 + 1, $"tags {tags.Right:F1} strip {strip.Right:F1}");
            Assert.InRange(Math.Abs(CenterY(rig.BoundsOf(page.NormalizeToggle)) - CenterY(rig.BoundsOf(page.WriteTagsSwitch))), 0, 1.5);
        });
    }

    [Fact]
    public void Choosing_Desligado_in_the_menu_turns_the_equalization_off_and_the_toggle_follows()
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig(1100, Mode.Playing);
            var page = rig.Page;

            page.NormalizeCombo.SelectedValue = "off";
            rig.Layout(1100);

            Assert.False(rig.Som.Current.Player.NormalizeEnabled);
            Assert.False(page.NormalizeToggle.IsChecked);

            page.NormalizeToggle.IsChecked = true;
            page.NormalizeCombo.SelectedValue = "-23";

            Assert.True(rig.Som.Current.Player.NormalizeEnabled);
            Assert.Equal(-23, rig.Som.Current.Player.NormalizeTargetLufs);
        });
    }

    [Fact]
    public void Render_the_tab_for_the_owner_to_look_at()
    {
        WpfTestHost.Run(() =>
        {
            foreach (var width in Widths)
            {
                using (var idle = new Rig(width))
                {
                    idle.Snapshot($"som-{width}-parado");
                }

                using (var playing = new Rig(width, Mode.Playing, unplugged: true))
                {
                    playing.Snapshot($"som-{width}-tocando");
                }

                using (var paused = new Rig(width, Mode.Paused))
                {
                    paused.Snapshot($"som-{width}-pausado");
                }

                using (var noArt = new Rig(width, Mode.Playing, art: false))
                {
                    noArt.Snapshot($"som-{width}-sem-capa");
                }

                using (var off = new Rig(width, Mode.Off))
                {
                    off.Snapshot($"som-{width}-desligado");
                }

                using (var none = new Rig(width, Mode.Playing, alarms: 0))
                {
                    none.Snapshot($"som-{width}-0-despertadores");
                }

                using (var three = new Rig(width, Mode.Playing, alarms: 3, alarmsOff: true))
                {
                    three.Snapshot($"som-{width}-3-despertadores");
                }

                using (var mix = new Rig(width, Mode.Playing, crossfade: 8, curve: CrossfadeCurve.Intermediate))
                {
                    mix.Snapshot($"som-{width}-mescla-intermediaria");
                }

                using (var measuring = new Rig(width, Mode.Playing, scanning: 237))
                {
                    measuring.Snapshot($"som-{width}-medindo");
                }

                using (var mixTags = new Rig(width, Mode.Playing, crossfade: 8, scanning: 120, tagsOff: true))
                {
                    mixTags.Snapshot($"som-{width}-medindo-mescla-sem-etiquetas");
                }

                using (var longNames = new Rig(width, Mode.Playing, unplugged: true, alarms: 3, longNames: true))
                {
                    longNames.Snapshot($"som-{width}-nomes-longos");
                }
            }

            var policy = AlertaIBT.App.Tests.Platform.PolicyFakes.Policy(null, [AlertaIBT.App.Tests.Platform.PolicyFakes.Primary()], (0, 0));
            SnapshotDialog(ConfirmDialog.Create("Excluir a lista \U0000201CIgreja\U0000201D?", "S\U000000F3 a lista sai do app. As m\U000000FAsicas continuam na pasta: nada \U000000E9 apagado do PC.", policy), "dialogo-excluir");
            SnapshotDialog(InputDialog.Create("Renomear a lista", "Louvor", text => text.Trim() == "Igreja" ? "J\U000000E1 existe uma lista com esse nome." : null, policy), "dialogo-renomear");
        });
    }

    // The small windows are never shown in the tests: their content is drawn on its own, at the size the window would have.
    private static void SnapshotDialog(Window dialog, string name)
    {
        var dir = Environment.GetEnvironmentVariable("ALERTAIBT_RENDER_DIR");
        if (string.IsNullOrEmpty(dir))
        {
            return;
        }

        Directory.CreateDirectory(dir);
        var content = (FrameworkElement)dialog.Content;
        content.Measure(new Size(dialog.Width, dialog.Height));
        content.Arrange(new Rect(0, 0, dialog.Width, dialog.Height));
        content.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)dialog.Width, (int)dialog.Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(System.IO.Path.Combine(dir, name + ".png"));
        encoder.Save(stream);
    }
}
