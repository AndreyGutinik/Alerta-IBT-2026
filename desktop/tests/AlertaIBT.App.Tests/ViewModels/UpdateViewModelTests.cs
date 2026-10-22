using AlertaIBT.App.Platform;
using AlertaIBT.App.ViewModels;
using AlertaIBT.Core.Diagnostics;
using AlertaIBT.Core.Settings;

namespace AlertaIBT.App.Tests.ViewModels;

/// <summary>An update service that does nothing: the tests set its state and count the button presses.</summary>
internal sealed class FakeUpdateStatus : IUpdateStatus
{
    public string CurrentVersion { get; set; } = "8.0.1-pre.1";

    public UpdateState State { get; private set; } = new(UpdatePhase.Idle, null, "Ainda n\U000000E3o verificado");

    public int CheckNows { get; private set; }

    public Func<Task>? OnCheck { get; set; }

    public bool HasListeners => StateChanged is not null;

    public event Action<UpdateState>? StateChanged;

    public Task CheckNowAsync(CancellationToken cancellationToken)
    {
        CheckNows++;
        return OnCheck?.Invoke() ?? Task.CompletedTask;
    }

    public void Set(UpdateState state)
    {
        State = state;
        StateChanged?.Invoke(state);
    }
}

// The update block of Configuracoes > Sistema (the switch, the version, "Verificar agora", the last result) and the notice line of the window.
public sealed class UpdateViewModelTests
{
    private static SettingsViewModel Create(SettingsRig rig, FakeUpdateStatus? updates) =>
        new(
            rig.Settings,
            rig.Protector,
            rig.Hub,
            rig.Saver,
            new DiagnosticsViewModel(new LogFiles(Path.Combine(rig.Folder, "logs")), _ => { }, () => null),
            () => Task.CompletedTask,
            updates: updates);

    [Fact]
    public void The_switch_is_on_by_default_and_loading_saves_nothing()
    {
        using var rig = new SettingsRig();
        using var vm = Create(rig, new FakeUpdateStatus());

        Assert.True(vm.AutoUpdate);
        Assert.False(rig.Saver.HasPending);
    }

    [Fact]
    public void Switching_it_off_saves_after_the_rest_and_only_that_value()
    {
        using var rig = new SettingsRig();
        using var vm = Create(rig, new FakeUpdateStatus());
        var since = new DateTimeOffset(2026, 10, 22, 9, 0, 0, TimeSpan.Zero);
        rig.Settings.Update(s => s with { System = s.System with { UpdateAttemptVersion = "8.0.2", UpdateAttemptSince = since, UpdateAttempts = 1 } });

        vm.AutoUpdate = false;
        Assert.True(rig.Saver.HasPending);
        Assert.True(rig.OnDisk().System.AutoUpdate); // not yet: 600 ms
        rig.Rest();

        var saved = rig.OnDisk().System;
        Assert.False(saved.AutoUpdate);
        Assert.Equal("8.0.2", saved.UpdateAttemptVersion);
        Assert.Equal(since, saved.UpdateAttemptSince);
        Assert.Equal(1, saved.UpdateAttempts);
    }

    [Fact]
    public void A_saved_off_switch_shows_off_and_switching_back_on_is_saved()
    {
        using var rig = new SettingsRig(new AppSettings { System = new SystemSettings { AutoUpdate = false } });
        using var vm = Create(rig, new FakeUpdateStatus());
        Assert.False(vm.AutoUpdate);

        vm.AutoUpdate = true;
        rig.Rest();

        Assert.True(rig.OnDisk().System.AutoUpdate);
    }

    [Fact]
    public void The_version_and_the_last_result_come_from_the_update_service_and_follow_it()
    {
        using var rig = new SettingsRig();
        var updates = new FakeUpdateStatus();
        using var vm = Create(rig, updates);

        Assert.Equal("Vers\U000000E3o 8.0.1-pre.1", vm.VersionText);
        Assert.Equal("Ainda n\U000000E3o verificado", vm.UpdateResultText);

        updates.Set(new UpdateState(UpdatePhase.Idle, null, "Atualizado (8.0.1-pre.1) \U000000E0s 14:32"));
        Assert.Equal("Atualizado (8.0.1-pre.1) \U000000E0s 14:32", vm.UpdateResultText);

        updates.Set(new UpdateState(UpdatePhase.WaitingForIdle, null, "Nova vers\U000000E3o 8.0.1-pre.2 baixada, instalando quando ficar ocioso"));
        Assert.Contains("instalando quando ficar ocioso", vm.UpdateResultText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Verificar_agora_asks_the_service_once_and_is_off_while_it_works()
    {
        using var rig = new SettingsRig();
        var gate = new TaskCompletionSource();
        var updates = new FakeUpdateStatus { OnCheck = () => gate.Task };
        using var vm = Create(rig, updates);
        Assert.True(vm.CheckUpdateCommand.CanExecute(null));

        var running = vm.CheckUpdateCommand.ExecuteAsync(null);

        Assert.True(vm.IsCheckingUpdate);
        Assert.False(vm.CheckUpdateCommand.CanExecute(null));
        await vm.CheckUpdateCommand.ExecuteAsync(null); // a second press while it works does nothing
        Assert.Equal(1, updates.CheckNows);

        gate.SetResult();
        await running;

        Assert.False(vm.IsCheckingUpdate);
        Assert.True(vm.CheckUpdateCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_service_that_throws_never_escapes_the_button()
    {
        using var rig = new SettingsRig();
        var updates = new FakeUpdateStatus { OnCheck = () => throw new InvalidOperationException("boom") };
        using var vm = Create(rig, updates);

        await vm.CheckUpdateCommand.ExecuteAsync(null);

        Assert.False(vm.IsCheckingUpdate);
    }

    [Fact]
    public void Without_an_update_service_the_tab_still_works_and_disposing_lets_go_of_the_service()
    {
        using var rig = new SettingsRig();
        using (var plain = Create(rig, null))
        {
            Assert.StartsWith("Vers", plain.VersionText, StringComparison.Ordinal);
            Assert.Equal(string.Empty, plain.UpdateResultText);
            plain.CheckUpdateCommand.Execute(null);
        }

        var updates = new FakeUpdateStatus();
        var vm = Create(rig, updates);
        Assert.True(updates.HasListeners);
        vm.Dispose();
        Assert.False(updates.HasListeners);
    }

    [Fact]
    public void The_window_notice_follows_the_update_while_it_downloads_and_installs_and_goes_away()
    {
        using var rig = new SettingsRig();
        var updates = new FakeUpdateStatus();
        var gateway = new FakeGateway(rig.Clock);
        var home = new HomeViewModel(gateway, new QuickPanelViewModel(gateway, rig.Hub, clock: rig.Clock), clock: rig.Clock);
        var correction = new CorrectionViewModel(rig.Settings, rig.Protector, rig.Hub, rig.Saver);
        using var settings = Create(rig, updates);
        using var main = new MainViewModel(rig.Hub, home, correction, settings, rig.Saver, updates: updates);
        Assert.False(main.HasUpdateNotice);
        Assert.Equal(string.Empty, main.UpdateNotice);

        updates.Set(new UpdateState(UpdatePhase.Downloading, "Baixando a atualiza\U000000E7\U000000E3o 8.0.1-pre.2\U00002026", "x"));
        Assert.True(main.HasUpdateNotice);
        Assert.Equal("Baixando a atualiza\U000000E7\U000000E3o 8.0.1-pre.2\U00002026", main.UpdateNotice);

        updates.Set(new UpdateState(UpdatePhase.Installing, "Instalando e reiniciando\U00002026", "x"));
        Assert.Equal("Instalando e reiniciando\U00002026", main.UpdateNotice);

        updates.Set(new UpdateState(UpdatePhase.Idle, null, "Atualizado"));
        Assert.False(main.HasUpdateNotice);

        main.Dispose();
        Assert.False(updates.HasListeners);
    }
}
