using AlertaIBT.App.Platform;
using AlertaIBT.App.ViewModels;
using AlertaIBT.Core.Diagnostics;
using AlertaIBT.Core.Input;
using AlertaIBT.Core.Settings;

namespace AlertaIBT.App.Tests.ViewModels;

// The "bloqueador de mouse" rows of Configuracoes > Sistema: the switch, its shortcut and Ctrl x3.
public sealed class SettingsViewModelMouseLockTests : IDisposable
{
    private const HotkeyModifiers CtrlAlt = HotkeyModifiers.Control | HotkeyModifiers.Alt;

    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        foreach (var item in _disposables)
        {
            item.Dispose();
        }
    }

    private (SettingsRig Rig, SettingsViewModel Vm) Create(AppSettings? initial = null)
    {
        var rig = new SettingsRig(initial);
        var vm = new SettingsViewModel(
            rig.Settings,
            rig.Protector,
            rig.Hub,
            rig.Saver,
            new DiagnosticsViewModel(new LogFiles(Path.Combine(rig.Folder, "logs")), _ => { }, () => null),
            () => Task.CompletedTask);
        _disposables.Add(vm);
        _disposables.Add(rig);
        return (rig, vm);
    }

    [Fact]
    public void It_starts_with_the_lock_off_Ctrl_Alt_M_and_Ctrl_x3_on()
    {
        var (_, vm) = Create();

        Assert.False(vm.MouseLockEnabled);
        Assert.True(vm.TripleCtrl);
        Assert.Equal("Ctrl+Alt+M", vm.MouseLockHotkeyDisplay);
        Assert.False(vm.HasMouseLockHotkeyHint);
        Assert.False(vm.HasMouseLockHotkeyProblem);
    }

    [Fact]
    public void It_shows_what_the_settings_hold()
    {
        var (_, vm) = Create(new AppSettings { MouseLock = new MouseLockSettings { Enabled = true, Hotkey = "Ctrl+Shift+F9", TripleCtrl = false } });

        Assert.True(vm.MouseLockEnabled);
        Assert.False(vm.TripleCtrl);
        Assert.Equal("Ctrl+Shift+F9", vm.MouseLockHotkeyDisplay);
    }

    [Fact]
    public void An_unusable_saved_shortcut_shows_as_the_default_that_the_shell_will_apply()
    {
        var (_, vm) = Create(new AppSettings { MouseLock = new MouseLockSettings { Hotkey = "Ctrl+V" } });

        Assert.Equal("Ctrl+Alt+M", vm.MouseLockHotkeyDisplay);
    }

    [Fact]
    public void The_lock_switch_takes_effect_at_once_without_waiting_for_the_autosave()
    {
        var (rig, vm) = Create();
        var announced = new List<bool>();
        rig.Settings.Changed += s => announced.Add(s.MouseLock.Enabled);

        vm.MouseLockEnabled = true;

        Assert.True(rig.Settings.Current.MouseLock.Enabled); // the shell follows this
        Assert.Equal([true], announced);
        Assert.True(rig.OnDisk().MouseLock.Enabled); // and it is on disk already

        vm.MouseLockEnabled = false;
        Assert.False(rig.OnDisk().MouseLock.Enabled);
    }

    [Fact]
    public void The_Ctrl_x3_switch_saves_after_the_rest()
    {
        var (rig, vm) = Create();

        vm.TripleCtrl = false;
        rig.Rest();

        Assert.False(rig.OnDisk().MouseLock.TripleCtrl);
        Assert.False(rig.Settings.Current.MouseLock.Enabled); // the others are untouched
    }

    [Fact]
    public void Toggling_the_lock_elsewhere_the_shortcut_or_the_tray_moves_the_switch_without_saving_again()
    {
        var (rig, vm) = Create();
        var announced = 0;
        rig.Settings.Changed += _ => announced++;

        rig.Settings.Update(s => s with { MouseLock = s.MouseLock with { Enabled = true } });

        Assert.True(vm.MouseLockEnabled);
        Assert.Equal(1, announced); // the switch followed; it did not write a second time
        Assert.False(rig.Saver.HasPending);
    }

    [Fact]
    public void A_good_shortcut_is_shown_and_saved_and_the_old_hint_goes_away()
    {
        var (rig, vm) = Create();
        vm.TryCaptureMouseLockHotkey(HotkeyModifiers.Control, 0x56); // Ctrl+V: refused
        Assert.True(vm.HasMouseLockHotkeyHint);

        var ok = vm.TryCaptureMouseLockHotkey(CtrlAlt, 0x77); // Ctrl+Alt+F8
        rig.Rest();

        Assert.True(ok);
        Assert.False(vm.HasMouseLockHotkeyHint);
        Assert.Equal("Ctrl+Alt+F8", vm.MouseLockHotkeyDisplay);
        Assert.Equal("Ctrl+Alt+F8", rig.OnDisk().MouseLock.Hotkey);
    }

    [Theory]
    [InlineData(HotkeyModifiers.Control, 0x56)] // Ctrl+V
    [InlineData(HotkeyModifiers.Alt, 0x73)] // Alt+F4
    [InlineData(HotkeyModifiers.Win, 0x4C)] // Win+L
    [InlineData(HotkeyModifiers.None, 0x7B)] // F12 alone
    [InlineData(HotkeyModifiers.Shift, 0x2D)] // Shift+Insert
    public void A_reserved_or_unusable_shortcut_is_refused_with_the_same_hint_as_the_panel_one_and_not_saved(HotkeyModifiers modifiers, int key)
    {
        var (rig, vm) = Create();

        var ok = vm.TryCaptureMouseLockHotkey(modifiers, key);
        rig.Rest();

        Assert.False(ok);
        Assert.True(vm.HasMouseLockHotkeyHint);
        Assert.Equal(HotkeyPolicy.Hint(HotkeyGesture.Validate(modifiers, key)), vm.MouseLockHotkeyHint);
        Assert.Equal("Ctrl+Alt+M", rig.OnDisk().MouseLock.Hotkey);
        Assert.Equal("Ctrl+Alt+M", vm.MouseLockHotkeyDisplay);
    }

    [Fact]
    public void The_mouse_lock_shortcut_cannot_be_the_one_of_the_quick_panel_and_the_other_way_round()
    {
        var (rig, vm) = Create();

        Assert.False(vm.TryCaptureMouseLockHotkey(CtrlAlt, 0x20)); // Ctrl+Alt+Space belongs to the panel
        Assert.Contains("painel", vm.MouseLockHotkeyHint, StringComparison.Ordinal);

        Assert.False(vm.TryCaptureHotkey(CtrlAlt, 0x4D)); // Ctrl+Alt+M belongs to the lock
        Assert.Contains("trava o mouse", vm.HotkeyHint, StringComparison.Ordinal);

        rig.Rest();
        Assert.Equal("Ctrl+Alt+M", rig.OnDisk().MouseLock.Hotkey);
        Assert.Equal("Ctrl+Alt+Space", rig.OnDisk().System.Hotkey);
    }

    [Fact]
    public void Moving_the_panel_shortcut_frees_the_old_chord_for_the_lock()
    {
        var (_, vm) = Create();

        Assert.True(vm.TryCaptureHotkey(CtrlAlt, 0x77)); // panel to Ctrl+Alt+F8
        Assert.True(vm.TryCaptureMouseLockHotkey(CtrlAlt, 0x20)); // lock to Ctrl+Alt+Space
    }

    [Fact]
    public void The_shell_telling_that_the_lock_shortcut_is_taken_shows_in_the_tab_and_clears_when_it_works()
    {
        var (_, vm) = Create();

        vm.ApplyMouseLockHotkey(new HotkeyStatus("Ctrl+Alt+M", "Ctrl+Alt+M", false, "long", "Atalho Ctrl+Alt+M em uso por outro programa"));
        Assert.True(vm.HasMouseLockHotkeyProblem);
        Assert.Equal("Atalho Ctrl+Alt+M em uso por outro programa. Escolha outro no campo acima.", vm.MouseLockHotkeyProblem);
        Assert.False(vm.HasHotkeyProblem); // the panel's own line is a different one

        vm.ApplyMouseLockHotkey(new HotkeyStatus("Ctrl+Alt+M", "Ctrl+Alt+M", true, null));
        Assert.False(vm.HasMouseLockHotkeyProblem);
    }

    [Fact]
    public void Disposing_stops_following_the_settings()
    {
        var (rig, vm) = Create();
        vm.Dispose();

        rig.Settings.Update(s => s with { MouseLock = s.MouseLock with { Enabled = true } });

        Assert.False(vm.MouseLockEnabled);
    }
}
