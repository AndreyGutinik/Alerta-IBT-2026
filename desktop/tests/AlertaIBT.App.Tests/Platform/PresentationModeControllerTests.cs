using AlertaIBT.App.Platform;
using AlertaIBT.App.Tests.ViewModels;
using AlertaIBT.Core.Holyrics;
using AlertaIBT.Core.Settings;

namespace AlertaIBT.App.Tests.Platform;

// The presentation mode, with a fake hook (no key is ever injected or swallowed on this PC) and a fake Holyrics.
public sealed class PresentationModeControllerTests
{
    private const int PageDown = 0x22;
    private const int PageUp = 0x21;
    private const int Right = 0x27;
    private const int LetterB = 0x42;

    [Fact]
    public void It_is_off_at_the_start_with_no_hook()
    {
        using var rig = new PresentationRig();

        Assert.False(rig.Controller.IsOn);
        Assert.False(rig.Gate.IsInstalled);
        Assert.False(rig.Gate.Press(PageDown, true)); // no hook: the key goes on
    }

    [Fact]
    public void It_is_never_saved_as_on()
    {
        using var rig = new PresentationRig();

        rig.Controller.SetOn(true);
        rig.Chrome.Settings.Update(s => s);

        Assert.DoesNotContain("presentationOn", SettingsJson.Save(rig.Chrome.Settings.Current), StringComparison.OrdinalIgnoreCase);
        using var again = new PresentationRig();
        Assert.False(again.Controller.IsOn);
    }

    [Fact]
    public void Turning_on_without_a_token_is_refused_and_consumes_nothing()
    {
        using var rig = new PresentationRig(configured: false);

        var result = rig.Controller.SetOn(true);

        Assert.False(result.Success);
        Assert.Equal(PresentationModeController.NotConfiguredText, result.Message);
        Assert.Equal(PresentationModeController.NotConfiguredText, rig.Controller.Reason);
        Assert.False(rig.Controller.IsOn);
        Assert.False(rig.Gate.IsInstalled);
        Assert.False(rig.Controller.CanTurnOn);
    }

    [Fact]
    public void Turning_on_installs_the_hook_and_the_chosen_keys_are_swallowed_and_become_slide_commands()
    {
        using var rig = new PresentationRig();
        var changes = 0;
        rig.Controller.StateChanged += () => changes++;

        Assert.True(rig.Controller.SetOn(true).Success);

        Assert.True(rig.Controller.IsOn);
        Assert.True(rig.Gate.IsInstalled);
        Assert.True(changes > 0);

        Assert.True(rig.Gate.Press(PageDown, true));
        Assert.True(rig.Gate.Press(PageDown, false)); // the up is swallowed too
        Assert.True(rig.Gate.Press(PageUp, true));
        Assert.True(rig.Gate.Press(PageUp, false));
        Assert.True(rig.Gate.Press(Right, true));
        Assert.True(rig.Gate.Press(Right, false));

        Assert.True(rig.Remote.WaitFor(3));
        Assert.Equal(
            [HolyricsActionKind.SlideNext, HolyricsActionKind.SlidePrevious, HolyricsActionKind.SlideNext],
            rig.Remote.Posted.Select(c => c.Kind));
    }

    [Fact]
    public void Other_keys_a_modifier_and_the_own_window_pass_through()
    {
        using var rig = new PresentationRig();
        rig.Controller.SetOn(true);

        Assert.False(rig.Gate.Press(LetterB, true));
        Assert.False(rig.Gate.Press(LetterB, false));

        rig.ModifierHeld = true; // Ctrl+Right in another program keeps working
        Assert.False(rig.Gate.Press(Right, true));
        Assert.False(rig.Gate.Press(Right, false));
        rig.ModifierHeld = false;

        rig.OwnForeground = true; // the quick panel handles its own arrows
        Assert.False(rig.Gate.Press(Right, true));
        Assert.False(rig.Gate.Press(Right, false));

        Thread.Sleep(50);
        Assert.Empty(rig.Remote.Posted);
    }

    [Fact]
    public void A_held_key_sends_one_command()
    {
        using var rig = new PresentationRig();
        rig.Controller.SetOn(true);

        for (var i = 0; i < 6; i++)
        {
            Assert.True(rig.Gate.Press(PageDown, true));
        }

        Assert.True(rig.Gate.Press(PageDown, false));
        Assert.True(rig.Remote.WaitFor(1));
        Thread.Sleep(50);
        Assert.Single(rig.Remote.Posted);
    }

    [Fact]
    public void Turning_off_releases_the_hook_and_the_keys_behave_normally()
    {
        using var rig = new PresentationRig();
        rig.Controller.SetOn(true);

        Assert.True(rig.Controller.SetOn(false).Success);

        Assert.False(rig.Controller.IsOn);
        Assert.False(rig.Gate.IsInstalled);
        Assert.False(rig.Gate.Press(PageDown, true));
    }

    [Fact]
    public void Turning_off_while_a_key_is_held_never_leaves_it_stuck()
    {
        using var rig = new PresentationRig();
        rig.Controller.SetOn(true);
        rig.Gate.Press(PageDown, true);

        rig.Controller.SetOn(false);

        // With the hook gone the up reaches Windows; a hook installed again starts with nothing held.
        Assert.False(rig.Gate.Press(PageDown, false));
        rig.Controller.SetOn(true);
        Assert.False(rig.Gate.Press(PageDown, false));
    }

    [Fact]
    public void A_hook_that_windows_refuses_leaves_the_mode_off_with_the_reason()
    {
        using var rig = new PresentationRig();
        rig.Gate.AcceptInstall = false;

        var result = rig.Controller.SetOn(true);

        Assert.False(result.Success);
        Assert.False(rig.Controller.IsOn);
        Assert.Contains("gancho", rig.Controller.Reason);
    }

    [Fact]
    public void A_hook_that_cannot_be_renewed_turns_the_mode_off_and_says_why()
    {
        using var rig = new PresentationRig();
        rig.Controller.SetOn(true);

        rig.Chrome.Clock.Advance(PresentationModeController.RefreshInterval);
        Assert.True(rig.Gate.Refreshes >= 1);
        Assert.True(rig.Controller.IsOn); // a successful refresh changes nothing

        rig.Gate.AcceptRefresh = false;
        rig.Chrome.Clock.Advance(PresentationModeController.RefreshInterval);

        Assert.False(rig.Controller.IsOn);
        Assert.False(rig.Gate.IsInstalled);
        Assert.Contains("removeu o gancho", rig.Controller.Reason);
    }

    [Fact]
    public void Removing_the_token_while_on_turns_the_mode_off()
    {
        using var rig = new PresentationRig();
        rig.Controller.SetOn(true);

        rig.Remote.IsConfigured = false;
        rig.Chrome.Settings.Update(s => s with { Holyrics = s.Holyrics with { TokenProtected = null } });

        Assert.False(rig.Controller.IsOn);
        Assert.False(rig.Gate.IsInstalled);
        Assert.Equal(PresentationModeController.NotConfiguredText, rig.Controller.Reason);
    }

    [Fact]
    public void Changed_keys_apply_at_once_while_on()
    {
        using var rig = new PresentationRig();
        rig.Controller.SetOn(true);

        rig.Chrome.Settings.Update(s => s with { Holyrics = s.Holyrics with { PresentationNext = [LetterB], PresentationPrevious = [] } });

        Assert.True(rig.Gate.Press(LetterB, true));
        Assert.False(rig.Gate.Press(PageDown, true));
        Assert.False(rig.Gate.Press(PageUp, true));
    }

    [Fact]
    public void Toggle_flips_the_mode_and_the_breadcrumbs_have_no_key()
    {
        using var rig = new PresentationRig();

        rig.Controller.Toggle();
        Assert.True(rig.Controller.IsOn);
        rig.Controller.Toggle();
        Assert.False(rig.Controller.IsOn);
    }

    [Fact]
    public void Dispose_lets_go_of_the_hook()
    {
        var rig = new PresentationRig();
        rig.Controller.SetOn(true);

        rig.Controller.Dispose();

        Assert.False(rig.Gate.IsInstalled);
        Assert.False(rig.Controller.SetOn(true).Success);
        rig.Dispose();
    }

    [Fact]
    public void The_shortcut_is_Ctrl_Alt_P_and_a_conflict_gives_a_warning_without_failing()
    {
        using var free = new PresentationRig();
        Assert.Equal(["Ctrl+Alt+P"], free.Registrar.Registered);
        Assert.True(free.Controller.HotkeyStatus.Registered);
        Assert.Null(free.Controller.HotkeyStatus.Message);

        var taken = new PresentationRig();
        using (taken)
        {
            // A second controller on the same settings, over a registrar that Windows refuses.
            var registrar = new FakeRegistrar { Accept = false };
            using var other = new PresentationModeController(taken.Chrome.Settings, taken.Remote, new FakeKeyboardGate(), work => work());
            HotkeyStatus? seen = null;
            other.HotkeyStatusChanged += s => seen = s;

            other.Start(registrar);

            Assert.False(other.HotkeyStatus.Registered);
            Assert.Contains("Ctrl+Alt+P", other.HotkeyStatus.Message);
            Assert.Contains("outro programa", other.HotkeyStatus.Message);
            Assert.NotNull(seen);
            other.Toggle(); // the mode still works without the shortcut
            Assert.True(other.IsOn);
        }
    }
}
