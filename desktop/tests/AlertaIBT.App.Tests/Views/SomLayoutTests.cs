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
using AlertaIBT.App.ViewModels;
using AlertaIBT.App.Views;
using AlertaIBT.Core.Audio;
using AlertaIBT.Core.Diagnostics;
using AlertaIBT.Core.Player;
using AlertaIBT.Core.Settings;

namespace AlertaIBT.App.Tests.Views;

// The real Som page laid out at three window widths (the window is 900 wide at the least). Set ALERTAIBT_RENDER_DIR to also get PNG pictures of
// the whole page (every width and every state of the player) to look at.
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

        public Rig(double windowWidth, Mode mode = Mode.Idle, bool unplugged = false, bool alertsOff = false, bool art = true)
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
                    Enabled = mode != Mode.Off,
                    StopFadeSeconds = mode == Mode.Paused ? 0 : 5,
                },
                System = new SystemSettings { AlertsEnabled = !alertsOff },
            };
            _som = new SomRig(initial, new FakeCover(art), new NameMetadata());
            if (!unplugged)
            {
                _som.Audio.Devices.Add(new AudioDevice("id-2", "Alto-falantes (WING-USB Out 1-2)"));
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

    // The window is 900 wide at the least; there the cards are one column, from about 1000 on they are two.
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

    private static double CenterX(Rect rect) => rect.Left + (rect.Width / 2);

    // ---- the structure ----

    [Theory]
    [MemberData(nameof(WindowWidths))]
    public void The_player_is_on_top_and_the_five_cards_follow_in_two_columns_with_their_rows(double width)
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig(width);
            var page = rig.Page;

            Assert.Equal(
                ["Volumes ao iniciar", "Sa\U000000EDdas de \U000000E1udio", "Playlists", "Despertadores", "Comportamento do player"],
                new[] { page.VolumesSection, page.OutputsSection, page.PlaylistsSection, page.AlarmsSection, page.BehaviorSection }.Select(s => (string)s.Header));
            Assert.All(
                new[] { page.VolumesSection, page.OutputsSection, page.PlaylistsSection, page.AlarmsSection, page.BehaviorSection },
                section => Assert.False(string.IsNullOrWhiteSpace(section.Subtitle), (string)section.Header));
            Assert.Equal(
                [
                    "Ajustar o som ao iniciar", "Volume geral do Windows", "Holyrics", "Player", "Sons do Windows (notifica\U000000E7\U000000F5es e afins)", "Aplicar agora",
                    "Sa\U000000EDda padr\U000000E3o do Windows", "Travar a sa\U000000EDda padr\U000000E3o neste dispositivo", "Player", "Holyrics", "Definir a sa\U000000EDda do Holyrics",
                    "Lista de m\U000000FAsicas", "Pasta da lista",
                    "Despertadores ligados",
                    "Ao parar", "Ao come\U000000E7ar pelo despertador", "Teclas de m\U000000EDdia",
                ],
                rig.Find<SettingRow>().Select(r => r.Title));

            // The player card is above everything else and spans the page.
            var hero = rig.BoundsOf(page.PlayerHero);
            Assert.True(hero.Bottom <= rig.BoundsOf(page.VolumesSection).Top);
            Assert.True(hero.Bottom <= rig.BoundsOf(page.PlaylistsSection).Top);
            Assert.Equal(rig.BoundsOf(page.VolumesSection).Left, hero.Left, 1);
            Assert.Equal(rig.BoundsOf(page.PlaylistsSection).Right, hero.Right, 1);

            // Two columns: Volumes above Sa\U000000EDdas on the left; Playlists, Despertadores, Comportamento on the right, in that order.
            // One column (narrow): the same five cards, one under the other, in the same reading order.
            Assert.Equal(TwoColumns(width), !page.IsSingleColumn);
            Assert.True(rig.BoundsOf(page.OutputsSection).Top > rig.BoundsOf(page.VolumesSection).Bottom);
            Assert.True(rig.BoundsOf(page.AlarmsSection).Top > rig.BoundsOf(page.PlaylistsSection).Bottom);
            Assert.True(rig.BoundsOf(page.BehaviorSection).Top > rig.BoundsOf(page.AlarmsSection).Bottom);
            Assert.Equal(rig.BoundsOf(page.PlaylistsSection).Left, rig.BoundsOf(page.BehaviorSection).Left, 1);
            if (TwoColumns(width))
            {
                Assert.True(rig.BoundsOf(page.PlaylistsSection).Left > rig.BoundsOf(page.VolumesSection).Right);
                Assert.True(rig.BoundsOf(page.PlaylistsSection).Top < rig.BoundsOf(page.OutputsSection).Bottom);
            }
            else
            {
                Assert.True(rig.BoundsOf(page.PlaylistsSection).Top >= rig.BoundsOf(page.OutputsSection).Bottom);
                Assert.Equal(rig.BoundsOf(page.VolumesSection).Left, rig.BoundsOf(page.PlaylistsSection).Left, 1);
                Assert.Equal(rig.BoundsOf(page.VolumesSection).Width, rig.BoundsOf(page.PlaylistsSection).Width, 1);
            }
            Assert.DoesNotContain(rig.Find<SettingRow>(), r => r.Title.Contains("Spotify", StringComparison.OrdinalIgnoreCase));
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
            Assert.Equal(hero.Left + 20 + 1, art.Left, 1); // card padding and border
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
            var order = new FrameworkElement[] { page.ShuffleToggle, page.PreviousButton, page.PlayPauseButton, page.NextButton, page.RepeatButton };
            for (var i = 1; i < order.Length; i++)
            {
                Assert.True(rig.BoundsOf(order[i]).Left >= rig.BoundsOf(order[i - 1]).Right);
            }

            // The corner: the switch and the next alarm are at the top right of the card, the volume at the right of the transport row.
            var corner = rig.BoundsOf(page.HeroCorner);
            Assert.Equal(rig.BoundsOf(page.HeroInfo).Right, corner.Right, 1);
            Assert.True(corner.Top <= title.Top + 4);
            Assert.True(rig.BoundsOf(page.PlayerOnSwitch).Right <= hero.Right);
            var volume = rig.BoundsOf(page.PlayerVolumeBox);
            Assert.True(volume.Left >= transport.Right);
            Assert.Equal(rig.BoundsOf(page.HeroInfo).Right, volume.Right, 1);
            Assert.True(rig.BoundsOf(page.StopButton).Right <= transport.Left);

            // Nothing is cut: the title may have an ellipsis, but no control of the card leaves it.
            foreach (var control in rig.Find<Control>().Where(c => Shown(c) && IsInside(c, page.PlayerHero)))
            {
                var bounds = rig.BoundsOf(control);
                Assert.True(bounds.Left >= hero.Left - 0.5 && bounds.Right <= hero.Right + 0.5, $"{control.GetType().Name} {bounds}");
            }
        });
    }

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
            Assert.StartsWith("Pr\U000000F3ximo despertador:", page.NextAlarmText.Text, StringComparison.Ordinal);
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
            Assert.False(page.PlayerVolumeSlider.IsEnabled);
            Assert.InRange(page.ArtHost.Opacity, 0.2, 0.6);
            Assert.Equal(Visibility.Visible, page.CoverPlaceholder.Visibility);

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

    // ---- the settings grid ----

    [Theory]
    [MemberData(nameof(WindowWidths))]
    public void Every_row_is_inside_its_card_and_nothing_leaves_the_page_or_needs_a_horizontal_scroll(double width)
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig(width, Mode.Playing);
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

            foreach (var control in rig.Find<Control>().Where(c => c is TextBox or Button or ComboBox or ToggleButton or Slider or RadioButton && Shown(c)))
            {
                var bounds = rig.BoundsOf(control);
                Assert.True(bounds.Left >= pageBounds.Left - 0.5 && bounds.Right <= pageBounds.Right + 0.5, $"{control.GetType().Name} {AutomationProperties.GetName(control)} {bounds}");
                var section = AncestorOf<SettingsSection>(control);
                if (section is null)
                {
                    Assert.True(IsInside(control, rig.Page.PlayerHero), $"{AutomationProperties.GetName(control)} is neither in a card nor in the player");
                    continue;
                }

                Assert.True(bounds.Right <= rig.BoundsOf(section).Right - 8, $"{AutomationProperties.GetName(control)} {bounds} leaves its card");
            }

            // The text of a title or a status line is never cut off by the card: each one is as wide as it needs or wraps.
            foreach (var text in rig.Find<TextBlock>().Where(t => Shown(t) && t.Text.Length > 0 && t.TextWrapping == TextWrapping.Wrap))
            {
                Assert.True(text.ActualHeight > 0, text.Text);
                Assert.True(rig.BoundsOf(text).Right <= pageBounds.Right + 0.5, text.Text);
            }

            // A title that is not allowed to wrap shows all its text or has an ellipsis; the ones that show everything are as wide as the text.
            foreach (var text in rig.Find<TextBlock>().Where(t => Shown(t) && t.Text.Length > 0 && t.TextWrapping == TextWrapping.NoWrap && t.TextTrimming == TextTrimming.None))
            {
                Assert.True(text.ActualWidth >= text.DesiredSize.Width - text.Margin.Left - text.Margin.Right - 0.5, text.Text);
            }
        });
    }

    [Theory]
    [MemberData(nameof(WindowWidths))]
    public void The_cards_of_a_column_are_as_wide_as_each_other_and_the_controls_share_a_right_edge(double width)
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig(width);
            var page = rig.Page;

            Assert.Equal(rig.BoundsOf(page.VolumesSection).Width, rig.BoundsOf(page.OutputsSection).Width, 1);
            Assert.Equal(rig.BoundsOf(page.VolumesSection).Width, rig.BoundsOf(page.PlaylistsSection).Width, 1);
            Assert.Equal(rig.BoundsOf(page.PlaylistsSection).Width, rig.BoundsOf(page.AlarmsSection).Width, 1);
            Assert.Equal(rig.BoundsOf(page.PlaylistsSection).Width, rig.BoundsOf(page.BehaviorSection).Width, 1);

            // Rows touch each other inside a card (one template, one line).
            foreach (var section in new[] { page.VolumesSection, page.OutputsSection, page.BehaviorSection })
            {
                var rows = rig.Find<SettingRow>().Where(r => ReferenceEquals(AncestorOf<SettingsSection>(r), section)).ToList();
                for (var i = 1; i < rows.Count; i++)
                {
                    Assert.Equal(rig.BoundsOf(rows[i - 1]).Bottom, rig.BoundsOf(rows[i]).Top, 1);
                }
            }

            // The switches and the list boxes of one column end at the same edge, and every list box is as wide as the others.
            Assert.Equal(rig.BoundsOf(page.AdjustSwitch).Right, rig.BoundsOf(page.LockSwitch).Right, 1);
            Assert.Equal(rig.BoundsOf(page.AdjustSwitch).Right, rig.BoundsOf(page.HolyricsRouteSwitch).Right, 1);
            Assert.Equal(rig.BoundsOf(page.AlarmsOnSwitch).Right, rig.BoundsOf(page.MediaKeysSwitch).Right, 1);
            Assert.Equal(rig.BoundsOf(page.DefaultOutputCombo).Right, rig.BoundsOf(page.AdjustSwitch).Right, 1);
            Assert.Equal(rig.BoundsOf(page.PlaylistCombo).Right, rig.BoundsOf(page.MediaKeysSwitch).Right, 1);
            Assert.Equal(rig.BoundsOf(page.DefaultOutputCombo).Width, rig.BoundsOf(page.HolyricsOutputCombo).Width, 1);
            Assert.Equal(rig.BoundsOf(page.DefaultOutputCombo).Width, rig.BoundsOf(page.PlaylistCombo).Width, 1);
            Assert.Equal(rig.BoundsOf(page.StopFadeList).Right, rig.BoundsOf(page.AlarmFadeList).Right, 1);
            Assert.Equal(rig.BoundsOf(page.StopFadeList).Right, rig.BoundsOf(page.MediaKeysSwitch).Right, 1.5); // the list sits in a 1 px frame

            // Card padding is the same everywhere: the first row is the same distance in from the card on every card, left and right.
            var insets = new List<(double Left, double Right)>();
            foreach (var section in new[] { page.VolumesSection, page.OutputsSection, page.PlaylistsSection, page.AlarmsSection, page.BehaviorSection })
            {
                var first = rig.Find<SettingRow>().First(r => ReferenceEquals(AncestorOf<SettingsSection>(r), section));
                insets.Add((rig.BoundsOf(first).Left - rig.BoundsOf(section).Left, rig.BoundsOf(section).Right - rig.BoundsOf(first).Right));
            }

            Assert.All(insets, inset =>
            {
                Assert.Equal(insets[0].Left, inset.Left, 0.6);
                Assert.Equal(insets[0].Right, inset.Right, 0.6);
                Assert.InRange(inset.Left, 12, 14);
                Assert.InRange(inset.Right, 12, 14);
            });
        });
    }

    [Theory]
    [MemberData(nameof(WindowWidths))]
    public void The_alarm_rows_line_up_with_each_other_and_wrap_cleanly(double width)
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig(width);
            var page = rig.Page;
            var rows = page.AlarmList.Items.Cast<object>().Select(item => (FrameworkElement)page.AlarmList.ItemContainerGenerator.ContainerFromItem(item)).ToList();
            Assert.Equal(2, rows.Count);

            var fields = rows.Select(row => Descendants(row).OfType<FrameworkElement>().ToList()).ToList();
            ToggleButton Switch(int i) => fields[i].OfType<ToggleButton>().First(t => AutomationProperties.GetName(t) == "Despertador ligado");
            TextBox Start(int i) => fields[i].OfType<TextBox>().First(t => AutomationProperties.GetName(t) == "Hora de in\U000000EDcio");
            TextBox Latest(int i) => fields[i].OfType<TextBox>().First(t => AutomationProperties.GetName(t) == "N\U000000E3o iniciar depois de");
            Button Trash(int i) => fields[i].OfType<Button>().First(t => AutomationProperties.GetName(t) == "Remover despertador");
            ComboBox Playlist(int i) => fields[i].OfType<ComboBox>().First();
            List<ToggleButton> Chips(int i) => fields[i].OfType<ToggleButton>().Where(t => t.Content is string { Length: 1 }).ToList();

            for (var i = 0; i < 2; i++)
            {
                var bounds = rig.BoundsOf(rows[i]);
                // Line one: switch, start, the limit and the trash are on one line, in that order, the trash at the right edge.
                Assert.Equal(CenterY(rig.BoundsOf(Start(i))), CenterY(rig.BoundsOf(Latest(i))), 1);
                Assert.Equal(rig.BoundsOf(Start(i)).Bottom, rig.BoundsOf(Switch(i)).Bottom + 5, 1); // the switch sits on the line of the boxes
                Assert.True(rig.BoundsOf(Switch(i)).Right < rig.BoundsOf(Start(i)).Left);
                Assert.True(rig.BoundsOf(Start(i)).Right < rig.BoundsOf(Latest(i)).Left);
                Assert.True(rig.BoundsOf(Latest(i)).Right < rig.BoundsOf(Trash(i)).Left);
                Assert.Equal(bounds.Right, rig.BoundsOf(Trash(i)).Right, 1);

                // Line two: the weekdays are below the first line, in a row, then the playlist (beside them or, when narrow, under them).
                var chips = Chips(i);
                Assert.Equal(7, chips.Count);
                Assert.True(rig.BoundsOf(chips[0]).Top > rig.BoundsOf(Start(i)).Bottom);
                for (var c = 1; c < 7; c++)
                {
                    Assert.Equal(rig.BoundsOf(chips[0]).Top, rig.BoundsOf(chips[c]).Top, 1);
                    Assert.True(rig.BoundsOf(chips[c]).Left > rig.BoundsOf(chips[c - 1]).Left);
                }

                var combo = rig.BoundsOf(Playlist(i));
                var beside = combo.Left >= rig.BoundsOf(chips[6]).Right;
                Assert.True(beside || combo.Top >= rig.BoundsOf(chips[0]).Bottom, "the playlist is beside the weekdays or under them, never on top of them");
                Assert.True(combo.Right <= bounds.Right + 0.5);
                Assert.True(beside, "with room, the playlist sits beside the weekdays");
            }

            // The two alarms use the very same columns.
            Assert.Equal(rig.BoundsOf(Start(0)).Left, rig.BoundsOf(Start(1)).Left, 1);
            Assert.Equal(rig.BoundsOf(Latest(0)).Left, rig.BoundsOf(Latest(1)).Left, 1);
            Assert.Equal(rig.BoundsOf(Chips(0)[0]).Left, rig.BoundsOf(Chips(1)[0]).Left, 1);
            Assert.Equal(rig.BoundsOf(Playlist(0)).Left, rig.BoundsOf(Playlist(1)).Left, 1);
        });
    }

    [Theory]
    [InlineData(300)]
    [InlineData(340)]
    public void An_alarm_row_in_a_narrow_card_wraps_the_playlist_under_the_weekdays_and_stays_inside(double rowWidth)
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig(900);
            var template = (DataTemplate)rig.Page.Resources["AlarmTemplate"];
            var presenter = new ContentPresenter { Content = rig.Som.Som.Alarms[0], ContentTemplate = template, Width = rowWidth };
            presenter.Measure(new Size(rowWidth, 1000));
            presenter.Arrange(new Rect(0, 0, rowWidth, presenter.DesiredSize.Height));
            presenter.UpdateLayout();

            var all = Descendants(presenter).OfType<FrameworkElement>().ToList();
            var chips = all.OfType<ToggleButton>().Where(t => t.Content is string { Length: 1 }).ToList();
            var combo = all.OfType<ComboBox>().Single();
            Rect B(FrameworkElement e) => e.TransformToAncestor(presenter).TransformBounds(new Rect(e.RenderSize));

            Assert.Equal(7, chips.Count);
            Assert.True(B(combo).Top >= B(chips[0]).Bottom, "the playlist is under the weekdays");
            Assert.Equal(B(chips[0]).Left, B(combo).Left, 1); // and starts at the same left edge
            foreach (var element in all.Where(e => e is TextBox or Button or ComboBox or ToggleButton && Shown(e)))
            {
                Assert.True(B(element).Right <= rowWidth + 0.5 && B(element).Left >= -0.5, $"{AutomationProperties.GetName(element)} {B(element)} is outside the row");
            }

            var start = all.OfType<TextBox>().First(t => AutomationProperties.GetName(t) == "Hora de in\U000000EDcio");
            var latest = all.OfType<TextBox>().First(t => AutomationProperties.GetName(t) == "N\U000000E3o iniciar depois de");
            var trash = all.OfType<Button>().First(t => AutomationProperties.GetName(t) == "Remover despertador");
            Assert.Equal(CenterY(B(start)), CenterY(B(latest)), 1);
            Assert.True(B(latest).Right < B(trash).Left, "the first line still holds the switch, both times and the trash");
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
    public void The_alarms_show_their_time_weekdays_limit_and_playlist()
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig(1100);
            var page = rig.Page;

            Assert.Equal(2, rig.Som.Som.Alarms.Count);
            Assert.Equal(2, page.AlarmList.Items.Count);
            Assert.StartsWith("Pr\U000000F3ximo despertador:", page.AlarmsNextText.Text, StringComparison.Ordinal);
            var chips = rig.Find<ToggleButton>().Where(t => t.Content is string { Length: 1 } letter && "DSTQ".Contains(letter, StringComparison.Ordinal)).ToList();
            Assert.Equal(14, chips.Count);
            Assert.Equal(["D", "S", "T", "Q", "Q", "S", "S"], chips.Take(7).Select(c => (string)c.Content));
            Assert.Equal([true, false, false, false, false, false, false], chips.Take(7).Select(c => c.IsChecked == true));
            Assert.Equal([false, false, false, true, false, false, false], chips.Skip(7).Select(c => c.IsChecked == true));
        });
    }

    [Fact]
    public void The_playlists_card_has_the_selector_the_folder_and_the_four_actions()
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig(1100, Mode.Playing);
            var page = rig.Page;

            Assert.Equal(["Igreja", "Louvor"], ((IEnumerable<PlaylistEntry>)page.PlaylistCombo.ItemsSource).Select(p => p.Name));
            Assert.Contains(@"C:\Users\gutni\Music\Mornye\Igreja", page.PlaylistInfoText.Text, StringComparison.Ordinal);
            Assert.Equal(
                ["Adicionar lista", "Renomear lista", "Excluir lista"],
                new[] { page.AddPlaylistButton, page.RenamePlaylistButton, page.RemovePlaylistButton }.Select(AutomationProperties.GetName));
            Assert.Equal(["Trocar a pasta", "Atualizar"], new[] { page.ChangeFolderButton, page.RefreshButton }.Select(AutomationProperties.GetName));
            var actions = new[] { page.AddPlaylistButton, page.RenamePlaylistButton, page.RemovePlaylistButton }.Select(rig.BoundsOf).ToList();
            Assert.All(actions, bounds => Assert.Equal(actions[0].Top, bounds.Top, 1)); // one line, no orphan
            var folder = new[] { page.ChangeFolderButton, page.RefreshButton }.Select(rig.BoundsOf).ToList();
            Assert.Equal(folder[0].Top, folder[1].Top, 1);
            Assert.Equal(rig.BoundsOf(page.PlaylistCombo).Right, folder[1].Right, 1);
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

                using var off = new Rig(width, Mode.Off);
                off.Snapshot($"som-{width}-desligado");
            }
        });
    }
}
