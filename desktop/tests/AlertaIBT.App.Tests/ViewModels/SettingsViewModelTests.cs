using AlertaIBT.App.Platform;
using AlertaIBT.App.ViewModels;
using AlertaIBT.Core.Diagnostics;
using AlertaIBT.Core.Input;
using AlertaIBT.Core.Overlay;
using AlertaIBT.Core.Services;
using AlertaIBT.Core.Settings;

namespace AlertaIBT.App.Tests.ViewModels;

public sealed class SettingsViewModelTests : IDisposable
{
    private const string ClearToken = "123456789:AAHdqTcvCH1vGWJxfSeofSAs0K5PALDsaw";

    private readonly SettingsRig _rig;
    private readonly string _logs;
    private readonly SettingsViewModel _vm;
    private int _barShows;
    private Func<Task> _showBar;

    public SettingsViewModelTests()
    {
        _rig = new SettingsRig();
        _logs = Path.Combine(_rig.Folder, "logs");
        _showBar = () =>
        {
            _barShows++;
            return Task.CompletedTask;
        };
        _vm = Create(_rig);
    }

    public void Dispose()
    {
        _vm.Dispose();
        _rig.Dispose();
    }

    private SettingsViewModel Create(SettingsRig rig) =>
        new(
            rig.Settings,
            rig.Protector,
            rig.Hub,
            rig.Saver,
            new DiagnosticsViewModel(new LogFiles(_logs), _ => { }, () => null),
            () => _showBar());

    // ---- start ----

    [Fact]
    public void It_starts_from_the_settings_with_the_documented_defaults()
    {
        Assert.True(_vm.IsTop);
        Assert.False(_vm.IsBottom);
        Assert.Equal(56, _vm.BarHeight);
        Assert.Equal(90, _vm.BarSpeed);
        Assert.Equal("M\U000000E9dia \U000000B7 56 px", _vm.SizeLabel);
        Assert.Equal("90 px/s", _vm.SpeedLabel);
        Assert.Equal(string.Empty, _vm.FontSize.Text);
        Assert.Equal("24", _vm.Margin.Text);
        Assert.Equal("320", _vm.SlideMs.Text);
        Assert.Equal("#000000", _vm.Background.Text);
        Assert.Equal("#FFF7E8", _vm.Foreground.Text);
        Assert.Equal(string.Empty, _vm.ChatIdText);
        Assert.Equal("Ctrl+Alt+Espa\U000000E7o", _vm.HotkeyDisplay);
        Assert.Equal(new BarSettings(), _vm.PreviewBar);
        Assert.False(_rig.Saver.HasPending); // loading the settings into the controls saves nothing
        Assert.False(_vm.Token.IsLocked);
    }

    [Fact]
    public void The_slider_ranges_are_the_documented_ones()
    {
        Assert.Equal(28, _vm.MinHeight);
        Assert.Equal(120, _vm.MaxHeight);
        Assert.Equal(30, _vm.MinSpeed);
        Assert.Equal(300, _vm.MaxSpeed);
    }

    // ---- the bar: autosave 600 ms after the last change, preview at once ----

    [Fact]
    public void Changing_the_size_updates_the_preview_at_once_and_saves_600_ms_later()
    {
        _vm.BarHeight = 88;

        Assert.Equal(88, _vm.PreviewBar.Height);
        Assert.Equal("Grande \U000000B7 88 px", _vm.SizeLabel);
        Assert.Equal(56, _rig.Settings.Current.Bar.Height);

        _rig.Clock.Advance(TimeSpan.FromMilliseconds(599));
        Assert.Equal(56, _rig.Settings.Current.Bar.Height);
        _rig.Clock.Advance(TimeSpan.FromMilliseconds(1));

        Assert.Equal(88, _rig.Settings.Current.Bar.Height);
        Assert.Equal(88, _rig.OnDisk().Bar.Height);
    }

    [Fact]
    public void Dragging_a_slider_saves_once_with_the_last_value()
    {
        for (var h = 56; h <= 100; h += 2)
        {
            _vm.BarHeight = h;
            _rig.Clock.Advance(TimeSpan.FromMilliseconds(20));
        }

        _rig.Rest();

        Assert.Equal(100, _rig.OnDisk().Bar.Height);
        Assert.Equal(1, _rig.ChangedCount);
    }

    [Fact]
    public void Changing_the_speed_updates_the_preview_and_persists_after_a_restart()
    {
        _vm.BarSpeed = 150;
        Assert.Equal(150, _vm.PreviewBar.Speed);
        _rig.Rest();

        // A restart: new service, new view-model, on the same file.
        using var again = new SettingsRig();
        File.Copy(_rig.SettingsPath, again.SettingsPath, overwrite: true);
        using var reloaded = new SettingsService(new SettingsStore(again.SettingsPath, again.Clock), again.Clock);
        using var vm = new SettingsViewModel(
            reloaded,
            again.Protector,
            again.Hub,
            new Autosaver(reloaded, clock: again.Clock),
            new DiagnosticsViewModel(new LogFiles(_logs), _ => { }, () => null),
            () => Task.CompletedTask);

        Assert.Equal(150, vm.BarSpeed);
        Assert.Equal(150, vm.PreviewBar.Speed);
    }

    [Theory]
    [InlineData(5000, 120)]
    [InlineData(-3, 28)]
    public void A_size_outside_the_range_is_held_in_range_before_it_is_saved(double typed, int expected)
    {
        _vm.BarHeight = typed;
        _rig.Rest();

        Assert.Equal(expected, _rig.OnDisk().Bar.Height);
        Assert.Equal(expected, _vm.PreviewBar.Height);
    }

    [Theory]
    [InlineData(5000, 300)]
    [InlineData(1, 30)]
    public void A_speed_outside_the_range_is_held_in_range_before_it_is_saved(double typed, int expected)
    {
        _vm.BarSpeed = typed;
        _rig.Rest();

        Assert.Equal(expected, _rig.OnDisk().Bar.Speed);
        Assert.Equal(expected, _vm.PreviewBar.Speed);
    }

    [Fact]
    public void The_position_is_top_by_default_and_the_rodape_choice_is_saved()
    {
        _vm.IsBottom = true;

        Assert.False(_vm.IsTop);
        Assert.Equal("bottom", _vm.PreviewBar.Position);
        _rig.Rest();
        Assert.Equal("bottom", _rig.OnDisk().Bar.Position);

        _vm.IsTop = true;
        _rig.Rest();
        Assert.Equal("top", _rig.OnDisk().Bar.Position);
    }

    [Fact]
    public void The_speed_label_says_how_long_a_pass_lasts_once_the_text_width_is_known()
    {
        _vm.ReportSampleWidth(2840);
        Assert.Equal("90 px/s \U000000B7 passa em 60 s", _vm.SpeedLabel); // (2560 + 2840) / 90

        _vm.BarSpeed = 180;
        Assert.Equal("180 px/s \U000000B7 passa em 30 s", _vm.SpeedLabel);

        _vm.ReportSampleWidth(double.NaN);
        _vm.ReportSampleWidth(-5);
        Assert.Equal("180 px/s \U000000B7 passa em 30 s", _vm.SpeedLabel);
    }

    // ---- Avancado: numbers and colors, never garbage ----

    [Fact]
    public void The_advanced_numbers_are_saved_and_the_blank_font_is_automatic()
    {
        _vm.FontSize.Text = "48";
        _vm.Margin.Text = "40";
        _vm.SlideMs.Text = "500";
        _rig.Rest();

        var bar = _rig.OnDisk().Bar;
        Assert.Equal(48, bar.FontSize);
        Assert.Equal(40, bar.PaddingX);
        Assert.Equal(500, bar.SlideMs);
        Assert.Equal(48, _vm.PreviewBar.FontSize);

        _vm.FontSize.Text = string.Empty;
        _rig.Rest();
        Assert.Null(_rig.OnDisk().Bar.FontSize);
    }

    [Fact]
    public void A_font_size_that_is_not_a_number_is_ignored_with_a_hint_and_nothing_garbage_is_saved()
    {
        _vm.FontSize.Text = "48";
        _vm.FontSize.Text = "quarenta";
        _rig.Rest();

        Assert.True(_vm.FontSize.HasHint);
        Assert.Null(_rig.OnDisk().Bar.FontSize); // the 48 that was waiting was dropped too: the box held garbage
        Assert.False(File.Exists(_rig.SettingsPath) && _rig.FileText().Contains("quarenta", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("9999", 200)]
    [InlineData("1", 8)]
    public void A_font_size_out_of_range_is_held_in_range(string typed, int expected)
    {
        _vm.FontSize.Text = typed;
        _rig.Rest();

        Assert.Equal(expected, _rig.OnDisk().Bar.FontSize);
    }

    [Fact]
    public void The_margin_and_the_slide_time_are_held_in_their_ranges_and_never_saved_as_text()
    {
        _vm.Margin.Text = "9999";
        _vm.SlideMs.Text = "abc";
        _rig.Rest();

        var bar = _rig.OnDisk().Bar;
        Assert.Equal(BarSettings.MaxPaddingX, bar.PaddingX);
        Assert.Equal(320, bar.SlideMs);
        Assert.True(_vm.SlideMs.HasHint);
    }

    [Fact]
    public void Colors_are_saved_as_hex_and_a_bad_color_is_ignored()
    {
        _vm.Background.Text = "#102030";
        _vm.Foreground.Text = "amarelo";
        _rig.Rest();

        var bar = _rig.OnDisk().Bar;
        Assert.Equal("#102030", bar.Background);
        Assert.Equal("#FFF7E8", bar.Foreground);
        Assert.Equal("#102030", _vm.PreviewBar.Background);
        Assert.True(_vm.Foreground.HasHint);
    }

    // ---- Telegram ----

    [Fact]
    public void The_token_goes_in_protected_the_chat_id_in_clear_and_the_token_is_nowhere_in_clear()
    {
        _vm.Token.Entry = ClearToken;
        _vm.ChatIdText = "-1001234567890";
        _rig.Rest();

        var saved = _rig.OnDisk();
        Assert.Equal(ClearToken, saved.GetTelegramToken(_rig.Protector));
        Assert.Equal(-1001234567890, saved.Telegram.ChatId);
        Assert.DoesNotContain(ClearToken, _rig.FileText());
        Assert.DoesNotContain("AAHdqT", _rig.FileText());
    }

    [Fact]
    public void After_saving_the_token_the_field_is_locked_with_dots_and_the_chat_id_is_locked_too()
    {
        _vm.Token.Entry = ClearToken;
        _rig.Rest();

        Assert.True(_vm.Token.IsLocked);
        Assert.Equal(string.Empty, _vm.Token.Entry);
        Assert.False(_vm.ChatIdEnabled); // the lock holds the token and the chat id
        Assert.Equal("Trocar", _vm.Token.LockButtonText);
    }

    [Fact]
    public void A_locked_field_cannot_be_edited_and_trocar_opens_both_boxes()
    {
        using var rig = new SettingsRig(new AppSettings().WithTelegramToken(ClearToken, new ReversingProtector()));
        using var vm = Create(rig);
        Assert.True(vm.Token.IsLocked);
        Assert.False(vm.ChatIdEnabled);

        vm.Token.Entry = "999999999:anotherTOKENanotherTOKEN_1234"; // typing in a locked field does nothing
        rig.Rest();
        Assert.Equal(ClearToken, rig.OnDisk().GetTelegramToken(rig.Protector));

        vm.Token.Unlock();

        Assert.False(vm.Token.IsLocked);
        Assert.True(vm.ChatIdEnabled);
        Assert.Equal(string.Empty, vm.Token.Entry);
    }

    [Fact]
    public void A_new_token_sets_the_saved_offset_back_to_zero_in_the_same_update()
    {
        using var rig = new SettingsRig(new AppSettings().WithTelegramToken(ClearToken, new ReversingProtector()));
        rig.Settings.Update(s => s with { Telegram = s.Telegram with { Offset = 8123 } });
        using var vm = Create(rig);
        var seen = new List<(long Offset, string? Token)>();
        rig.Settings.Changed += s => seen.Add((s.Telegram.Offset, s.GetTelegramToken(rig.Protector)));
        vm.Token.Unlock();

        vm.Token.Entry = "999999999:anotherTOKENanotherTOKEN_1234";
        rig.Rest();

        Assert.Equal(0, rig.OnDisk().Telegram.Offset);
        Assert.Equal("999999999:anotherTOKENanotherTOKEN_1234", rig.OnDisk().GetTelegramToken(rig.Protector));
        Assert.Single(seen); // token and offset changed in one update, never one without the other
        Assert.Equal((0L, "999999999:anotherTOKENanotherTOKEN_1234"), seen[0]);
    }

    [Fact]
    public void Typing_the_same_token_again_keeps_the_offset()
    {
        using var rig = new SettingsRig(new AppSettings().WithTelegramToken(ClearToken, new ReversingProtector()));
        rig.Settings.Update(s => s with { Telegram = s.Telegram with { Offset = 8123 } });
        using var vm = Create(rig);
        vm.Token.Unlock();

        vm.Token.Entry = ClearToken;
        rig.Rest();

        Assert.Equal(8123, rig.OnDisk().Telegram.Offset);
    }

    [Theory]
    [InlineData("123456")]
    [InlineData("abc:def")]
    [InlineData("123456789:short")]
    [InlineData("123456789:has space in the middle of it")]
    [InlineData(":AAHdqTcvCH1vGWJxfSeofSAs0K5PALDsaw")]
    public void A_token_that_does_not_look_like_a_token_is_not_saved(string typed)
    {
        _vm.Token.Entry = typed;
        _rig.Rest();

        Assert.True(_vm.Token.HasHint);
        Assert.Null(_rig.OnDisk().Telegram.TokenProtected);
        Assert.False(_vm.Token.IsLocked);
    }

    [Fact]
    public void The_chat_id_takes_a_negative_number_and_ignores_anything_else()
    {
        _vm.ChatIdText = "-100123";
        _rig.Rest();
        Assert.Equal(-100123, _rig.OnDisk().Telegram.ChatId);

        _vm.ChatIdText = "grupo";
        _rig.Rest();

        Assert.True(_vm.HasChatIdHint);
        Assert.Equal(-100123, _rig.OnDisk().Telegram.ChatId);

        _vm.ChatIdText = string.Empty;
        _rig.Rest();
        Assert.Equal(0, _rig.OnDisk().Telegram.ChatId);
        Assert.False(_vm.HasChatIdHint);
    }

    [Fact]
    public void A_chat_id_that_turned_bad_before_its_time_cancels_the_good_one_that_was_waiting()
    {
        _vm.ChatIdText = "-100123";
        _vm.ChatIdText = "-100x";
        _rig.Rest();

        Assert.Equal(0, _rig.OnDisk().Telegram.ChatId);
    }

    // ---- the shortcut ----

    [Fact]
    public void A_captured_shortcut_is_saved_as_text_and_shown_in_portuguese()
    {
        var ok = _vm.TryCaptureHotkey(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x77); // F8
        _rig.Rest();

        Assert.True(ok);
        Assert.Equal("Ctrl+Alt+F8", _rig.OnDisk().System.Hotkey);
        Assert.Equal("Ctrl+Alt+F8", _vm.HotkeyDisplay);
        Assert.False(_vm.HasHotkeyHint);
        Assert.True(HotkeyGesture.TryParse(_rig.OnDisk().System.Hotkey, out _));
    }

    [Fact]
    public void A_shortcut_without_ctrl_alt_or_win_is_refused_with_a_hint_and_not_saved()
    {
        var ok = _vm.TryCaptureHotkey(HotkeyModifiers.Shift, 0x41);
        _rig.Rest();

        Assert.False(ok);
        Assert.True(_vm.HasHotkeyHint);
        Assert.Equal("Ctrl+Alt+Space", _rig.OnDisk().System.Hotkey);
        Assert.Equal("Ctrl+Alt+Espa\U000000E7o", _vm.HotkeyDisplay);
    }

    [Theory]
    [InlineData(HotkeyModifiers.Control, 0x56)] // Ctrl+V
    [InlineData(HotkeyModifiers.Control, 0x43)] // Ctrl+C
    [InlineData(HotkeyModifiers.Control, 0x58)] // Ctrl+X
    [InlineData(HotkeyModifiers.Control, 0x5A)] // Ctrl+Z
    [InlineData(HotkeyModifiers.Control, 0x41)] // Ctrl+A
    [InlineData(HotkeyModifiers.Control, 0x59)] // Ctrl+Y
    [InlineData(HotkeyModifiers.Alt, 0x73)] // Alt+F4
    [InlineData(HotkeyModifiers.Alt, 0x09)] // Alt+Tab
    [InlineData(HotkeyModifiers.Win, 0x4C)] // Win+L
    [InlineData(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x2E)] // Ctrl+Alt+Del
    public void A_reserved_shortcut_is_refused_keeps_the_old_one_and_shows_the_hint(HotkeyModifiers modifiers, int key)
    {
        var ok = _vm.TryCaptureHotkey(modifiers, key);
        _rig.Rest();

        Assert.False(ok);
        Assert.True(_vm.HasHotkeyHint);
        Assert.Contains("Windows", _vm.HotkeyHint, StringComparison.Ordinal);
        Assert.Equal("Ctrl+Alt+Space", _rig.OnDisk().System.Hotkey);
        Assert.Equal("Ctrl+Alt+Espaço", _vm.HotkeyDisplay);
    }

    [Theory]
    [InlineData(HotkeyModifiers.None, 0x7B)]
    [InlineData(HotkeyModifiers.Shift, 0x2D)] // Shift+Insert
    [InlineData(HotkeyModifiers.Shift, 0x2E)] // Shift+Delete
    [InlineData(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x11)] // a modifier key as the main key
    public void A_shortcut_with_no_usable_modifier_or_key_is_refused_with_a_hint(HotkeyModifiers modifiers, int key)
    {
        var ok = _vm.TryCaptureHotkey(modifiers, key);
        _rig.Rest();

        Assert.False(ok);
        Assert.True(_vm.HasHotkeyHint);
        Assert.Equal("Ctrl+Alt+Space", _rig.OnDisk().System.Hotkey);
    }

    [Fact]
    public void A_good_shortcut_after_a_refused_one_clears_the_hint()
    {
        _vm.TryCaptureHotkey(HotkeyModifiers.Control, 0x56);
        Assert.True(_vm.HasHotkeyHint);

        Assert.True(_vm.TryCaptureHotkey(HotkeyModifiers.Control | HotkeyModifiers.Shift, 0x56)); // Ctrl+Shift+V is free
        Assert.False(_vm.HasHotkeyHint);
    }

    [Fact]
    public void The_shell_telling_that_the_shortcut_is_taken_shows_in_the_tab()
    {
        _vm.ApplyHotkey(new HotkeyStatus("Ctrl+Alt+Space", "Ctrl+Alt+Espa\U000000E7o", false, "j\U000000E1 \U000000E9 usado por outro programa"));
        Assert.True(_vm.HasHotkeyProblem);

        _vm.ApplyHotkey(new HotkeyStatus("Ctrl+Alt+Space", "Ctrl+Alt+Espa\U000000E7o", true, null));
        Assert.False(_vm.HasHotkeyProblem);
    }

    [Fact]
    public void A_taken_shortcut_is_explained_in_the_tab_with_the_short_reason_and_the_way_out()
    {
        _vm.ApplyHotkey(new HotkeyStatus(
            "Ctrl+Alt+Space",
            "Ctrl+Alt+Espa\U000000E7o",
            false,
            "long text for the panel",
            "Atalho Ctrl+Alt+Espa\U000000E7o em uso por outro programa"));

        Assert.Equal("Atalho Ctrl+Alt+Espa\U000000E7o em uso por outro programa. Escolha outro no campo acima.", _vm.HotkeyProblem);
    }

    // ---- Mostrar barra 3 s ----

    [Fact]
    public async Task The_bar_test_saves_what_the_sliders_show_first_and_then_plays_on_the_real_bar()
    {
        _rig.Hub.SetLedPresent(true);
        _vm.BarHeight = 90; // still inside its 600 ms
        var heightWhenShown = 0;
        _showBar = () =>
        {
            _barShows++;
            heightWhenShown = _rig.Settings.Current.Bar.Height;
            return Task.CompletedTask;
        };

        await _vm.ShowBarCommand.ExecuteAsync(null);

        Assert.Equal(1, _barShows);
        Assert.Equal(90, heightWhenShown); // the real bar reads the saved settings: they were saved before it played
        Assert.False(_vm.IsBarTestRunning);
        Assert.False(_vm.HasBarTestMessage);
    }

    [Fact]
    public async Task The_bar_test_is_marked_running_until_the_pass_ends()
    {
        _rig.Hub.SetLedPresent(true);
        var gate = new TaskCompletionSource();
        _showBar = () =>
        {
            _barShows++;
            return gate.Task;
        };

        var running = _vm.ShowBarCommand.ExecuteAsync(null);

        Assert.True(_vm.IsBarTestRunning);
        Assert.True(_vm.HasBarTestMessage);
        gate.SetResult();
        await running;
        Assert.False(_vm.IsBarTestRunning);
    }

    [Fact]
    public async Task Without_the_led_the_test_says_so_and_does_not_call_the_bar()
    {
        _rig.Hub.SetLedPresent(false);

        await _vm.ShowBarCommand.ExecuteAsync(null);

        Assert.Equal(0, _barShows);
        Assert.Contains("LED", _vm.BarTestMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task While_an_alert_is_passing_the_test_waits_for_it()
    {
        _rig.Hub.SetLedPresent(true);
        _rig.Hub.SetShowing(true);

        await _vm.ShowBarCommand.ExecuteAsync(null);

        Assert.Equal(0, _barShows);
        Assert.True(_vm.HasBarTestMessage);
    }

    [Fact]
    public async Task A_bar_that_throws_is_reported_and_never_crashes_the_window()
    {
        _rig.Hub.SetLedPresent(true);
        _showBar = () => throw new InvalidOperationException("boom");

        await _vm.ShowBarCommand.ExecuteAsync(null);

        Assert.False(_vm.IsBarTestRunning);
        Assert.Contains("registro", _vm.BarTestMessage, StringComparison.Ordinal);
    }

    // ---- the site line ----

    [Fact]
    public void The_site_line_comes_from_the_status()
    {
        Assert.Equal(StatusLight.Gray, _vm.SiteLight);
        Assert.Equal("N\U000000E3o configurado", _vm.SiteText);
    }
}
