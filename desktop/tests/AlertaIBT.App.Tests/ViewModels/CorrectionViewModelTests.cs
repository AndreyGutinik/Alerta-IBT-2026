using AlertaIBT.App.ViewModels;
using AlertaIBT.Core.Correction;
using AlertaIBT.Core.Services;
using AlertaIBT.Core.Settings;

namespace AlertaIBT.App.Tests.ViewModels;

public sealed class CorrectionViewModelTests : IDisposable
{
    private const string ClearKey = "AIzaSyD-clear-text-key-1234567890abcdef";

    private readonly SettingsRig _rig = new();
    private readonly List<string> _inspected = [];
    private CorrectionViewModel? _vm;

    public void Dispose()
    {
        _vm?.Dispose();
        _rig.Dispose();
    }

    private CorrectionViewModel Create(Func<string, FixedRulesReport>? inspect = null)
    {
        _vm = new CorrectionViewModel(
            _rig.Settings,
            _rig.Protector,
            _rig.Hub,
            _rig.Saver,
            inspect: inspect ?? (text =>
            {
                _inspected.Add(text);
                return FixedRules.Inspect(text);
            }));
        return _vm;
    }

    // ---- start ----

    [Fact]
    public void It_starts_from_the_settings()
    {
        using var rig = new SettingsRig(new AppSettings
        {
            Correction = new CorrectionSettings { Enabled = false, Mode = "offline", GeminiModel = "gemini-x", FixedRules = "a => b" },
        });
        using var vm = new CorrectionViewModel(rig.Settings, rig.Protector, rig.Hub, rig.Saver);

        Assert.False(vm.Enabled);
        Assert.False(vm.UseGemini);
        Assert.True(vm.UseOffline);
        Assert.Equal("gemini-x", vm.Model);
        Assert.Equal("a => b", vm.RulesText);
        Assert.Equal("1 regra", vm.RulesNote);
    }

    [Fact]
    public void The_defaults_are_on_gemini_and_the_default_model()
    {
        var vm = Create();

        Assert.True(vm.Enabled);
        Assert.True(vm.UseGemini);
        Assert.Equal(GeminiPrompt.DefaultModel, vm.Model);
        Assert.Equal(GeminiPrompt.DefaultModel, vm.DefaultModel);
        Assert.Equal(string.Empty, vm.RulesNote);
        Assert.False(vm.HasRulesNote);
        Assert.False(_rig.Saver.HasPending); // loading the settings into the controls saves nothing
        Assert.Empty(_inspected);
    }

    // ---- autosave of every control ----

    [Fact]
    public void The_switch_the_mode_and_the_model_save_600_ms_after_the_last_change()
    {
        var vm = Create();

        vm.Enabled = false;
        vm.SelectModeCommand.Execute("offline");
        vm.Model = "gemini-3.1-flash-lite";

        Assert.True(_rig.Settings.Current.Correction.Enabled);
        _rig.Clock.Advance(TimeSpan.FromMilliseconds(599));
        Assert.True(_rig.Settings.Current.Correction.Enabled);
        _rig.Clock.Advance(TimeSpan.FromMilliseconds(1));

        var saved = _rig.OnDisk().Correction;
        Assert.False(saved.Enabled);
        Assert.Equal("offline", saved.Mode);
        Assert.Equal("gemini-3.1-flash-lite", saved.GeminiModel);
        Assert.Equal(1, _rig.ChangedCount);
    }

    [Fact]
    public void Choosing_gemini_again_saves_gemini_and_the_two_options_exclude_each_other()
    {
        var vm = Create();
        vm.SelectModeCommand.Execute("offline");
        _rig.Rest();

        vm.SelectModeCommand.Execute("gemini");
        _rig.Rest();

        Assert.True(vm.UseGemini);
        Assert.False(vm.UseOffline);
        Assert.Equal("gemini", _rig.OnDisk().Correction.Mode);
    }

    [Fact]
    public void A_blank_model_means_the_default_model_and_a_bad_one_is_ignored_with_a_hint()
    {
        var vm = Create();
        vm.Model = "gemini-9";
        _rig.Rest();

        vm.Model = "   ";
        _rig.Rest();
        Assert.Equal(GeminiPrompt.DefaultModel, _rig.OnDisk().Correction.GeminiModel);

        vm.Model = "my model; drop";
        _rig.Rest();

        Assert.True(vm.HasModelHint);
        Assert.Equal(GeminiPrompt.DefaultModel, _rig.OnDisk().Correction.GeminiModel); // nothing garbage saved

        vm.Model = "gemini-9";
        Assert.False(vm.HasModelHint);
    }

    [Fact]
    public void A_model_that_turned_bad_before_its_time_cancels_the_good_one_that_was_waiting()
    {
        var vm = Create();

        vm.Model = "gemini-9";
        vm.Model = "gemini 9";
        _rig.Rest();

        Assert.Equal(GeminiPrompt.DefaultModel, _rig.OnDisk().Correction.GeminiModel);
    }

    [Fact]
    public void The_reset_button_puts_the_default_model_back()
    {
        var vm = Create();
        vm.Model = "gemini-9";
        _rig.Rest();

        vm.ResetModelCommand.Execute(null);
        _rig.Rest();

        Assert.Equal(GeminiPrompt.DefaultModel, vm.Model);
        Assert.Equal(GeminiPrompt.DefaultModel, _rig.OnDisk().Correction.GeminiModel);
    }

    // ---- the Gemini key ----

    [Fact]
    public void A_new_key_is_stored_protected_and_the_clear_text_is_nowhere()
    {
        var vm = Create();

        vm.GeminiKey.Entry = ClearKey;
        _rig.Rest();

        var saved = _rig.OnDisk();
        Assert.Equal(ClearKey, saved.GetGeminiKey(_rig.Protector));
        Assert.DoesNotContain(ClearKey, _rig.FileText());
        Assert.DoesNotContain("AIza", _rig.FileText());
        Assert.DoesNotContain(ClearKey, saved.ToString());
        Assert.DoesNotContain(ClearKey, _rig.Settings.Current.ToString());
    }

    [Fact]
    public void After_saving_the_key_field_is_locked_with_dots_and_holds_nothing_in_clear()
    {
        var vm = Create();
        vm.GeminiKey.Entry = ClearKey;

        _rig.Rest();

        Assert.True(vm.GeminiKey.IsLocked);
        Assert.True(vm.GeminiKey.HasStored);
        Assert.Equal(string.Empty, vm.GeminiKey.Entry);
        Assert.Equal("Trocar", vm.GeminiKey.LockButtonText);
        Assert.DoesNotContain(ClearKey, vm.GeminiKey.Hint ?? string.Empty);
    }

    [Fact]
    public void An_open_start_with_a_stored_key_shows_it_locked_and_trocar_opens_an_empty_box()
    {
        using var rig = new SettingsRig(new AppSettings().WithGeminiKey(ClearKey, new ReversingProtector()));
        using var vm = new CorrectionViewModel(rig.Settings, rig.Protector, rig.Hub, rig.Saver);

        Assert.True(vm.GeminiKey.IsLocked);
        Assert.Equal(string.Empty, vm.GeminiKey.Entry);

        vm.GeminiKey.Unlock();

        Assert.Equal(string.Empty, vm.GeminiKey.Entry); // the stored secret is not put in the box
        Assert.False(vm.GeminiKey.IsLocked);
    }

    [Theory]
    [InlineData("short")]
    [InlineData("has a space inside the long key 1234567890")]
    public void A_key_that_looks_wrong_is_not_saved_and_the_old_one_stays(string typed)
    {
        using var rig = new SettingsRig(new AppSettings().WithGeminiKey(ClearKey, new ReversingProtector()));
        using var vm = new CorrectionViewModel(rig.Settings, rig.Protector, rig.Hub, rig.Saver);
        vm.GeminiKey.Unlock();

        vm.GeminiKey.Entry = typed;
        rig.Rest();

        Assert.True(vm.GeminiKey.HasHint);
        Assert.Equal(ClearKey, rig.OnDisk().GetGeminiKey(rig.Protector));
    }

    [Fact]
    public void A_key_that_turned_wrong_before_its_time_cancels_the_good_one_that_was_waiting()
    {
        var vm = Create();

        vm.GeminiKey.Entry = ClearKey;
        vm.GeminiKey.Entry = ClearKey + " oops";
        _rig.Rest();

        Assert.Null(_rig.OnDisk().Correction.GeminiKeyProtected);
    }

    [Fact]
    public void Trimming_cuts_the_blanks_around_a_pasted_key()
    {
        var vm = Create();

        vm.GeminiKey.Entry = "  " + ClearKey + "\r\n";
        _rig.Rest();

        Assert.Equal(ClearKey, _rig.OnDisk().GetGeminiKey(_rig.Protector));
    }

    // ---- fixed rules ----

    [Fact]
    public void The_rules_are_saved_600_ms_after_the_last_key_and_read_once_for_that_saved_change()
    {
        var vm = Create();

        vm.RulesText = "f";
        vm.RulesText = "fa";
        vm.RulesText = "farol acesso =";
        vm.RulesText = "farol acesso => farol aceso";

        Assert.Empty(_inspected); // not once per keystroke
        _rig.Clock.Advance(TimeSpan.FromMilliseconds(599));
        Assert.Empty(_inspected);
        _rig.Clock.Advance(TimeSpan.FromMilliseconds(1));

        Assert.Equal(["farol acesso => farol aceso"], _inspected);
        Assert.Equal("farol acesso => farol aceso", _rig.OnDisk().Correction.FixedRules);
        Assert.Equal("1 regra", vm.RulesNote);
    }

    [Fact]
    public void An_invalid_line_is_shown_as_ignored_and_a_repeat_counts_once()
    {
        var vm = Create();

        vm.RulesText = "farol acesso => farol aceso\nlixo sem seta\nFAROL ACESSO -> outro\nvidro abrto = vidro aberto";
        _rig.Rest();

        Assert.Equal("2 regras \U000000B7 1 linha ignorada (linha 2) \U000000B7 1 repetida (linha 3) conta uma vez", vm.RulesNote);
        Assert.True(vm.RulesNoteIsWarning);
        Assert.True(vm.HasRulesNote);
        Assert.Equal(2, FixedRules.Parse(_rig.Settings.Current.Correction.FixedRules).Count);
    }

    [Fact]
    public void Many_ignored_lines_are_listed_briefly_and_a_text_with_no_rule_says_so()
    {
        var vm = Create();

        vm.RulesText = "a\nb\nc\nd\ne\nf\ng";
        _rig.Rest();

        Assert.StartsWith("Nenhuma regra v\U000000E1lida \U000000B7 7 linhas ignoradas (linhas 1, 2, 3, 4, 5...)", vm.RulesNote);
        Assert.True(vm.RulesNoteIsWarning);
    }

    [Fact]
    public void An_emptied_box_has_no_note_and_clears_the_rules()
    {
        var vm = Create();
        vm.RulesText = "a => b";
        _rig.Rest();

        vm.RulesText = string.Empty;
        _rig.Rest();

        Assert.Equal(string.Empty, vm.RulesNote);
        Assert.False(vm.HasRulesNote);
        Assert.Equal(string.Empty, _rig.OnDisk().Correction.FixedRules);
    }

    [Fact]
    public void Typing_never_parses_the_rules_even_for_a_long_session()
    {
        var parses = 0;
        var vm = Create(text =>
        {
            parses++;
            return FixedRules.Inspect(text);
        });

        for (var i = 1; i <= 50; i++)
        {
            vm.RulesText = new string('x', i) + " => y";
            _rig.Clock.Advance(TimeSpan.FromMilliseconds(100));
        }

        Assert.Equal(0, parses);
        _rig.Rest();
        Assert.Equal(1, parses);
    }

    // ---- the Gemini dot ----

    [Fact]
    public void The_gemini_dot_is_blue_offline_when_the_pipeline_reports_it_unhealthy()
    {
        var vm = Create();
        _rig.Hub.SetCorrectionMode(CorrectionMode.Gemini);
        _rig.Hub.SetGemini(online: true);
        Assert.Equal(StatusLight.Green, vm.GeminiLight);
        Assert.Equal("Online", vm.GeminiText);

        _rig.Hub.SetGemini(online: false);

        Assert.Equal(StatusLight.Blue, vm.GeminiLight);
        Assert.Equal("Offline", vm.GeminiText);
        Assert.StartsWith("Gemini: Offline", vm.GeminiTip, StringComparison.Ordinal);
    }

    [Fact]
    public void The_gemini_pill_reads_Sem_chave_without_a_key_and_Verificando_while_the_first_probe_runs()
    {
        var vm = Create();
        _rig.Hub.SetCorrectionMode(CorrectionMode.Gemini);

        _rig.Hub.SetGeminiChecking(true);
        Assert.Equal((StatusLight.Yellow, "Verificando"), (vm.GeminiLight, vm.GeminiText));

        _rig.Hub.SetGeminiKeyMissing(true);
        Assert.Equal((StatusLight.Gray, "Sem chave"), (vm.GeminiLight, vm.GeminiText));
        Assert.Equal("Gemini: Sem chave\nChave do Gemini n\U000000E3o configurada", vm.GeminiTip);

        _rig.Hub.SetGeminiKeyMissing(false);
        _rig.Hub.SetGeminiProbe(online: false, "HTTP 403 PERMISSION_DENIED", DateTimeOffset.UtcNow);
        Assert.Equal((StatusLight.Blue, "Offline"), (vm.GeminiLight, vm.GeminiText));
        Assert.Contains("Offline: HTTP 403 PERMISSION_DENIED", vm.GeminiTip, StringComparison.Ordinal);
    }

    [Fact]
    public void The_gemini_dot_is_blue_when_only_offline_is_chosen_and_gray_when_the_correction_is_off()
    {
        var vm = Create();

        _rig.Hub.SetCorrectionMode(CorrectionMode.OfflineOnly);
        Assert.Equal(StatusLight.Blue, vm.GeminiLight);

        _rig.Hub.SetCorrectionMode(CorrectionMode.Disabled);
        Assert.Equal(StatusLight.Gray, vm.GeminiLight);
    }

    [Fact]
    public void Nothing_in_the_view_model_logs_or_prints_the_key()
    {
        var vm = Create();

        vm.GeminiKey.Entry = ClearKey;
        _rig.Rest();

        Assert.DoesNotContain(ClearKey, vm.GeminiKey.ToString());
        Assert.DoesNotContain(ClearKey, vm.GeminiTip);
        Assert.DoesNotContain(ClearKey, vm.RulesNote);
        Assert.DoesNotContain(ClearKey, _rig.Saver.Label);
        Assert.Empty(_rig.Errors);
    }
}
