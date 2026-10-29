using AlertaIBT.App.ViewModels;
using AlertaIBT.Core.Diagnostics;
using AlertaIBT.Core.Services;
using AlertaIBT.Core.Settings;
using Microsoft.Extensions.Time.Testing;

namespace AlertaIBT.App.Tests.ViewModels;

public sealed class MainViewModelTests : IDisposable
{
    private readonly SettingsRig _rig = new();
    private readonly FakeGateway _gateway;
    private readonly MainViewModel _vm;

    public MainViewModelTests()
    {
        _gateway = new FakeGateway(_rig.Clock);
        var composer = new QuickPanelViewModel(_gateway, _rig.Hub, clock: _rig.Clock);
        var home = new HomeViewModel(_gateway, composer, clock: _rig.Clock);
        var correction = new CorrectionViewModel(_rig.Settings, _rig.Protector, _rig.Hub, _rig.Saver);
        var diagnostics = new DiagnosticsViewModel(new LogFiles(Path.Combine(_rig.Folder, "logs")), _ => { }, () => null);
        var settings = new SettingsViewModel(_rig.Settings, _rig.Protector, _rig.Hub, _rig.Saver, diagnostics, () => Task.CompletedTask);
        _vm = new MainViewModel(_rig.Hub, home, correction, settings, _rig.Saver);
    }

    public void Dispose()
    {
        _vm.Dispose();
        _rig.Dispose();
    }

    [Fact]
    public void It_opens_on_Inicio_and_the_tabs_switch()
    {
        Assert.Equal(MainTab.Home, _vm.SelectedTab);
        Assert.True(_vm.IsHomeSelected);

        _vm.SelectTabCommand.Execute(MainTab.Som);
        Assert.True(_vm.IsSomSelected);
        Assert.False(_vm.IsHomeSelected);

        _vm.SelectTabCommand.Execute(MainTab.Ndi);
        Assert.True(_vm.IsNdiSelected);
        Assert.False(_vm.IsSomSelected);

        _vm.SelectTabCommand.Execute(MainTab.Settings);
        Assert.True(_vm.IsSettingsSelected);
        Assert.False(_vm.IsSomSelected);

        // The correction is not a tab any more: it lives inside Inicio.
        Assert.Equal(["Home", "Som", "Ndi", "Settings"], Enum.GetNames<MainTab>());
    }

    [Fact]
    public void The_side_bar_lists_telegram_site_led_barra_and_gemini_in_that_order()
    {
        Assert.Equal(["Telegram", "Site", "LED", "Barra", "Gemini"], _vm.Status.Select(r => r.Label));
    }

    [Fact]
    public void The_dots_follow_the_status_hub()
    {
        _rig.Hub.SetTelegram(AlertaIBT.Core.Telegram.TelegramState.Online);
        _rig.Hub.SetLedPresent(true);
        _rig.Hub.SetCaptureExcluded(true);

        Assert.Equal(StatusLight.Green, _vm.Status[0].Light); // Telegram
        Assert.Equal(StatusLight.Gray, _vm.Status[1].Light); // Site: not configured yet
        Assert.Equal(StatusLight.Green, _vm.Status[2].Light); // LED
        Assert.Equal(StatusLight.Green, _vm.Status[3].Light); // Barra

        _rig.Hub.SetPaused(true);
        Assert.Equal(StatusLight.Yellow, _vm.Status[3].Light);

        _rig.Hub.SetLedPresent(false);
        Assert.Equal(StatusLight.Red, _vm.Status[2].Light);
    }

    [Fact]
    public void Gemini_turns_blue_and_reads_Offline_when_the_pipeline_is_unhealthy()
    {
        _rig.Hub.SetCorrectionMode(CorrectionMode.Gemini);
        _rig.Hub.SetGemini(online: true);
        Assert.Equal(("Gemini", StatusLight.Green), (_vm.Status[4].Label, _vm.Status[4].Light));

        _rig.Hub.SetGemini(online: false);

        Assert.Equal(("Offline", StatusLight.Blue), (_vm.Status[4].Label, _vm.Status[4].Light));
        Assert.StartsWith("Gemini: Offline", _vm.Status[4].Tip, StringComparison.Ordinal);

        _rig.Hub.SetGemini(online: true);
        Assert.Equal("Gemini", _vm.Status[4].Label);
    }

    [Fact]
    public void Gemini_without_a_key_is_a_gray_Sem_chave_line_with_its_tooltip()
    {
        _rig.Hub.SetCorrectionMode(CorrectionMode.Gemini);
        _rig.Hub.SetGeminiKeyMissing(true);

        Assert.Equal(("Gemini", StatusLight.Gray), (_vm.Status[4].Label, _vm.Status[4].Light));
        Assert.Equal("Gemini: Sem chave\nChave do Gemini n\U000000E3o configurada", _vm.Status[4].Tip);
    }

    [Fact]
    public void Gemini_is_yellow_while_the_first_probe_runs_then_the_probe_decides_with_its_reason_and_age()
    {
        _rig.Hub.SetCorrectionMode(CorrectionMode.Gemini);
        _rig.Hub.SetGeminiChecking(true);
        Assert.Equal(("Gemini", StatusLight.Yellow), (_vm.Status[4].Label, _vm.Status[4].Light));
        Assert.StartsWith("Gemini: Verificando", _vm.Status[4].Tip, StringComparison.Ordinal);

        _rig.Hub.SetGeminiProbe(online: true, reason: null, DateTimeOffset.UtcNow);
        Assert.Equal(StatusLight.Green, _vm.Status[4].Light);
        Assert.Equal("Gemini: Online\nGemini conectado agora", _vm.Status[4].Tip);

        _rig.Hub.SetGeminiProbe(online: false, "tempo esgotado", DateTimeOffset.UtcNow);
        Assert.Equal(("Offline", StatusLight.Blue), (_vm.Status[4].Label, _vm.Status[4].Light));
        Assert.StartsWith("Gemini: Offline\nOffline: tempo esgotado (verificado agora)", _vm.Status[4].Tip, StringComparison.Ordinal);
    }

    [Fact]
    public void Each_line_has_a_tooltip_with_the_state_and_the_detail()
    {
        _rig.Hub.SetLedPresent(false);

        var led = _vm.Status[2];

        Assert.StartsWith("LED: Desligado\n", led.Tip, StringComparison.Ordinal);
        Assert.Contains("2560x768", led.Tip);
    }

    [Fact]
    public void Changes_from_other_threads_reach_the_list_through_the_post_function()
    {
        var posted = new List<Action>();
        using var rig = new SettingsRig();
        var composer = new QuickPanelViewModel(_gateway, rig.Hub, clock: rig.Clock);
        var home = new HomeViewModel(_gateway, composer, clock: rig.Clock);
        var correction = new CorrectionViewModel(rig.Settings, rig.Protector, rig.Hub, rig.Saver);
        var diagnostics = new DiagnosticsViewModel(new LogFiles(Path.Combine(rig.Folder, "logs")), _ => { }, () => null);
        var settings = new SettingsViewModel(rig.Settings, rig.Protector, rig.Hub, rig.Saver, diagnostics, () => Task.CompletedTask);
        using var vm = new MainViewModel(rig.Hub, home, correction, settings, rig.Saver, posted.Add);
        var before = vm.Status[2];

        rig.Hub.SetLedPresent(true);

        Assert.Equal(before, vm.Status[2]);
        Assert.NotEmpty(posted);
        posted.ForEach(a => a());
        Assert.Equal(StatusLight.Green, vm.Status[2].Light);
    }

    [Fact]
    public void Disposing_saves_what_was_still_waiting()
    {
        _vm.Settings.BarHeight = 100;
        Assert.Equal(56, _rig.OnDisk().Bar.Height);

        _vm.Dispose();

        Assert.Equal(100, _rig.OnDisk().Bar.Height);
    }

    [Fact]
    public void Both_settings_tabs_share_one_save_stamp()
    {
        Assert.Same(_vm.Saver, _vm.Correction.Saver);
        Assert.Same(_vm.Saver, _vm.Settings.Saver);

        _vm.Correction.Enabled = false;
        Assert.Equal(SaveState.Saving, _vm.Settings.Saver.State);
        _rig.Rest();
        Assert.Equal(SaveState.Saved, _vm.Correction.Saver.State);
    }
}
