using AlertaIBT.App.ViewModels;
using AlertaIBT.Core.Diagnostics;
using AlertaIBT.Core.Settings;

namespace AlertaIBT.App.Tests.ViewModels;

// "Limpar historico toda segunda-feira, 00:00" in Configuracoes > Sistema: on by default, saved 600 ms after the click.
public sealed class SettingsViewModelHistoryTests
{
    private static SettingsViewModel Create(SettingsRig rig) =>
        new(
            rig.Settings,
            rig.Protector,
            rig.Hub,
            rig.Saver,
            new DiagnosticsViewModel(new LogFiles(Path.Combine(rig.Folder, "logs")), _ => { }, () => null),
            () => Task.CompletedTask);

    [Fact]
    public void The_switch_is_on_by_default_and_loading_saves_nothing()
    {
        using var rig = new SettingsRig();
        using var vm = Create(rig);

        Assert.True(vm.ClearHistoryWeekly);
        Assert.False(rig.Saver.HasPending);
    }

    [Fact]
    public void Switching_it_off_saves_after_the_rest_and_only_that_value()
    {
        using var rig = new SettingsRig();
        using var vm = Create(rig);
        var lastClear = new DateTimeOffset(2026, 10, 19, 3, 0, 0, TimeSpan.Zero);
        rig.Settings.Update(s => s with { System = s.System with { LastHistoryClearAt = lastClear } });

        vm.ClearHistoryWeekly = false;
        Assert.True(rig.Saver.HasPending);
        rig.Rest();

        var saved = rig.OnDisk();
        Assert.False(saved.System.ClearHistoryWeekly);
        Assert.Equal(lastClear, saved.System.LastHistoryClearAt);
    }

    [Fact]
    public void A_saved_off_switch_shows_off_when_the_tab_is_built()
    {
        using var rig = new SettingsRig(new AppSettings { System = new SystemSettings { ClearHistoryWeekly = false } });
        using var vm = Create(rig);

        Assert.False(vm.ClearHistoryWeekly);
    }

    [Fact]
    public void Switching_it_back_on_is_saved_too()
    {
        using var rig = new SettingsRig(new AppSettings { System = new SystemSettings { ClearHistoryWeekly = false } });
        using var vm = Create(rig);

        vm.ClearHistoryWeekly = true;
        rig.Rest();

        Assert.True(rig.OnDisk().System.ClearHistoryWeekly);
    }
}
