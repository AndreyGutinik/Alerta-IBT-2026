using AlertaIBT.App.ViewModels;
using AlertaIBT.Core.Holyrics;

namespace AlertaIBT.App.Tests.ViewModels;

// The presentation part of the Holyrics card: the switch, the key chips, "adicionar tecla", "restaurar padrao".
public sealed class ControlPresentationViewModelTests
{
    private static int[] Keys(System.Collections.ObjectModel.ObservableCollection<PresentationKeyChip> chips) => chips.Select(c => c.VirtualKey).ToArray();

    [Fact]
    public void It_starts_off_with_the_default_chips()
    {
        using var rig = new PresentationRig();
        var vm = rig.Vm;

        Assert.True(vm.HasPresentation);
        Assert.False(vm.PresentationOn);
        Assert.Equal([0x22, 0x27], Keys(vm.PresentationNextKeys));
        Assert.Equal([0x21, 0x25], Keys(vm.PresentationPreviousKeys));
        Assert.Equal(["PgDn", "\U00002192"], vm.PresentationNextKeys.Select(c => c.Label));
        Assert.All(vm.PresentationNextKeys.Concat(vm.PresentationPreviousKeys), c => Assert.False(string.IsNullOrEmpty(c.Tip)));
    }

    [Fact]
    public void The_switch_turns_the_mode_on_and_off()
    {
        using var rig = new PresentationRig();
        var vm = rig.Vm;
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.PresentationOn = true;

        Assert.True(rig.Controller.IsOn);
        Assert.True(vm.PresentationOn);
        Assert.Contains(nameof(vm.PresentationOn), raised);
        Assert.True(rig.Gate.IsInstalled);

        vm.PresentationOn = false;
        Assert.False(rig.Controller.IsOn);
        Assert.False(rig.Gate.IsInstalled);
    }

    [Fact]
    public void Without_a_token_the_switch_is_disabled_with_a_tip_and_turning_on_is_refused()
    {
        using var rig = new PresentationRig(configured: false);
        var vm = rig.Vm;

        Assert.False(vm.PresentationSwitchEnabled);
        Assert.Contains("token", vm.PresentationTip);

        vm.PresentationOn = true;

        Assert.False(vm.PresentationOn);
        Assert.False(rig.Gate.IsInstalled);
        Assert.True(vm.HasPresentationReason);
        Assert.Contains("token", vm.PresentationReasonText);
    }

    [Fact]
    public void Filling_the_token_enables_the_switch()
    {
        using var rig = new PresentationRig(configured: false);
        var vm = rig.Vm;

        rig.Remote.IsConfigured = true;
        rig.Chrome.Settings.Update(s => s); // any change: the card looks again

        Assert.True(vm.PresentationSwitchEnabled);
    }

    [Fact]
    public void A_hook_that_was_lost_shows_the_reason_in_the_card()
    {
        using var rig = new PresentationRig();
        rig.Vm.PresentationOn = true;

        rig.Gate.AcceptRefresh = false;
        rig.Chrome.Clock.Advance(PresentationModeControllerRefresh);

        Assert.False(rig.Vm.PresentationOn);
        Assert.Contains("gancho", rig.Vm.PresentationReasonText);
    }

    private static TimeSpan PresentationModeControllerRefresh => AlertaIBT.App.Platform.PresentationModeController.RefreshInterval;

    [Fact]
    public void A_shortcut_in_use_by_another_program_shows_its_warning_line()
    {
        using var rig = new PresentationRig();
        using var taken = new PresentationRig();
        var registrar = new FakeRegistrar { Accept = false };
        var controller = new AlertaIBT.App.Platform.PresentationModeController(rig.Chrome.Settings, rig.Remote, new FakeKeyboardGate(), work => work());
        controller.Start(registrar);
        using var vm = new ControlViewModel(rig.Chrome.Settings, rig.Service, (_, _) => true, presentation: controller);

        Assert.True(vm.HasPresentationReason);
        Assert.Contains("Ctrl+Alt+P", vm.PresentationReasonText);
        controller.Dispose();
    }

    [Fact]
    public void A_key_is_added_to_a_set_saved_and_shown()
    {
        using var rig = new PresentationRig();
        var vm = rig.Vm;

        Assert.True(vm.AddPresentationKey(next: true, 0x42));

        Assert.Equal([0x22, 0x27, 0x42], Keys(vm.PresentationNextKeys));
        Assert.Equal([0x22, 0x27, 0x42], rig.Chrome.Settings.Current.Holyrics.PresentationNext);
    }

    [Fact]
    public void A_key_that_cannot_be_used_is_refused_with_a_note()
    {
        using var rig = new PresentationRig();
        var vm = rig.Vm;

        Assert.False(vm.AddPresentationKey(next: true, 0x11)); // Ctrl
        Assert.True(vm.HasPresentationKeyHint);

        Assert.False(vm.AddPresentationKey(next: true, 0x22)); // already there
        Assert.Contains("j\U000000E1 est\U000000E1", vm.PresentationKeyHint);

        Assert.False(vm.AddPresentationKey(next: true, 0x21)); // belongs to Anterior
        Assert.Contains("Anterior", vm.PresentationKeyHint);
        Assert.Equal([0x22, 0x27], Keys(vm.PresentationNextKeys));
    }

    [Fact]
    public void A_set_holds_at_most_eight_keys()
    {
        using var rig = new PresentationRig();
        var vm = rig.Vm;
        foreach (var key in new[] { 0x41, 0x42, 0x43, 0x44, 0x45, 0x46 })
        {
            Assert.True(vm.AddPresentationKey(next: true, key));
        }

        Assert.Equal(8, vm.PresentationNextKeys.Count);
        Assert.False(vm.AddPresentationKey(next: true, 0x47));
        Assert.Equal(8, vm.PresentationNextKeys.Count);
    }

    [Fact]
    public void A_chip_removes_its_key_and_a_set_can_be_emptied()
    {
        using var rig = new PresentationRig();
        var vm = rig.Vm;

        vm.PresentationNextKeys[0].RemoveCommand.Execute(null);
        Assert.Equal([0x27], Keys(vm.PresentationNextKeys));

        vm.PresentationNextKeys[0].RemoveCommand.Execute(null);
        Assert.Empty(vm.PresentationNextKeys);
        Assert.Empty(rig.Chrome.Settings.Current.Holyrics.PresentationNext);
    }

    [Fact]
    public void Add_key_asks_the_recorder_and_cancelling_changes_nothing()
    {
        using var rig = new PresentationRig();
        var vm = rig.Vm;
        var asked = new List<string>();
        int? answer = 0x42;
        vm.KeyRecorder = name =>
        {
            asked.Add(name);
            return answer;
        };

        vm.AddNextKeyCommand.Execute(null);
        answer = null;
        vm.AddPreviousKeyCommand.Execute(null);

        Assert.Equal(["Pr\U000000F3ximo", "Anterior"], asked);
        Assert.Equal([0x22, 0x27, 0x42], Keys(vm.PresentationNextKeys));
        Assert.Equal([0x21, 0x25], Keys(vm.PresentationPreviousKeys));
    }

    [Fact]
    public void Add_key_without_a_recorder_does_nothing()
    {
        using var rig = new PresentationRig();

        rig.Vm.AddNextKeyCommand.Execute(null);

        Assert.Equal(2, rig.Vm.PresentationNextKeys.Count);
    }

    [Fact]
    public void Restore_puts_the_default_sets_back()
    {
        using var rig = new PresentationRig();
        var vm = rig.Vm;
        vm.AddPresentationKey(next: false, 0x42);
        vm.PresentationNextKeys[0].RemoveCommand.Execute(null);

        vm.RestorePresentationKeysCommand.Execute(null);

        Assert.Equal([0x22, 0x27], Keys(vm.PresentationNextKeys));
        Assert.Equal([0x21, 0x25], Keys(vm.PresentationPreviousKeys));
        Assert.False(vm.HasPresentationKeyHint);
    }

    [Fact]
    public void Chosen_keys_reach_the_hook_while_the_mode_is_on()
    {
        using var rig = new PresentationRig();
        rig.Vm.PresentationOn = true;

        rig.Vm.AddPresentationKey(next: true, 0x42);

        Assert.True(rig.Gate.Press(0x42, true));
        Assert.True(rig.Remote.WaitFor(1));
        Assert.Equal(HolyricsActionKind.SlideNext, rig.Remote.Posted[0].Kind);
    }
}
