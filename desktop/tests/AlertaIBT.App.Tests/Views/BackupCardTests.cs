using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AlertaIBT.App.Platform;
using AlertaIBT.App.Tests.Platform;
using AlertaIBT.App.Tests.ViewModels;
using AlertaIBT.App.ViewModels;
using AlertaIBT.App.Views;
using AlertaIBT.Core.Backup;
using AlertaIBT.Core.Diagnostics;
using AlertaIBT.Core.Settings;

namespace AlertaIBT.App.Tests.Views;

// The Backup card of Configuracoes at 900, 1100 and 1600 px, and the two windows it opens (the password and the preview). Set ALERTAIBT_RENDER_DIR
// to also get PNG pictures to look at.
public sealed class BackupCardTests
{
    private static readonly double[] Widths = [900, 1100, 1600];

    public static TheoryData<double> WindowWidths => new(Widths);

    private static AppWindowPolicy Policy() => PolicyFakes.Policy(null, [PolicyFakes.Primary()], (0, 0));

    private sealed class Rig : IDisposable
    {
        private readonly SettingsRig _settings = new();
        private readonly AutoBackupService _auto;

        public Rig(double windowWidth)
        {
            var gateway = new FakeGateway(_settings.Clock);
            var composer = new QuickPanelViewModel(gateway, _settings.Hub, clock: _settings.Clock);
            var home = new HomeViewModel(gateway, composer, clock: _settings.Clock);
            var correction = new CorrectionViewModel(_settings.Settings, _settings.Protector, _settings.Hub, _settings.Saver);
            var diagnostics = new DiagnosticsViewModel(new LogFiles(Path.Combine(_settings.Folder, "logs")), _ => { }, () => null);
            var updates = new FakeUpdateStatus();
            Folder = Path.Combine(_settings.Folder, "copias");
            Directory.CreateDirectory(Folder);
            _auto = new AutoBackupService(_settings.Settings, _settings.Protector, _settings.Clock, runner: work =>
            {
                work();
                return Task.CompletedTask;
            });
            _auto.Start();
            Backup = new BackupViewModel(_settings.Settings, _settings.Protector, _auto, _settings.Saver, new NullUi(), () => { }, clock: _settings.Clock);
            var settings = new SettingsViewModel(_settings.Settings, _settings.Protector, _settings.Hub, _settings.Saver, diagnostics, () => Task.CompletedTask, updates: updates, backup: Backup);
            ViewModel = new MainViewModel(_settings.Hub, home, correction, settings, _settings.Saver, updates: updates);
            Window = new MainWindow(ViewModel, null);
            Root = (FrameworkElement)Window.Content;
            ViewModel.SelectTabCommand.Execute(MainTab.Settings);
            Layout(windowWidth);
        }

        public string Folder { get; }

        public SettingsRig Settings => _settings;

        public AutoBackupService Auto => _auto;

        public BackupViewModel Backup { get; }

        public MainViewModel ViewModel { get; }

        public MainWindow Window { get; }

        public FrameworkElement Root { get; }

        public SettingsPage Page => Window.Settings;

        public double Width { get; private set; }

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

        public void Snapshot(string name)
        {
            var dir = Environment.GetEnvironmentVariable("ALERTAIBT_RENDER_DIR");
            if (string.IsNullOrEmpty(dir))
            {
                return;
            }

            // Only the card and some page above it: from the Site card down, the part of the page that matters here.
            var top = Math.Max(0, BoundsOf(Page.SiteSection).Top - 16);
            var bottom = BoundsOf(Page.BackupSection).Bottom + 16;
            var width = (int)Math.Ceiling(Root.ActualWidth);
            var height = (int)Math.Ceiling(bottom - top);
            Render(Root, width, height, new Point(0, -top), dir, name);
        }

        public void Dispose()
        {
            Backup.Dispose();
            _auto.Dispose();
            ViewModel.Dispose();
            _settings.Dispose();
        }
    }

    private sealed class NullUi : IBackupUi
    {
        public string? PickSavePath(string suggestedName) => null;

        public string? PickOpenPath() => null;

        public string? PickFolder(string? current) => null;

        public string? AskNewPassword(string title) => null;

        public string? AskPassword(string title, Func<string, string?> verify) => null;

        public bool ShowPreview(IReadOnlyList<BackupChange> changes) => false;

        public bool ConfirmRestore() => false;
    }

    private static void Render(FrameworkElement element, int width, int height, Point offset, string dir, string name)
    {
        Directory.CreateDirectory(dir);
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x0A, 0x0A, 0x0C)), null, new Rect(0, 0, width, height));
            var brush = new VisualBrush(element)
            {
                Stretch = Stretch.Fill,
                ViewboxUnits = BrushMappingMode.Absolute,
                Viewbox = new Rect(-offset.X, -offset.Y, width, height),
                ViewportUnits = BrushMappingMode.Absolute,
                Viewport = new Rect(0, 0, width, height),
            };
            context.DrawRectangle(brush, null, new Rect(0, 0, width, height));
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(dir, name + ".png"));
        encoder.Save(stream);
    }

    private static void RenderDialog(Window dialog, string name)
    {
        var dir = Environment.GetEnvironmentVariable("ALERTAIBT_RENDER_DIR");
        if (string.IsNullOrEmpty(dir))
        {
            return;
        }

        // Shown for a moment far off the screen (the window is excluded from capture anyway), so the picture is what the person would see.
        dialog.WindowStartupLocation = WindowStartupLocation.Manual;
        dialog.Left = -5000;
        dialog.Top = -5000;
        dialog.ShowActivated = false;
        dialog.Show();
        try
        {
            var content = (FrameworkElement)dialog.Content;
            content.UpdateLayout();
            Render(content, (int)Math.Ceiling(dialog.Width), (int)Math.Ceiling(dialog.Height), new Point(0, 0), dir, name);
        }
        finally
        {
            dialog.Hide();
        }
    }

    private static Brush Resource(string key) => (Brush)Application.Current.FindResource(key);

    [Theory]
    [MemberData(nameof(WindowWidths))]
    public void The_card_sits_under_the_site_card_with_the_header_the_two_icon_buttons_and_three_aligned_blocks(double width)
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig(width);
            var page = rig.Page;
            var card = page.BackupSection;

            Assert.Equal(Visibility.Visible, card.Visibility);
            Assert.Equal("Backup", (string)card.Header);
            Assert.NotNull(card.Icon);
            Assert.Equal(rig.BoundsOf(page.SiteSection).Bottom + 24, rig.BoundsOf(card).Top, 1);
            Assert.Equal(rig.BoundsOf(page.SiteSection).Left, rig.BoundsOf(card).Left, 1);
            Assert.Equal(rig.BoundsOf(page.SiteSection).Width, rig.BoundsOf(card).Width, 1);

            // Icon buttons: 32 px, a tooltip and a name, at the right end of the header, side by side.
            var cardBox = rig.BoundsOf(card);
            foreach (var (button, tip) in new[] { (page.ExportBackupButton, "Exportar backup"), (page.RestoreBackupButton, "Restaurar backup"), (page.BackupFolderButton, "Escolher a pasta das c\U000000F3pias") })
            {
                Assert.Equal(32, button.ActualWidth);
                Assert.Equal(32, button.ActualHeight);
                Assert.Equal(tip, button.ToolTip);
                Assert.Equal(tip, AutomationProperties.GetName(button));
                Assert.True(cardBox.Contains(rig.BoundsOf(button)), tip + " is cut");
            }

            var export = rig.BoundsOf(page.ExportBackupButton);
            var restore = rig.BoundsOf(page.RestoreBackupButton);
            Assert.True(restore.Left >= export.Right + 8 - 0.5);
            Assert.Equal(export.Top, restore.Top, 1);
            var title = (TextBlock)card.Template.FindName("HeaderText", card);
            Assert.True(rig.BoundsOf(title).Right <= export.Left);
            Assert.True(restore.Right >= cardBox.Right - 24 - 1);

            // The three blocks line up and nothing inside them is cut or overlaps.
            var rows = new[] { page.AutoBackupRow, page.BackupFolderRow, page.BackupStateRow };
            Assert.All(rows, row => Assert.Equal(rig.BoundsOf(rows[0]).Left, rig.BoundsOf(row).Left, 1));
            Assert.All(rows, row => Assert.Equal(rig.BoundsOf(rows[0]).Right, rig.BoundsOf(row).Right, 1));
            Assert.True(rig.BoundsOf(page.BackupFolderRow).Top >= rig.BoundsOf(page.AutoBackupRow).Bottom);
            Assert.True(rig.BoundsOf(page.BackupStateRow).Top >= rig.BoundsOf(page.BackupFolderRow).Bottom);
            Assert.True(rig.BoundsOf(page.BackupFolderText).Right <= rig.BoundsOf(page.BackupFolderButton).Left);
            Assert.True(rig.BoundsOf(page.AutoBackupSwitch).Right <= rig.BoundsOf(page.AutoBackupRow).Right);
            Assert.True(rig.BoundsOf(page.BackupFolderButton).Right <= rig.BoundsOf(page.BackupFolderRow).Right);
            Assert.True(rig.BoundsOf(page.BackupStateText).Right <= rig.BoundsOf(page.BackupStateRow).Right);
            Assert.True(rig.BoundsOf(card).Bottom <= rig.BoundsOf(page.DiagnosticsCard).Top);
        });
    }

    [Fact]
    public void The_card_follows_the_design_system_and_the_blocks_are_lighter_than_the_card()
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig(1100);
            var card = rig.Page.BackupSection;
            var border = (Border)VisualTreeHelper.GetChild(card, 0);

            Assert.Same(Resource("IbtSurfaceBrush"), border.Background);
            Assert.Equal(new Thickness(1), border.BorderThickness);
            Assert.Equal(12, border.CornerRadius.TopLeft);
            foreach (var row in new[] { rig.Page.AutoBackupRow, rig.Page.BackupFolderRow, rig.Page.BackupStateRow })
            {
                var block = (Border)VisualTreeHelper.GetChild(row, 0);
                Assert.Same(Resource("IbtSurfaceRaisedBrush"), block.Background);
                Assert.NotNull(row.Icon);
            }

            Assert.False(string.IsNullOrWhiteSpace(card.Info));
            Assert.Contains("playlists", card.Info!, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Without_a_backup_view_model_the_card_is_not_shown()
    {
        WpfTestHost.Run(() =>
        {
            var settingsRig = new SettingsRig();
            try
            {
                var diagnostics = new DiagnosticsViewModel(new LogFiles(Path.Combine(settingsRig.Folder, "logs")), _ => { }, () => null);
                var settings = new SettingsViewModel(settingsRig.Settings, settingsRig.Protector, settingsRig.Hub, settingsRig.Saver, diagnostics, () => Task.CompletedTask);
                var page = new SettingsPage { DataContext = settings };

                Assert.Equal(Visibility.Collapsed, page.BackupSection.Visibility);
                settings.Dispose();
            }
            finally
            {
                settingsRig.Dispose();
            }
        });
    }

    [Fact]
    public void The_status_dot_and_the_result_chip_follow_the_state()
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig(1100);
            var page = rig.Page;

            Assert.Equal(Visibility.Collapsed, page.BackupResultChip.Visibility);
            Assert.Equal("Desligada", page.BackupStateText.Text);
            Assert.False(page.AutoBackupSwitch.IsChecked);

            rig.Auto.Enable(rig.Folder, "senha-da-copia-1");
            rig.Layout(1100);
            Assert.True(page.AutoBackupSwitch.IsChecked);
            Assert.StartsWith("hoje", page.BackupStateText.Text, StringComparison.Ordinal);
            Assert.Equal(rig.Folder, page.BackupFolderText.Text);
            Assert.Equal(rig.Folder, page.BackupFolderText.ToolTip);
            var green = ((SolidColorBrush)page.BackupStateDot.Fill).Color;
            rig.Snapshot("backup-card-" + 1100 + "-ligada");

            Directory.Delete(rig.Folder, recursive: true);
            rig.Settings.Settings.Update(s => s with { Bar = s.Bar with { Speed = 77 } });
            rig.Settings.Clock.Advance(AutoBackupService.Debounce);
            rig.Layout(1100);
            var red = ((SolidColorBrush)page.BackupStateDot.Fill).Color;
            Assert.NotEqual(green, red);
            Assert.Equal("Falhou", page.BackupStateText.Text);
            var tip = (string)((FrameworkElement)page.BackupStateText.Parent).ToolTip;
            Assert.Contains("pasta", tip, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void Render_the_card_at_every_width_in_each_state_for_a_look()
    {
        WpfTestHost.Run(() =>
        {
            foreach (var width in Widths)
            {
                using var rig = new Rig(width);
                rig.Snapshot($"backup-card-{(int)width}-desligada");

                rig.Auto.Enable(rig.Folder, "senha-da-copia-1");
                rig.Layout(width);
                rig.Snapshot($"backup-card-{(int)width}-ligada");

                Directory.Delete(rig.Folder, recursive: true);
                rig.Settings.Settings.Update(s => s with { Bar = s.Bar with { Speed = 77 } });
                rig.Settings.Clock.Advance(AutoBackupService.Debounce);
                rig.Layout(width);
                rig.Snapshot($"backup-card-{(int)width}-erro");
            }
        });
    }

    [Fact]
    public void Render_the_chip_in_both_colors_for_a_look()
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig(1100);
            var ok = typeof(BackupViewModel).GetMethod("Report", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            ok.Invoke(rig.Backup, [BackupResultKind.Ok, "Backup salvo"]);
            rig.Layout(1100);
            Assert.Equal(Visibility.Visible, rig.Page.BackupResultChip.Visibility);
            Assert.Equal("Backup salvo", rig.Page.BackupResultText.Text);
            var okBrush = ((SolidColorBrush)rig.Page.BackupResultChip.Background).Color;
            rig.Snapshot("backup-card-1100-chip-ok");

            ok.Invoke(rig.Backup, [BackupResultKind.Error, "Senha incorreta ou arquivo alterado."]);
            rig.Layout(1100);
            Assert.NotEqual(okBrush, ((SolidColorBrush)rig.Page.BackupResultChip.Background).Color);
            Assert.True(rig.Page.BackupResultChip.ActualWidth <= rig.BoundsOf(rig.Page.BackupSection).Width);
            rig.Snapshot("backup-card-1100-chip-erro");
        });
    }

    [Fact]
    public void The_new_password_window_asks_twice_needs_eight_characters_and_a_match_and_can_show_what_is_typed()
    {
        WpfTestHost.Run(() =>
        {
            var dialog = PasswordDialog.CreateNew("Senha do backup", Policy());

            Assert.Equal(Visibility.Visible, dialog.SecondBlock.Visibility);
            Assert.False(dialog.OkButton.IsEnabled);
            Assert.False(dialog.Revealed);
            Assert.Equal(Visibility.Visible, dialog.Hidden1.Visibility);
            Assert.Equal(Visibility.Collapsed, dialog.Shown1.Visibility);
            RenderDialog(dialog, "backup-senha-nova-vazia");

            dialog.SetFirst("curta");
            Assert.Contains("8 caracteres", dialog.Problem.Text, StringComparison.Ordinal);
            Assert.False(dialog.OkButton.IsEnabled);
            RenderDialog(dialog, "backup-senha-nova-curta");

            dialog.SetFirst("senha-bem-comprida");
            dialog.SetSecond("senha-diferente!!");
            Assert.Contains("conferem", dialog.Problem.Text, StringComparison.Ordinal);
            Assert.False(dialog.OkButton.IsEnabled);
            RenderDialog(dialog, "backup-senha-nova-diferente");

            dialog.SetSecond("senha-bem-comprida");
            Assert.Equal(string.Empty, dialog.Problem.Text);
            Assert.True(dialog.OkButton.IsEnabled);

            // The password box holds the same text as the visible one, and the eye swaps which is on screen.
            Assert.Equal("senha-bem-comprida", dialog.Hidden1.Password);
            dialog.EyeButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Assert.True(dialog.Revealed);
            Assert.Equal(Visibility.Visible, dialog.Shown1.Visibility);
            Assert.Equal(Visibility.Collapsed, dialog.Hidden1.Visibility);
            Assert.Equal("Esconder a senha", dialog.EyeButton.ToolTip);
            RenderDialog(dialog, "backup-senha-nova-visivel");
            dialog.EyeButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Assert.Equal("Mostrar a senha", dialog.EyeButton.ToolTip);

            dialog.OkButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Assert.Equal("senha-bem-comprida", dialog.Answer);
            Assert.Equal(40, dialog.OkButton.Height);
        });
    }

    [Fact]
    public void The_new_password_window_never_accepts_a_short_or_unmatched_password_even_if_committed_directly()
    {
        WpfTestHost.Run(() =>
        {
            var dialog = PasswordDialog.CreateNew("Senha do backup", Policy());
            dialog.SetFirst("1234567");
            dialog.SetSecond("1234567");
            dialog.Commit();
            Assert.Null(dialog.Answer);

            dialog.SetFirst("12345678");
            dialog.SetSecond("12345679");
            dialog.Commit();
            Assert.Null(dialog.Answer);

            var cancel = PasswordDialog.CreateNew("Senha do backup", Policy());
            cancel.SetFirst("12345678");
            cancel.SetSecond("12345678");
            cancel.CancelButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Assert.Null(cancel.Answer);
            Assert.True(cancel.CancelButton.IsCancel);
        });
    }

    [Fact]
    public void The_open_password_window_asks_once_shows_the_problem_inside_and_stays_open_until_the_password_works()
    {
        WpfTestHost.Run(() =>
        {
            var dialog = PasswordDialog.CreateOpen("Senha do backup", typed => typed == "certa-123" ? null : "Senha incorreta ou arquivo alterado.", Policy());

            Assert.Equal(Visibility.Collapsed, dialog.SecondBlock.Visibility);
            Assert.Equal("Abrir", dialog.OkButton.Content);
            dialog.SetFirst("errada-123");
            dialog.Commit();
            Assert.Null(dialog.Answer);
            Assert.Equal("Senha incorreta ou arquivo alterado.", dialog.Problem.Text);
            RenderDialog(dialog, "backup-senha-abrir-errada");

            dialog.SetFirst("certa-123");
            Assert.Equal(string.Empty, dialog.Problem.Text);
            dialog.Commit();
            Assert.Equal("certa-123", dialog.Answer);
        });
    }

    private static IReadOnlyList<BackupChange> SamplePreview(bool anyChange)
    {
        var protector = new ReversingProtector();
        var current = new AppSettings().WithTelegramToken("123456789:AAFakeTokenFakeTokenFakeToken_1", protector);
        var other = anyChange
            ? current
                .WithTelegramToken("999999999:ZZOtherTokenOtherTokenOther_9", protector)
                .WithGeminiKey("AIzaSyOTHER-gemini-key-9876543210", protector)
            : current;
        other = other with
        {
            Bar = anyChange ? other.Bar with { Speed = 60, Height = 40, Position = "bottom" } : other.Bar,
            System = anyChange ? other.System with { Hotkey = "Ctrl+Alt+P", AlertsEnabled = false } : other.System,
            Site = anyChange ? other.Site with { DatabaseUrl = "https://outra-rtdb.firebaseio.com", DeviceEmail = "pc-telao@alertaibt.web.app" } : other.Site,
            Correction = anyChange ? other.Correction with { FixedRules = "a=b\nc=d\ne=f" } : other.Correction,
        };
        var payload = SettingsBackup.Capture(other, protector, DateTimeOffset.UnixEpoch);
        return SettingsBackup.Preview(current, payload, protector);
    }

    [Fact]
    public void The_preview_window_lists_only_what_changes_with_old_and_new_masked_and_Cancelar_is_the_default()
    {
        WpfTestHost.Run(() =>
        {
            var changes = SamplePreview(true);
            var dialog = RestorePreviewDialog.Create(changes, Policy());

            Assert.Equal(changes.Count(c => c.Changed), dialog.Rows.Children.Count);
            Assert.True(dialog.Rows.Children.Count > 3);
            Assert.True(dialog.CancelButton.IsDefault);
            Assert.True(dialog.CancelButton.IsCancel);
            Assert.True(dialog.ContinueButton.IsEnabled);
            var texts = string.Join("|", dialog.Rows.Children.OfType<Border>().SelectMany(b => ((Grid)b.Child).Children.OfType<TextBlock>()).Select(t => t.Text));
            Assert.Contains("123••••••", texts, StringComparison.Ordinal);
            Assert.Contains("999••••••", texts, StringComparison.Ordinal);
            Assert.DoesNotContain("FakeToken", texts, StringComparison.Ordinal);
            Assert.DoesNotContain("OtherToken", texts, StringComparison.Ordinal);
            Assert.DoesNotContain("gemini-key", texts, StringComparison.Ordinal);
            RenderDialog(dialog, "backup-previa");

            dialog.ContinueButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Assert.True(dialog.Answer);

            var cancelled = RestorePreviewDialog.Create(changes, Policy());
            Assert.False(cancelled.Answer);
        });
    }

    [Fact]
    public void The_preview_window_of_a_backup_equal_to_now_says_nothing_changes_and_cannot_continue()
    {
        WpfTestHost.Run(() =>
        {
            var dialog = RestorePreviewDialog.Create(SamplePreview(false), Policy());

            Assert.Empty(dialog.Rows.Children);
            Assert.False(dialog.ContinueButton.IsEnabled);
            RenderDialog(dialog, "backup-previa-igual");
        });
    }

    [Fact]
    public void The_two_windows_are_hidden_from_screen_capture_before_they_show()
    {
        WpfTestHost.Run(() =>
        {
            var password = PasswordDialog.CreateNew("Senha do backup", Policy());
            var preview = RestorePreviewDialog.Create(SamplePreview(true), Policy());

            Assert.True(CaptureExclusion.IsExcluded(new System.Windows.Interop.WindowInteropHelper(password).Handle));
            Assert.True(CaptureExclusion.IsExcluded(new System.Windows.Interop.WindowInteropHelper(preview).Handle));
            Assert.False(password.IsVisible);
        });
    }

    [Fact]
    public void The_last_question_is_a_confirm_with_Restaurar_and_Cancelar_and_Cancelar_is_the_default()
    {
        WpfTestHost.Run(() =>
        {
            var dialog = ConfirmDialog.Create("Restaurar este backup?", "x", Policy(), "Restaurar", "Cancelar");

            Assert.Equal("Restaurar", dialog.YesButton.Content);
            Assert.Equal("Cancelar", dialog.NoButton.Content);
            Assert.True(dialog.NoButton.IsDefault);
            RenderDialog(dialog, "backup-confirmar");
        });
    }
}
