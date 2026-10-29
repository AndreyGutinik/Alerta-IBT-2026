using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AlertaIBT.App.Tests.ViewModels;
using AlertaIBT.App.Theme;
using AlertaIBT.App.ViewModels;
using AlertaIBT.App.Views;
using AlertaIBT.Core.Diagnostics;
using AlertaIBT.Core.Ndi;
using AlertaIBT.Core.Screens;
using AlertaIBT.Core.Settings;

namespace AlertaIBT.App.Tests.Views;

// The real NDI page laid out at three window widths (the window is 900 wide at the least), with fake test-pattern pictures in the thumbnails.
// Set ALERTAIBT_RENDER_DIR to also get PNG pictures of the page in every state to look at.
public sealed class NdiLayoutTests
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
        private readonly NdiRig _ndi;
        private readonly SettingsRig _chrome;

        public Rig(double windowWidth, int cameras = 3, bool runtime = true, Action<NdiRig>? setup = null, bool pictures = true)
        {
            _ndi = new NdiRig(cameras, runtime);
            setup?.Invoke(_ndi);
            _chrome = new SettingsRig();
            var gateway = new FakeGateway(_chrome.Clock);
            var composer = new QuickPanelViewModel(gateway, _chrome.Hub, clock: _chrome.Clock);
            var home = new HomeViewModel(gateway, composer, clock: _chrome.Clock);
            var correction = new CorrectionViewModel(_chrome.Settings, _chrome.Protector, _chrome.Hub, _chrome.Saver);
            var diagnostics = new DiagnosticsViewModel(new LogFiles(System.IO.Path.Combine(_chrome.Folder, "logs")), _ => { }, () => null);
            var updates = new FakeUpdateStatus();
            var page = new SettingsViewModel(_chrome.Settings, _chrome.Protector, _chrome.Hub, _chrome.Saver, diagnostics, () => Task.CompletedTask, updates: updates);
            ViewModel = new MainViewModel(_chrome.Hub, home, correction, page, _chrome.Saver, updates: updates, ndi: _ndi.Vm);
            Window = new MainWindow(ViewModel, null);
            Root = (FrameworkElement)Window.Content;
            ViewModel.SelectTabCommand.Execute(MainTab.Ndi);
            Layout(windowWidth);
            if (pictures)
            {
                Refresh();
            }
        }

        public NdiRig Engine => _ndi;

        public MainViewModel ViewModel { get; }

        public MainWindow Window { get; }

        public FrameworkElement Root { get; }

        public NdiPage Page => Window.Ndi;

        public NdiViewModel Vm => _ndi.Vm;

        public void Layout(double width)
        {
            var size = new Size(width, 3000);
            Root.InvalidateMeasure();
            Root.Measure(size);
            Root.Arrange(new Rect(new Point(0, 0), size));
            Root.UpdateLayout();
        }

        // The pictures take the newest thumbnails (the timer does this about 20 times a second in the app).
        public void Refresh()
        {
            Page.PreviewPicture.Poll();
            Page.OnAirPicture.Poll();
            Layout(Root.ActualWidth);
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
            _ndi.Service.Dispose();
            _ndi.Settings.Dispose();
            _chrome.Dispose();
        }
    }

    // Three cameras: Cam1 on air with the telao on, Cam2 in the preview.
    private static void ThreeCameras(NdiRig engine)
    {
        engine.GoOnAir(1, hue: 0);
        engine.Vm.LayerOn = true;
        engine.LaneOf(1).Push(0);
        engine.Preview(2, hue: 3);
        engine.Vm.Preview.Position = 25;
        engine.Vm.OnAir.Position = 70;
    }

    [Theory]
    [MemberData(nameof(WindowWidths))]
    public void The_two_pictures_are_side_by_side_with_Cortar_centered_between_them(double width)
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig(width, setup: ThreeCameras);

            var preview = rig.BoundsOf(rig.Named<ContentControl>("PreviewLane"));
            var onAir = rig.BoundsOf(rig.Named<ContentControl>("OnAirLane"));
            var take = rig.BoundsOf(rig.Named<Button>("TakeButton"));
            var previewPicture = rig.BoundsOf(rig.Page.PreviewPicture);
            var onAirPicture = rig.BoundsOf(rig.Page.OnAirPicture);

            Assert.True(preview.Right <= take.Left, "Previa is left of Cortar");
            Assert.True(take.Right <= onAir.Left, "Cortar is left of No ar");
            Assert.Equal(previewPicture.Width, onAirPicture.Width, 1);
            Assert.Equal(previewPicture.Top, onAirPicture.Top, 1);

            // Cortar is centered in the gap between the pictures, horizontally and vertically
            Assert.Equal((previewPicture.Right + onAirPicture.Left) / 2, take.Left + (take.Width / 2), 1);
            Assert.InRange(Math.Abs((previewPicture.Top + (previewPicture.Height / 2)) - (take.Top + (take.Height / 2))), 0, 1);

            // 16:9, large enough to read
            Assert.Equal(16.0 / 9, previewPicture.Width / previewPicture.Height, 2);
            Assert.True(previewPicture.Width >= 240, $"the picture is {previewPicture.Width} wide");
            if (width >= 1600)
            {
                Assert.True(previewPicture.Width >= 500);
            }

            // the word and the key
            Assert.Equal("Cortar", AutomationProperties.GetName(rig.Named<Button>("TakeButton")));
            Assert.Contains("Ctrl+Espa\U000000E7o", (string)rig.Named<Button>("TakeButton").ToolTip);
            Assert.Contains(rig.Named<Button>("TakeButton").Descendants<TextBlock>(), text => text.Text == "Cortar");
        });
    }

    [Fact]
    public void Previa_is_on_the_left_and_No_ar_on_the_right_with_the_tally_on_the_program_one()
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig(1100, setup: ThreeCameras);

            var lanes = rig.Find<TextBlock>().Where(t => t.Text is "PR\U000000C9VIA").ToList();
            Assert.Single(lanes);
            var preview = rig.BoundsOf(lanes[0]);
            Assert.True(preview.Left < rig.Root.ActualWidth / 2);

            var onAirLane = rig.Vm.OnAir;
            Assert.True(onAirLane.ShowTally);
            Assert.True(onAirLane.TallyRed);
            Assert.Contains("NO AR", onAirLane.TallyTip);
            Assert.Equal("No ar", onAirLane.Title);
            Assert.False(rig.Vm.Preview.ShowTally);
        });
    }

    [Fact]
    public void Top_to_bottom_the_order_is_pictures_then_cameras_then_telao_and_settings_then_the_trademark()
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig(1100, setup: ThreeCameras);

            var stage = rig.BoundsOf(rig.Named<Border>("Stage"));
            var cameras = rig.BoundsOf(rig.Named<CardSection>("CamerasCard"));
            var bottom = rig.BoundsOf(rig.Named<AdaptiveGrid>("BottomRow"));
            var trademark = rig.BoundsOf(rig.Named<TextBlock>("TrademarkText"));

            Assert.True(stage.Bottom <= cameras.Top);
            Assert.True(cameras.Bottom <= bottom.Top);
            Assert.True(bottom.Bottom <= trademark.Top);
            Assert.Equal("NDI(R) is a registered trademark of Vizrt NDI AB".Replace("(R)", "\U000000AE"), rig.Named<TextBlock>("TrademarkText").Text);
            Assert.Equal("ndi.video", rig.Named<Button>("SiteLink").Content);
        });
    }

    [Fact]
    public void The_menu_order_is_Inicio_Som_NDI_Configuracoes_and_the_page_fades_in()
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig(1100);

            var tops = new[] { "HomeTab", "SomTab", "NdiTab", "SettingsTab" }
                .Select(name => rig.BoundsOf(Descendants(rig.Root).OfType<Button>().First(b => b.Name == name)).Top)
                .ToList();
            Assert.Equal(tops.OrderBy(t => t), tops);
            Assert.Equal(4, tops.Distinct().Count());
            Assert.Equal("NDI", AutomationProperties.GetName(Descendants(rig.Root).OfType<Button>().First(b => b.Name == "NdiTab")));
            Assert.True(Motion.GetFadeOnShow(rig.Window.Ndi));
            Assert.Equal(Visibility.Visible, rig.Window.Ndi.Visibility);
            Assert.Equal(Visibility.Collapsed, rig.Window.Som.Visibility);
        });
    }

    [Fact]
    public void Cards_are_lighter_than_the_page_with_a_thin_border()
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig(1100, setup: ThreeCameras);
            var page = Luminance((SolidColorBrush)Application.Current.FindResource("IbtBackgroundBrush"));

            var stage = rig.Named<Border>("Stage");
            Assert.True(Luminance((SolidColorBrush)stage.Background) > page);
            Assert.Equal(new Thickness(1), stage.BorderThickness);
            Assert.NotNull(stage.BorderBrush);
            Assert.Equal(12, stage.CornerRadius.TopLeft);

            foreach (var card in rig.Find<CardSection>())
            {
                var surface = Descendants(card).OfType<Border>().First();
                Assert.True(Luminance((SolidColorBrush)surface.Background) > page, card.Header?.ToString());
                Assert.Equal(new Thickness(1), surface.BorderThickness);
            }

            // the blocks in the cards are lighter again
            var block = Descendants(rig.Named<CardSection>("TelaoCard")).OfType<Border>().First(b => Equals(b.Style, Application.Current.FindResource("IbtBlock")));
            Assert.True(Luminance((SolidColorBrush)block.Background) > Luminance((SolidColorBrush)stage.Background));
        });
    }

    [Fact]
    public void Every_icon_button_and_toggle_has_a_tooltip_and_an_automation_name()
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig(1100, cameras: 6, setup: ThreeCameras);
            var controls = rig.Find<ButtonBase>().Where(c => c is not RepeatButton && !InsideCombo(c)).ToList();

            Assert.True(controls.Count >= 20, $"{controls.Count} buttons");
            foreach (var control in controls)
            {
                Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(control)), $"{control.Name}: automation name");
                Assert.NotNull(control.ToolTip);
            }

            // the icon buttons of the tab, by name
            foreach (var name in new[] { "IpsButton", "LayerSwitch", "TestPatternSwitch", "MonitorLock", "HideSwitch", "ShortcutsSwitch" })
            {
                var control = rig.Named<ButtonBase>(name);
                Assert.False(string.IsNullOrWhiteSpace((control.ToolTip as string) ?? string.Empty), name);
            }
        });
    }

    [Theory]
    [MemberData(nameof(WindowWidths))]
    public void Nothing_is_cut_or_pushed_out_of_the_window_in_any_state(double width)
    {
        WpfTestHost.Run(() =>
        {
            var long32 = string.Join(" ", Enumerable.Repeat("Lateral", 5))[..32];
            var scenarios = new (string Name, Func<Rig> Make)[]
            {
                ("empty", () => new Rig(width, cameras: 0)),
                ("three", () => new Rig(width, setup: ThreeCameras)),
                ("six", () => new Rig(width, cameras: 6, setup: ThreeCameras)),
                ("runtime", () => new Rig(width, runtime: false)),
                ("long", () => new Rig(width, cameras: 6, setup: e =>
                {
                    NdiRig.Names = Enumerable.Repeat(long32, 6).ToArray();
                })),
            };

            foreach (var (name, make) in scenarios)
            {
                if (name == "long")
                {
                    NdiRig.Names = Enumerable.Repeat(long32, 6).ToArray();
                }

                using var rig = make();
                NdiRig.Names = [];
                var scroll = (ScrollViewer)rig.Page.Content;
                Assert.True(scroll.ExtentWidth <= scroll.ViewportWidth + 0.5, $"{name}: horizontal overflow at {width}");

                var cell = rig.BoundsOf(rig.Page);
                foreach (var element in rig.Find<FrameworkElement>().Where(e => Shown(e) && e.ActualWidth > 0))
                {
                    var bounds = rig.BoundsOf(element);
                    Assert.True(bounds.Right <= cell.Right + 1, $"{name} at {width}: {element.GetType().Name} {element.Name} sticks out ({bounds.Right} > {cell.Right})");
                    Assert.True(bounds.Left >= cell.Left - 1, $"{name} at {width}: {element.GetType().Name} {element.Name} starts left of the page");
                }

                foreach (var text in rig.Find<TextBlock>().Where(t => Shown(t) && t.TextWrapping == TextWrapping.NoWrap && t.TextTrimming == TextTrimming.None && t.ActualWidth > 0))
                {
                    var parent = VisualTreeHelper.GetParent(text) as FrameworkElement;
                    if (parent is not null && parent.ActualWidth > 0)
                    {
                        Assert.True(text.ActualWidth <= parent.ActualWidth + 1, $"{name} at {width}: text '{text.Text}' wider than its parent");
                    }
                }
            }
        });
    }

    [Fact]
    public void The_six_camera_buttons_and_no_plus_tile_fit_in_rows_of_equal_cells()
    {
        WpfTestHost.Run(() =>
        {
            foreach (var width in Widths)
            {
                using var rig = new Rig(width, cameras: 6, setup: ThreeCameras);
                var grid = rig.Named<ItemsControl>("CameraGrid");
                var panel = Descendants(grid).OfType<AdaptiveGrid>().First();

                Assert.Equal(6, panel.Children.Count);
                var sizes = Enumerable.Range(0, 6).Select(i => ((FrameworkElement)panel.Children[i]).RenderSize).ToList();
                Assert.All(sizes, size => Assert.Equal(sizes[0].Width, size.Width, 1));
                Assert.All(sizes, size => Assert.True(size.Width >= 160, $"cell {size.Width} at {width}"));
                Assert.Equal(width >= 1100 ? 4 : 3, panel.Columns);
            }
        });
    }

    [Fact]
    public void A_camera_button_shows_where_it_is_with_a_marker_and_the_pencil_and_trash_stay_inside()
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig(900, setup: ThreeCameras);

            var tiles = rig.Find<Button>().Where(b => b.Name == "Select").ToList();
            Assert.Equal(3, tiles.Count);
            var preview = Descendants(rig.Named<ItemsControl>("CameraGrid")).OfType<Border>().Where(b => b.Name == "PreviewMark").ToList();
            var program = Descendants(rig.Named<ItemsControl>("CameraGrid")).OfType<Border>().Where(b => b.Name == "ProgramMark").ToList();
            Assert.Single(preview, m => m.Visibility == Visibility.Visible);
            Assert.Single(program, m => m.Visibility == Visibility.Visible);

            foreach (var tile in rig.Find<Grid>().Where(g => g.Name == "Tile"))
            {
                var tileBounds = rig.BoundsOf(tile);
                foreach (var button in Descendants(tile).OfType<Button>().Where(b => b.Name is "Edit" or "Delete"))
                {
                    var bounds = rig.BoundsOf(button);
                    Assert.True(bounds.Left >= tileBounds.Left && bounds.Right <= tileBounds.Right && bounds.Bottom <= tileBounds.Bottom);
                    Assert.True(bounds.Width >= 28);
                }
            }

            Assert.Equal(1, rig.Find<Button>().Count(b => b.Name == "AddTile"));
        });
    }

    [Fact]
    public void The_empty_state_is_one_friendly_tile_and_Cortar_is_off()
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig(1100, cameras: 0);

            var tile = rig.Named<Button>("EmptyTile");
            Assert.Equal(Visibility.Visible, tile.Visibility);
            Assert.Contains(tile.Descendants<TextBlock>(), t => t.Text == "Adicione sua primeira c\U000000E2mera");
            Assert.Contains(tile.Descendants<TextBlock>(), t => t.Text.StartsWith("Nenhuma fonte NDI", StringComparison.Ordinal) && Shown(t));
            Assert.Equal(Visibility.Collapsed, rig.Named<ItemsControl>("CameraGrid").Visibility);
            Assert.False(rig.Named<Button>("TakeButton").IsEnabled);
            Assert.True(rig.BoundsOf(tile).Height >= 120);

            // both pictures say there is no camera, never a black box: nothing was drawn from a picture
            Assert.Null(rig.Page.PreviewPicture.Bitmap);
            Assert.Null(rig.Page.OnAirPicture.Bitmap);
        });
    }

    [Fact]
    public void Without_the_runtime_a_banner_with_the_link_and_Verificar_de_novo_shows_and_the_rest_is_off()
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig(1100, runtime: false);

            var banner = rig.Named<Border>("RuntimeBanner");
            Assert.Equal(Visibility.Visible, banner.Visibility);
            Assert.Contains(banner.Descendants<TextBlock>(), t => t.Text == "NDI Tools n\U000000E3o encontrado");
            Assert.Equal("https://ndi.video/tools/", rig.Named<Button>("DownloadLink").Content);
            var recheck = rig.Named<Button>("RecheckButton");
            Assert.Equal("Verificar de novo", AutomationProperties.GetName(recheck));
            Assert.Equal("Verificar de novo", recheck.ToolTip);
            Assert.True(recheck.IsEnabled);

            var body = rig.Named<StackPanel>("Body");
            Assert.False(body.IsEnabled);
            Assert.Equal(Visibility.Visible, body.Visibility);
            Assert.False(rig.Named<ToggleButton>("LayerSwitch").IsEnabled);
            Assert.True(rig.BoundsOf(banner).Bottom <= rig.BoundsOf(rig.Named<Border>("Stage")).Top);
        });
    }

    [Fact]
    public void With_the_runtime_there_is_no_banner()
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig(1100, setup: ThreeCameras);

            Assert.Equal(Visibility.Collapsed, rig.Named<Border>("RuntimeBanner").Visibility);
            Assert.True(rig.Named<StackPanel>("Body").IsEnabled);
        });
    }

    [Fact]
    public void The_telao_card_has_the_big_switch_off_the_chips_and_the_test_bars_and_the_settings_card_the_rest()
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig(1100, setup: ThreeCameras);
            rig.Engine.Vm.LayerOn = false;

            var layer = rig.Named<ToggleButton>("LayerSwitch");
            Assert.False(layer.IsChecked);
            Assert.True(layer.LayoutTransform is ScaleTransform { ScaleX: > 1.2 });
            Assert.True(rig.BoundsOf(layer).Height >= 32);
            foreach (var chip in new[] { "SignalChip", "FpsChip", "ResolutionChip", "DroppedChip" })
            {
                var border = rig.Named<Border>(chip);
                Assert.False(string.IsNullOrWhiteSpace(border.ToolTip as string), chip);
                Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(border)), chip);
                Assert.Equal(32, rig.BoundsOf(border).Height, 1);
            }

            Assert.False(rig.Named<ToggleButton>("TestPatternSwitch").IsChecked);
            Assert.False(rig.Named<ToggleButton>("HideSwitch").IsChecked);
            Assert.True(rig.Named<ToggleButton>("ShortcutsSwitch").IsChecked);
            Assert.True(rig.Named<ToggleButton>("MonitorLock").IsChecked);
            Assert.False(rig.Named<ComboBox>("MonitorCombo").IsEnabled);
            Assert.Equal("\\\\.\\DISPLAY3", rig.Named<ComboBox>("MonitorCombo").SelectedValue);
            Assert.Contains("Holyrics", (string)rig.Named<ToggleButton>("ShortcutsSwitch").ToolTip);
            Assert.Equal(Visibility.Collapsed, rig.Named<StackPanel>("ShortcutWarning").Visibility);

            // the cards are in the same row at 1100
            Assert.Equal(rig.BoundsOf(rig.Named<CardSection>("TelaoCard")).Top, rig.BoundsOf(rig.Named<CardSection>("SettingsCard")).Top, 1);
        });
    }

    [Fact]
    public void Unlocking_the_monitor_enables_the_list_with_names_and_resolutions()
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig(1100, setup: ThreeCameras);

            rig.Engine.Vm.MonitorLocked = false;
            rig.Layout(1100);

            var combo = rig.Named<ComboBox>("MonitorCombo");
            Assert.True(combo.IsEnabled);
            Assert.Equal(2, combo.Items.Count);
            Assert.Equal("\\\\.\\DISPLAY3", combo.SelectedValue);
            Assert.False(rig.Named<ToggleButton>("MonitorLock").IsChecked);
        });
    }

    [Fact]
    public void A_shortcut_conflict_shows_the_engines_warning_in_the_card()
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig(1100, setup: e =>
            {
                e.Hotkeys = new NdiHotkeyController(new TakenRegistry(), e.Service);
                e.Hotkeys.Apply(true);
                e.Vm.SetActive(true);
                e.Vm.SetActive(false);
            });

            Assert.Equal(Visibility.Visible, rig.Named<StackPanel>("ShortcutWarning").Visibility);
            Assert.Contains("F1", rig.Named<TextBlock>("ShortcutWarningText").Text);
            Assert.True(rig.BoundsOf(rig.Named<TextBlock>("ShortcutWarningText")).Right <= rig.Root.ActualWidth);
        });
    }

    [Fact]
    public void The_wheel_over_a_picture_moves_the_band_by_one_percent_and_by_five_with_shift_and_the_slider_follows()
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig(1100, setup: ThreeCameras);
            var picture = rig.Page.PreviewPicture;
            var start = rig.Vm.Preview.Position;

            picture.RaiseEvent(Wheel(picture, -120)); // wheel down: lower band
            Assert.Equal(start + 1, rig.Vm.Preview.Position);
            picture.RaiseEvent(Wheel(picture, 120));
            Assert.Equal(start, rig.Vm.Preview.Position);

            Assert.Equal(start, rig.Engine.Ndi.Cameras[1].VerticalPosition);
            var slider = rig.Find<Slider>().First();
            Assert.Equal(start, slider.Value);
            Assert.Equal(0, slider.Minimum);
            Assert.Equal(100, slider.Maximum);
            Assert.Equal("25%", Descendants(rig.Named<ContentControl>("PreviewLane")).OfType<TextBlock>().First(t => t.Name == "PositionText").Text);
        });
    }

    [Fact]
    public void The_wheel_with_shift_moves_five_percent()
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig(1100, setup: ThreeCameras);
            var picture = rig.Page.PreviewPicture;
            var start = rig.Vm.Preview.Position;

            // Shift is read from the keyboard state; the lane takes the same step the picture asks for
            rig.Vm.Preview.Nudge(-5);

            Assert.Equal(start - 5, rig.Vm.Preview.Position);
            var wheel = Wheel(picture, 120);
            picture.RaiseEvent(wheel);
            Assert.True(wheel.Handled, "the page must not scroll under the wheel");
        });
    }

    [Fact]
    public void The_slider_writes_the_position_of_its_camera()
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig(1100, setup: ThreeCameras);
            var slider = Descendants(rig.Named<ContentControl>("OnAirLane")).OfType<Slider>().First();

            slider.Value = 33;

            Assert.Equal(33, rig.Engine.Ndi.Cameras[0].VerticalPosition);
            Assert.Equal("33%", Descendants(rig.Named<ContentControl>("OnAirLane")).OfType<TextBlock>().First(t => t.Name == "PositionText").Text);
        });
    }

    [Fact]
    public void The_pictures_reuse_one_bitmap_and_only_poll_while_on_screen()
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig(1100, setup: ThreeCameras);
            var picture = rig.Page.OnAirPicture;
            var first = picture.Bitmap;
            Assert.NotNull(first);
            Assert.Equal(480, first.PixelWidth);
            Assert.Equal(270, first.PixelHeight);

            for (var i = 0; i < 5; i++)
            {
                rig.Engine.LaneOf(1).Push(i);
                picture.Poll();
            }

            Assert.Same(first, picture.Bitmap);
            Assert.False(first.IsFrozen);

            // a window that is not on screen asks for nothing
            Assert.False(picture.IsPolling);
            Assert.False(rig.Page.IsVisible);
        });
    }

    [Fact]
    public void The_crop_rectangle_follows_the_position_and_shows_53_percent_of_the_height()
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig(1100, setup: ThreeCameras);
            var crop = NdiCropMath.Compute(1920, 1080, 2560, 768, 25);

            Assert.Equal(768.0 / 1440, crop.VisibleFractionY, 3);
            Assert.Equal(0.533, crop.VisibleFractionY, 2);
            Assert.True(crop.V0 > 0 && crop.V1 < 1);
            Assert.Equal(25, rig.Vm.Preview.Position);
            Assert.Equal(2560, rig.Vm.Preview.CropWidth);
            Assert.Equal(768, rig.Vm.Preview.CropHeight);
        });
    }

    [Fact]
    public void The_camera_window_checks_the_name_and_the_source_and_a_listed_source_fills_the_box()
    {
        WpfTestHost.Run(() =>
        {
            var policy = AlertaIBT.App.Tests.Platform.PolicyFakes.Policy(null, [AlertaIBT.App.Tests.Platform.PolicyFakes.Primary()], (0, 0));
            static string? Check(string name, string source) =>
                string.IsNullOrWhiteSpace(name) ? "Escreva um nome." : NdiSourceParser.Parse(source) is null ? "Escolha uma fonte." : null;
            var input = new CameraEditInput("Nova c\U000000E2mera", "C\U000000E2mera 4", string.Empty, [new NdiSourceInfo("PC (OBS)", "10.0.0.2:5961"), new NdiSourceInfo("STUDIO (Central)", string.Empty)], Check);

            var dialog = CameraDialog.Create(input, policy);
            Assert.False(dialog.SaveButton.IsEnabled);
            Assert.Equal(2, dialog.SourceList.Children.Count);

            ((Button)dialog.SourceList.Children[1]).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.Equal("STUDIO (Central)", dialog.SourceBox.Text);
            Assert.True(dialog.SaveButton.IsEnabled);
            dialog.NameBox.Text = "Central";
            dialog.Commit();
            Assert.Equal(new CameraEditResult("Central", "STUDIO (Central)"), dialog.Answer);

            var empty = CameraDialog.Create(input with { Sources = [] }, policy);
            Assert.Single(empty.SourceList.Children);
            Assert.IsType<TextBlock>(empty.SourceList.Children[0]);
            empty.SourceBox.Text = "192.168.1.9";
            empty.Commit();
            Assert.Equal("192.168.1.9", empty.Answer?.Source);
        });
    }

    [Fact]
    public void The_fixed_ip_window_adds_removes_and_refuses_what_is_not_an_address()
    {
        WpfTestHost.Run(() =>
        {
            var policy = AlertaIBT.App.Tests.Platform.PolicyFakes.Policy(null, [AlertaIBT.App.Tests.Platform.PolicyFakes.Primary()], (0, 0));
            var dialog = IpListDialog.Create(["192.168.1.50"], policy);
            Assert.Single(dialog.Rows.Children);

            dialog.Box.Text = "not an ip";
            dialog.AddTyped();
            Assert.NotEmpty(dialog.Problem.Text);
            Assert.Single(dialog.Rows.Children);

            dialog.Box.Text = "10.0.0.8:5961";
            dialog.AddTyped();
            Assert.Equal(2, dialog.Rows.Children.Count);
            Assert.Equal(string.Empty, dialog.Problem.Text);

            dialog.Box.Text = "10.0.0.8:5961";
            dialog.AddTyped();
            Assert.Equal(2, dialog.Rows.Children.Count);
            Assert.NotEmpty(dialog.Problem.Text);

            var remove = Descendants(dialog.Rows).OfType<Button>().First();
            remove.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.Single(dialog.Rows.Children);

            dialog.Finish(["10.0.0.8:5961"]);
            Assert.Equal(["10.0.0.8:5961"], dialog.Answer);
        });
    }

    [Fact]
    public void Render_the_tab_for_the_owner_to_look_at()
    {
        WpfTestHost.Run(() =>
        {
            var long32 = "Lateral esquerda do corredor ala";
            foreach (var width in Widths)
            {
                using (var empty = new Rig(width, cameras: 0, runtime: true))
                {
                    empty.Snapshot($"ndi-{width}-nada-configurado");
                }

                using (var three = new Rig(width, setup: ThreeCameras))
                {
                    three.Snapshot($"ndi-{width}-3-cameras");
                }

                using (var six = new Rig(width, cameras: 6, setup: e =>
                {
                    ThreeCameras(e);
                    e.Vm.TestPattern = true;
                }))
                {
                    six.Snapshot($"ndi-{width}-6-cameras");
                }

                using (var missing = new Rig(width, runtime: false))
                {
                    missing.Snapshot($"ndi-{width}-runtime-ausente");
                }

                using (var lost = new Rig(width, setup: e =>
                {
                    ThreeCameras(e);
                    e.LaneOf(1).Set(NdiConnectionState.Lost);
                    e.Vm.Tick();
                }))
                {
                    lost.Snapshot($"ndi-{width}-sem-sinal");
                }

                using (var unlocked = new Rig(width, setup: e =>
                {
                    ThreeCameras(e);
                    e.Vm.MonitorLocked = false;
                    e.Vm.HideFromCapture = true;
                    e.Hotkeys = new NdiHotkeyController(new TakenRegistry(), e.Service);
                    e.Hotkeys.Apply(true);
                    e.Vm.SetActive(true);
                    e.Vm.SetActive(false);
                }))
                {
                    unlocked.Snapshot($"ndi-{width}-monitor-destravado");
                }

                NdiRig.Names = Enumerable.Repeat(long32, 6).ToArray();
                using (var longNames = new Rig(width, cameras: 6, setup: ThreeCameras))
                {
                    NdiRig.Names = [];
                    longNames.Snapshot($"ndi-{width}-nomes-longos");
                }

                NdiRig.Names = [];
            }

            var policy = AlertaIBT.App.Tests.Platform.PolicyFakes.Policy(null, [AlertaIBT.App.Tests.Platform.PolicyFakes.Primary()], (0, 0));
            static string? Check(string name, string source) => null;
            SnapshotDialog(
                CameraDialog.Create(new CameraEditInput("Editar c\U000000E2mera", "Central", "PC (Cam1)", [new NdiSourceInfo("PC (Cam1)", "192.168.1.20:5961"), new NdiSourceInfo("PC (OBS)", "192.168.1.21:5961"), new NdiSourceInfo("STUDIO (Lateral)", string.Empty)], Check), policy),
                "ndi-dialogo-camera");
            SnapshotDialog(IpListDialog.Create(["192.168.1.50", "10.0.0.8:5961"], policy), "ndi-dialogo-ips");
            SnapshotDialog(ConfirmDialog.Create("Remover a c\U000000E2mera \U0000201CCentral\U0000201D?", "Ela sai da lista de bot\U000000F5es. A c\U000000E2mera em si n\U000000E3o \U000000E9 mexida.", policy), "ndi-dialogo-remover");
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
        content.Measure(new Size(dialog.Width, double.PositiveInfinity));
        content.Arrange(new Rect(0, 0, dialog.Width, Math.Max(dialog.Height, content.DesiredSize.Height)));
        content.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)dialog.Width, (int)Math.Ceiling(Math.Max(dialog.Height, content.DesiredSize.Height)), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(System.IO.Path.Combine(dir, name + ".png"));
        encoder.Save(stream);
    }

    private static MouseWheelEventArgs Wheel(UIElement source, int delta) =>
        new(Mouse.PrimaryDevice, Environment.TickCount, delta) { RoutedEvent = UIElement.MouseWheelEvent, Source = source };

    // The tests never show the window, so "visible" means every parent along the way is Visible.
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

    private static bool InsideCombo(DependencyObject element)
    {
        for (var node = VisualTreeHelper.GetParent(element); node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is ComboBox)
            {
                return true;
            }
        }

        return false;
    }

    private static double Luminance(SolidColorBrush brush) =>
        (0.2126 * brush.Color.R) + (0.7152 * brush.Color.G) + (0.0722 * brush.Color.B);

    private sealed class TakenRegistry : INdiHotkeyRegistry
    {
        public bool Register(int id, AlertaIBT.Core.Input.HotkeyModifiers modifiers, int virtualKey) => virtualKey != 0x70;

        public void Unregister(int id)
        {
        }
    }
}

internal static class VisualExtensions
{
    public static IEnumerable<T> Descendants<T>(this DependencyObject parent)
        where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T found)
            {
                yield return found;
            }

            foreach (var nested in child.Descendants<T>())
            {
                yield return nested;
            }
        }
    }
}
