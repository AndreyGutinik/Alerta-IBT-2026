using AlertaIBT.App.Platform;
using AlertaIBT.App.Tests.ViewModels;
using AlertaIBT.Core.Input;
using AlertaIBT.Core.Screens;
using AlertaIBT.Core.Settings;

namespace AlertaIBT.App.Tests.Platform;

// The mouse lock wired to the settings, the shortcut and the Ctrl x3 hook, all fakes: nothing here touches the real mouse or keyboard.
public sealed class MouseLockControllerTests : IDisposable
{
    private const int Ctrl = TripleCtrlDetector.VkLeftControl;

    private readonly FakeCursor _cursor = new();
    private readonly FakeKeyboardHook _hook = new();
    private readonly FakeHotkeyRegistrar _registrar = new();
    private readonly ListLogger _log = new();
    private SettingsRig? _rig;
    private MouseLockController? _controller;

    public void Dispose()
    {
        _controller?.Dispose();
        _rig?.Dispose();
    }

    private MouseLockController Start(MouseLockSettings? mouseLock = null)
    {
        _rig = new SettingsRig(mouseLock is null ? null : new AppSettings { MouseLock = mouseLock });
        var service = new MouseLockService(_cursor, _log);
        _controller = new MouseLockController(_rig.Settings, service, _hook, action => action(), _log);
        _controller.Start(_registrar);
        return _controller;
    }

    // ---- start ----

    [Fact]
    public void By_default_the_lock_is_off_the_shortcut_is_Ctrl_Alt_M_and_Ctrl_x3_is_listening()
    {
        var controller = Start();

        Assert.False(controller.IsLocked);
        Assert.Equal(0, _cursor.ClipCalls);
        Assert.Equal(["Ctrl+Alt+M"], _registrar.Registered);
        Assert.True(controller.HotkeyStatus.Registered);
        Assert.True(_hook.IsInstalled);
    }

    [Fact]
    public void A_lock_that_was_on_is_restored_at_start()
    {
        var controller = Start(new MouseLockSettings { Enabled = true });

        Assert.True(controller.IsLocked);
        Assert.Equal(new ScreenRect(0, 0, 1920, 1080), _cursor.Clip);
    }

    [Fact]
    public void Nothing_touches_the_mouse_or_the_keyboard_before_Start()
    {
        _rig = new SettingsRig(new AppSettings { MouseLock = new MouseLockSettings { Enabled = true } });
        _controller = new MouseLockController(_rig.Settings, new MouseLockService(_cursor, _log), _hook, action => action(), _log);

        _controller.Toggle();
        _controller.Recenter();
        _controller.Verify();
        _controller.ApplySettings(new MouseLockSettings { Enabled = true });

        Assert.Equal(0, _cursor.ClipCalls);
        Assert.Empty(_cursor.Moves);
        Assert.Equal(0, _hook.Installs);
        Assert.Empty(_registrar.Registered);
    }

    [Fact]
    public void Ctrl_x3_off_in_the_settings_installs_no_keyboard_hook()
    {
        Start(new MouseLockSettings { TripleCtrl = false });

        Assert.False(_hook.IsInstalled);
        Assert.Equal(0, _hook.Installs);
    }

    [Fact]
    public void The_hook_follows_the_Ctrl_x3_setting_while_the_app_runs()
    {
        var controller = Start(new MouseLockSettings { TripleCtrl = false });

        controller.ApplySettings(new MouseLockSettings { TripleCtrl = true });
        Assert.True(_hook.IsInstalled);

        controller.ApplySettings(new MouseLockSettings { TripleCtrl = true }); // the same again: nothing more
        Assert.Equal(1, _hook.Installs);

        controller.ApplySettings(new MouseLockSettings { TripleCtrl = false });
        Assert.False(_hook.IsInstalled);
    }

    [Fact]
    public void A_refused_hook_is_logged_and_the_rest_keeps_working()
    {
        _hook.Accept = false;

        var controller = Start(new MouseLockSettings { Enabled = true });

        Assert.False(controller.TripleCtrlActive);
        Assert.True(controller.IsLocked);
        Assert.Contains(_log.Lines, line => line.StartsWith("Warning", StringComparison.Ordinal) && line.Contains("gancho", StringComparison.Ordinal));
    }

    // ---- shortcut ----

    [Fact]
    public void A_saved_shortcut_is_the_one_registered()
    {
        Start(new MouseLockSettings { Hotkey = "Ctrl+Shift+F9" });

        Assert.Equal(["Ctrl+Shift+F9"], _registrar.Registered);
    }

    [Fact]
    public void A_reserved_saved_shortcut_falls_back_to_the_default_with_a_log_line_and_is_saved_back()
    {
        var controller = Start(new MouseLockSettings { Hotkey = "Ctrl+V" });

        Assert.Equal(["Ctrl+Alt+M"], _registrar.Registered);
        Assert.Equal("Ctrl+Alt+M", controller.HotkeyStatus.Text);
        Assert.Equal("Ctrl+Alt+M", _rig!.Settings.Current.MouseLock.Hotkey);
        Assert.Contains(_log.Lines, line => line.Contains("invalido", StringComparison.Ordinal) && line.Contains("Ctrl+V", StringComparison.Ordinal));
    }

    [Fact]
    public void A_malformed_saved_shortcut_also_falls_back()
    {
        Start(new MouseLockSettings { Hotkey = "banana" });

        Assert.Equal(["Ctrl+Alt+M"], _registrar.Registered);
        Assert.Equal("Ctrl+Alt+M", _rig!.Settings.Current.MouseLock.Hotkey);
    }

    [Fact]
    public void A_shortcut_owned_by_another_program_is_a_message_not_an_error()
    {
        _registrar.Accept = false;

        var controller = Start();

        var status = controller.HotkeyStatus;
        Assert.False(status.Registered);
        Assert.Contains("travar o mouse", status.Message, StringComparison.Ordinal);
        Assert.Contains("Ctrl+Alt+M", status.Message, StringComparison.Ordinal);
        Assert.False(controller.IsLocked);
    }

    [Fact]
    public void Changing_the_shortcut_in_the_settings_registers_the_new_one()
    {
        var controller = Start();
        var seen = new List<HotkeyStatus>();
        controller.HotkeyStatusChanged += seen.Add;

        controller.ApplySettings(new MouseLockSettings { Hotkey = "Ctrl+Alt+F8" });

        Assert.Equal(["Ctrl+Alt+M", "Ctrl+Alt+F8"], _registrar.Registered);
        Assert.Equal("Ctrl+Alt+F8", Assert.Single(seen).Text);
    }

    // ---- toggle ----

    [Fact]
    public void The_shortcut_toggles_the_lock_and_saves_the_choice_for_the_next_start()
    {
        var controller = Start();

        controller.Toggle();
        Assert.True(controller.IsLocked);
        Assert.True(_rig!.Settings.Current.MouseLock.Enabled);

        controller.Toggle();
        Assert.False(controller.IsLocked);
        Assert.False(_rig.Settings.Current.MouseLock.Enabled);
        Assert.Equal(1, _cursor.UnclipCalls);
    }

    [Fact]
    public void A_lock_windows_refuses_is_not_saved_as_on()
    {
        _cursor.ClipAccepted = false;
        var controller = Start();

        controller.Toggle();

        Assert.False(controller.IsLocked);
        Assert.False(_rig!.Settings.Current.MouseLock.Enabled);
    }

    [Fact]
    public void The_setting_changing_elsewhere_the_switch_in_Configuracoes_turns_the_lock_on_and_off()
    {
        var controller = Start();

        controller.ApplySettings(new MouseLockSettings { Enabled = true });
        Assert.True(controller.IsLocked);

        controller.ApplySettings(new MouseLockSettings { Enabled = true });
        Assert.Equal(1, _cursor.ClipCalls); // already on: untouched

        controller.ApplySettings(new MouseLockSettings { Enabled = false });
        Assert.False(controller.IsLocked);
    }

    [Fact]
    public void The_lock_state_is_announced_to_the_tray()
    {
        var controller = Start();
        var states = new List<bool>();
        controller.LockedChanged += states.Add;

        controller.Toggle();
        controller.Toggle();

        Assert.Equal([true, false], states);
    }

    [Fact]
    public void A_display_change_or_the_watchdog_puts_the_lock_back_through_Verify()
    {
        var controller = Start(new MouseLockSettings { Enabled = true });
        _cursor.Primary = new ScreenRect(0, 0, 2560, 1440);

        controller.Verify();

        Assert.Equal(new ScreenRect(0, 0, 2560, 1440), _cursor.Clip);
    }

    // ---- Ctrl x3 ----

    [Fact]
    public void Ctrl_x3_sends_the_cursor_to_the_center_with_the_lock_off_and_logs_only_that()
    {
        Start();
        _cursor.X = -1500; // on the LED
        _cursor.Y = 100;

        _hook.Tap(Ctrl, 1000);
        _hook.Tap(Ctrl, 1200);
        Assert.Empty(_cursor.Moves);
        _hook.Tap(Ctrl, 1400);

        Assert.Equal([(960, 540)], _cursor.Moves);
        Assert.Equal(0, _cursor.ClipCalls); // the lock stays off
        Assert.Contains("Information: Ctrl x3: mouse centralizado", _log.Lines);
    }

    [Fact]
    public void Ctrl_x3_with_the_lock_on_moves_inside_the_clip()
    {
        Start(new MouseLockSettings { Enabled = true });

        _hook.Tap(Ctrl, 0);
        _hook.Tap(Ctrl, 100);
        _hook.Tap(Ctrl, 200);

        Assert.Equal((960, 540), _cursor.Moves[^1]);
        Assert.True(_cursor.Clip.Contains(960, 540));
    }

    [Fact]
    public void Typing_and_shortcuts_never_move_the_cursor_and_no_key_is_ever_logged()
    {
        Start();
        var linesBefore = _log.Lines.Count;

        _hook.Key(Ctrl, true, 0);
        _hook.Key(0x43, true, 20); // Ctrl+C
        _hook.Key(0x43, false, 60);
        _hook.Key(Ctrl, false, 80);
        foreach (var key in new[] { 0x41, 0x53, 0x44, 0x46 })
        {
            _hook.Tap(key, 200);
        }

        Assert.Empty(_cursor.Moves);
        Assert.Equal(linesBefore, _log.Lines.Count);
    }

    [Fact]
    public void With_Ctrl_x3_off_the_keys_reach_nothing()
    {
        Start(new MouseLockSettings { TripleCtrl = false });

        _hook.Tap(Ctrl, 0);
        _hook.Tap(Ctrl, 100);
        _hook.Tap(Ctrl, 200);

        Assert.Empty(_cursor.Moves);
    }

    [Fact]
    public void A_fourth_quick_Ctrl_does_not_move_it_again()
    {
        Start();

        _hook.Tap(Ctrl, 0);
        _hook.Tap(Ctrl, 100);
        _hook.Tap(Ctrl, 200);
        _hook.Tap(Ctrl, 300);

        Assert.Single(_cursor.Moves);
    }

    // ---- exit ----

    [Fact]
    public void Dispose_releases_the_cursor_the_hook_and_keeps_the_saved_choice()
    {
        var controller = Start(new MouseLockSettings { Enabled = true });

        controller.Dispose();

        Assert.False(controller.IsLocked);
        Assert.Equal(1, _cursor.UnclipCalls);
        Assert.False(_hook.IsInstalled);
        Assert.True(_rig!.Settings.Current.MouseLock.Enabled); // the lock comes back at the next start
        controller.Dispose(); // twice is fine
    }

    [Fact]
    public void ReleaseNow_after_an_unhandled_error_lets_go_without_changing_the_settings()
    {
        var controller = Start(new MouseLockSettings { Enabled = true });

        controller.ReleaseNow();

        Assert.False(controller.IsLocked);
        Assert.Equal(FakeCursor.Virtual, _cursor.Clip);
        Assert.False(_hook.IsInstalled);
        Assert.True(_rig!.Settings.Current.MouseLock.Enabled);
    }

    [Fact]
    public void A_controller_that_never_locked_never_releases_anything_on_exit()
    {
        var controller = Start();

        controller.Dispose();

        Assert.Equal(0, _cursor.UnclipCalls);
    }
}
