using AlertaIBT.App.ViewModels;
using AlertaIBT.Core.Control;
using AlertaIBT.Core.Diagnostics;
using AlertaIBT.Core.Settings;

namespace AlertaIBT.App.Tests.ViewModels;

// Batch 39: for every on/off switch of the tabs: flip it on the real view-model, let the autosave rest, build a FRESH rig from what is on disk
// (a new run of the app) and check the screen shows the same position. The "applied at start" part (the service really does it) is asserted where
// the service is part of the rig (Controle, NDI, Som, the player).
public sealed class SwitchRoundTripViewModelTests
{
    // ---- Configuracoes ----

    private static (SettingsRig Rig, SettingsViewModel Vm) SettingsTab(AppSettings? initial = null)
    {
        var rig = new SettingsRig(initial);
        var vm = new SettingsViewModel(
            rig.Settings,
            rig.Protector,
            rig.Hub,
            rig.Saver,
            new DiagnosticsViewModel(new LogFiles(Path.Combine(rig.Folder, "logs")), _ => { }, () => null),
            () => Task.CompletedTask);
        return (rig, vm);
    }

    private static readonly string[] SwitchNames = ["MouseLockEnabled", "TripleCtrl", "SiteEnabled", "ClearHistoryWeekly", "AlertsEnabled", "AutoUpdate", "AppWindowsInScreenshots"];

    public static TheoryData<string> SettingsSwitches() => new(SwitchNames);

    private static bool Read(SettingsViewModel vm, string name) => name switch
    {
        "MouseLockEnabled" => vm.MouseLockEnabled,
        "TripleCtrl" => vm.TripleCtrl,
        "SiteEnabled" => vm.SiteEnabled,
        "ClearHistoryWeekly" => vm.ClearHistoryWeekly,
        "AlertsEnabled" => vm.AlertsEnabled,
        "AutoUpdate" => vm.AutoUpdate,
        _ => vm.AppWindowsInScreenshots,
    };

    private static void Write(SettingsViewModel vm, string name, bool value)
    {
        switch (name)
        {
            case "MouseLockEnabled": vm.MouseLockEnabled = value; break;
            case "TripleCtrl": vm.TripleCtrl = value; break;
            case "SiteEnabled": vm.SiteEnabled = value; break;
            case "ClearHistoryWeekly": vm.ClearHistoryWeekly = value; break;
            case "AlertsEnabled": vm.AlertsEnabled = value; break;
            case "AutoUpdate": vm.AutoUpdate = value; break;
            default: vm.AppWindowsInScreenshots = value; break;
        }
    }

    [Theory]
    [MemberData(nameof(SettingsSwitches))]
    public void A_settings_switch_flipped_and_saved_is_shown_the_same_way_by_the_next_run(string name)
    {
        var (rig, vm) = SettingsTab();
        var before = Read(vm, name);
        var defaults = SwitchNames.ToDictionary(n => n, n => Read(vm, n));

        Write(vm, name, !before);
        rig.Rest();
        var saved = rig.OnDisk();
        vm.Dispose();
        rig.Dispose();

        var (next, nextVm) = SettingsTab(saved);
        try
        {
            Assert.Equal(!before, Read(nextVm, name));
            foreach (var other in defaults.Keys.Where(o => o != name))
            {
                Assert.True(Read(nextVm, other) == defaults[other], $"{other} changed when only {name} was flipped");
            }
        }
        finally
        {
            nextVm.Dispose();
            next.Dispose();
        }
    }

    // ---- Som e player ----

    [Fact]
    public void The_sound_switches_adjust_at_start_lock_output_and_holyrics_route_come_back_as_left()
    {
        var first = new SomRig();
        first.Som.AdjustAtStart = !first.Som.AdjustAtStart;
        first.Som.LockOutput = !first.Som.LockOutput;
        first.Som.HolyricsRoute = !first.Som.HolyricsRoute;
        first.Rest();
        var saved = first.Settings.OnDisk();
        var expected = (first.Som.AdjustAtStart, first.Som.LockOutput, first.Som.HolyricsRoute);
        first.Dispose();

        using var next = new SomRig(saved);

        Assert.Equal(expected, (next.Som.AdjustAtStart, next.Som.LockOutput, next.Som.HolyricsRoute));
        Assert.Equal(expected, (next.Current.Audio.Enabled, next.Current.Audio.LockOutput, next.Current.Audio.HolyricsRoute));
    }

    [Fact]
    public void The_mute_icons_of_the_four_volumes_come_back_as_left()
    {
        var first = new SomRig();
        foreach (var row in first.Som.VolumeRows)
        {
            row.Muted = true;
        }

        first.Rest();
        var saved = first.Settings.OnDisk();
        first.Dispose();

        using var next = new SomRig(saved);

        Assert.All(next.Som.VolumeRows, row => Assert.True(row.Muted));
        Assert.True(next.Current.Audio.MasterMuted);
        Assert.True(next.Current.Audio.SystemSoundsMuted);
        Assert.True(next.Current.Audio.Apps[0].Muted);
        Assert.True(next.Current.Player.StartMuted);
    }

    [Fact]
    public void The_player_switches_and_every_alarm_come_back_as_left()
    {
        var first = new SomRig();
        var p = first.PlayerView;
        p.PlayerOn = false;
        p.Shuffle = false;
        p.MediaKeys = false;
        p.NormalizeEnabled = false;
        p.WriteLoudnessTags = true;
        p.AlarmsEnabled = false;
        first.Som.Alarms[0].Enabled = false;
        first.Rest();
        first.Settings.Settings.Flush();
        var saved = first.Settings.OnDisk();
        first.Dispose();

        using var next = new SomRig(saved);

        var q = next.PlayerView;
        Assert.False(q.PlayerOn);
        Assert.False(q.Shuffle);
        Assert.False(q.MediaKeys);
        Assert.False(q.NormalizeEnabled);
        Assert.True(q.WriteLoudnessTags);
        Assert.False(q.AlarmsEnabled);
        Assert.False(next.Som.Alarms[0].Enabled);

        // and the engine behind the screen was built from the same settings, not from the screen
        Assert.False(next.Current.Player.Enabled);
        Assert.False(next.Current.Player.Alarms[0].Enabled);
    }

    // ---- NDI ----

    [Fact]
    public void The_ndi_switches_come_back_as_left_and_the_layer_and_bars_are_really_put_back_in_the_engine()
    {
        var first = new NdiRig();
        first.Vm.LayerOn = true;
        first.Vm.TestPattern = true;
        first.Vm.HideFromCapture = true;
        first.Vm.ShortcutsEnabled = false;
        first.Vm.MonitorLocked = false;
        first.Settings.Rest();
        first.Settings.Settings.Flush();
        var saved = first.Settings.OnDisk().Ndi;
        first.Dispose();

        using var next = new NdiRig(ndi: saved);
        Assert.False(next.Service.LayerOn); // services first
        next.Service.RestoreSavedState(); // then the shell puts the switches back

        Assert.True(next.Service.LayerOn);
        Assert.True(next.Service.Switcher.TestPattern);
        Assert.True(next.Vm.LayerOn);
        Assert.True(next.Vm.TestPattern);
        Assert.True(next.Vm.HideFromCapture);
        Assert.False(next.Vm.ShortcutsEnabled);
        Assert.False(next.Vm.MonitorLocked);
    }

    // ---- Controle ----

    [Fact]
    public void The_control_switches_come_back_as_left_and_the_master_really_opens_the_socket_at_start()
    {
        var first = new ControlRig();
        first.Vm.Enabled = true;
        first.Vm.UniverseOffset = false;
        first.Vm.HolyricsArrows = false;
        first.Vm.Rows[0].Enabled = false;
        first.Chrome.Settings.Flush();
        var saved = first.Chrome.OnDisk();
        first.Dispose();

        using var next = new ControlRig(saved);

        Assert.True(next.Vm.Enabled);
        Assert.Equal(ArtNetState.Listening, next.Socket.State); // applied by the service, not only drawn by the screen
        Assert.False(next.Vm.UniverseOffset);
        Assert.False(next.Vm.HolyricsArrows);
        Assert.False(next.Vm.Rows[0].Enabled);
        Assert.True(next.Vm.Rows[1].Enabled);
    }

    [Fact]
    public void A_master_left_off_keeps_the_socket_closed_and_a_busy_port_shows_the_chip_instead_of_failing()
    {
        using (var off = new ControlRig())
        {
            Assert.Equal(ArtNetState.Stopped, off.Socket.State);
        }

        using var on = new ControlRig(new AppSettings { Control = new ControlSettings { Enabled = true } });
        on.Socket.SetState(ArtNetState.PortInUse);

        Assert.Equal(ControlSignal.PortInUse, on.Service.GetStatus().Signal);
        Assert.True(on.Control.Enabled); // the choice is kept while the port is busy
    }

    // ---- Correcao (Inicio) ----

    [Fact]
    public void The_correction_switch_and_the_offline_only_choice_come_back_as_left()
    {
        var first = new SettingsRig();
        var vm = new CorrectionViewModel(first.Settings, first.Protector, first.Hub, first.Saver);
        vm.Enabled = false;
        vm.UseOffline = true;
        first.Rest();
        var saved = first.OnDisk();
        vm.Dispose();
        first.Dispose();

        using var next = new SettingsRig(saved);
        using var nextVm = new CorrectionViewModel(next.Settings, next.Protector, next.Hub, next.Saver);

        Assert.False(nextVm.Enabled);
        Assert.True(nextVm.UseOffline);
        Assert.Equal("offline", next.Settings.Current.Correction.Mode);
    }
}
