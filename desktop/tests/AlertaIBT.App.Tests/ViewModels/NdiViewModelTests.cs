using AlertaIBT.App.ViewModels;
using AlertaIBT.Core.Ndi;
using AlertaIBT.Core.Screens;
using AlertaIBT.Core.Settings;

namespace AlertaIBT.App.Tests.ViewModels;

// The NDI tab's logic on the real engine (switcher, settings) over fake receivers: clicking a camera, the cut, the vertical position, adding,
// renaming and removing cameras, the toggles, the monitor, and what the tab shows when the NDI runtime is missing.
public sealed class NdiViewModelTests
{
    [Fact]
    public void Only_the_cameras_that_were_added_are_buttons_and_the_plus_tile_follows_them()
    {
        using var rig = new NdiRig(cameras: 3);

        Assert.Equal(["Cam1", "Cam2", "Cam3"], rig.Vm.Cameras.Select(c => c.Name));
        Assert.Equal(4, rig.Vm.Tiles.Count);
        Assert.IsType<NdiAddTile>(rig.Vm.Tiles[^1]);
        Assert.True(rig.Vm.HasCameras);
        Assert.False(rig.Vm.HasNoCameras);
        Assert.True(rig.Vm.CanAddCamera);
    }

    [Fact]
    public void With_nothing_configured_there_are_no_buttons_and_the_empty_state_shows()
    {
        using var rig = new NdiRig(cameras: 0);

        Assert.Empty(rig.Vm.Cameras);
        Assert.True(rig.Vm.HasNoCameras);
        Assert.False(rig.Vm.HasCameras);
        Assert.Single(rig.Vm.Tiles);
        Assert.False(rig.Vm.CanTake);
    }

    [Fact]
    public void Clicking_a_camera_sends_it_to_the_preview_and_never_on_air()
    {
        using var rig = new NdiRig();

        rig.Vm.Cameras[1].SelectCommand.Execute(null);

        Assert.True(rig.Vm.Cameras[1].IsPreview);
        Assert.False(rig.Vm.Cameras[1].IsProgram);
        Assert.Equal(1, rig.Service.GetStatus().Switcher.PreviewSlot);
        Assert.Null(rig.Service.GetStatus().Switcher.ProgramSlot);
        Assert.True(rig.Vm.Preview.HasCamera);
        Assert.Equal("Cam2", rig.Vm.Preview.CameraName);
        Assert.False(rig.Vm.OnAir.HasCamera);
        Assert.True(rig.Vm.CanTake);
    }

    [Fact]
    public void Clicking_a_second_camera_moves_the_preview_and_keeps_what_is_on_air()
    {
        using var rig = new NdiRig();
        rig.GoOnAir(1);

        rig.Vm.Cameras[2].SelectCommand.Execute(null);

        Assert.True(rig.Vm.Cameras[0].IsProgram);
        Assert.True(rig.Vm.Cameras[2].IsPreview);
        Assert.False(rig.Vm.Cameras[1].IsPreview);
    }

    [Fact]
    public void Cortar_swaps_the_preview_and_the_program()
    {
        using var rig = new NdiRig();
        rig.GoOnAir(1);
        Assert.True(rig.Vm.Cameras[0].IsProgram);
        Assert.False(rig.Vm.Preview.HasCamera);

        rig.Preview(2, hue: 3);
        rig.Vm.TakeCommand.Execute(null);

        Assert.True(rig.Vm.Cameras[1].IsProgram);
        Assert.True(rig.Vm.Cameras[0].IsPreview);
        Assert.Equal("Cam2", rig.Vm.OnAir.CameraName);
        Assert.Equal("Cam1", rig.Vm.Preview.CameraName);
    }

    [Fact]
    public void Cortar_without_a_camera_in_the_preview_says_so_and_cuts_nothing()
    {
        using var rig = new NdiRig();

        rig.Vm.TakeCommand.Execute(null);

        Assert.Contains("pr", rig.Vm.Notice, StringComparison.OrdinalIgnoreCase);
        Assert.True(rig.Vm.HasNotice);
        Assert.Null(rig.Service.GetStatus().Switcher.ProgramSlot);
        rig.Vm.DismissNoticeCommand.Execute(null);
        Assert.False(rig.Vm.HasNotice);
    }

    [Fact]
    public void A_camera_without_a_source_cannot_go_to_the_preview_and_the_reason_is_shown()
    {
        using var rig = new NdiRig(cameraSlots: slots => slots.Select((s, i) => i == 1 ? s with { Source = string.Empty } : s).ToList());

        rig.Vm.Cameras.First(c => c.Slot == 1).SelectCommand.Execute(null);

        Assert.NotEmpty(rig.Vm.Notice);
        Assert.False(rig.Vm.Preview.HasCamera);
    }

    [Fact]
    public void The_shortcuts_and_the_api_are_seen_by_the_tab_because_it_listens_to_the_engine()
    {
        using var rig = new NdiRig();

        rig.Service.SelectPreview(2);
        Assert.True(rig.Vm.Cameras[2].IsPreview);
        Assert.Equal("Cam3", rig.Vm.Preview.CameraName);

        rig.LaneOf(3).Push();
        rig.Service.Take();
        Assert.True(rig.Vm.Cameras[2].IsProgram);
        Assert.Equal("Cam3", rig.Vm.OnAir.CameraName);

        rig.Service.SetLayer(true);
        Assert.True(rig.Vm.LayerOn);
        rig.Service.SetLayer(false);
        Assert.False(rig.Vm.LayerOn);
    }

    [Fact]
    public void The_slider_moves_the_camera_position_at_once_and_it_is_saved()
    {
        using var rig = new NdiRig();
        rig.Preview(2);

        rig.Vm.Preview.Position = 80;

        Assert.Equal(80, rig.Ndi.Cameras[1].VerticalPosition);
        Assert.Equal(50, rig.Ndi.Cameras[0].VerticalPosition);
        rig.Settings.Rest();
        rig.Settings.Settings.Flush();
        Assert.Equal(80, rig.Settings.OnDisk().Ndi.Cameras[1].VerticalPosition);
    }

    [Fact]
    public void The_wheel_steps_are_one_percent_and_five_with_shift_and_stay_between_zero_and_a_hundred()
    {
        using var rig = new NdiRig();
        rig.Preview(1);
        var lane = rig.Vm.Preview;

        lane.Nudge(1);
        Assert.Equal(51, rig.Ndi.Cameras[0].VerticalPosition);
        lane.Nudge(-5);
        Assert.Equal(46, rig.Ndi.Cameras[0].VerticalPosition);
        lane.Nudge(500);
        Assert.Equal(100, lane.Position);
        lane.Nudge(-500);
        Assert.Equal(0, lane.Position);
        Assert.Equal(0, rig.Ndi.Cameras[0].VerticalPosition);
    }

    [Fact]
    public void Each_camera_keeps_its_own_position_and_the_picture_follows_the_camera_that_is_in_it()
    {
        using var rig = new NdiRig();
        rig.Preview(1);
        rig.Vm.Preview.Position = 10;
        rig.Vm.Cameras[0].SelectCommand.Execute(null);
        rig.GoOnAir(2);
        Assert.Equal(50, rig.Vm.OnAir.Position);

        rig.Vm.OnAir.Position = 90;

        Assert.Equal(90, rig.Ndi.Cameras[1].VerticalPosition);
        Assert.Equal(10, rig.Ndi.Cameras[0].VerticalPosition);
    }

    [Fact]
    public void A_lane_without_a_camera_does_not_write_a_position()
    {
        using var rig = new NdiRig();

        rig.Vm.Preview.Position = 70;

        Assert.All(rig.Ndi.Cameras, camera => Assert.Equal(50, camera.VerticalPosition));
    }

    [Fact]
    public void Adding_a_camera_takes_the_first_free_slot_with_the_name_and_source_the_owner_typed()
    {
        using var rig = new NdiRig(cameras: 2);
        rig.Edits.Enqueue(new CameraEditResult("Lateral", "192.168.1.50:5961"));

        rig.Vm.AddCameraCommand.Execute(null);

        Assert.Equal(3, rig.Vm.Cameras.Count);
        Assert.Equal("Lateral", rig.Ndi.Cameras[2].Name);
        Assert.Equal("192.168.1.50:5961", rig.Ndi.Cameras[2].Source);
        Assert.Equal("Nova c\U000000E2mera", rig.EditRequests[0].Title);
        Assert.Equal("C\U000000E2mera 3", rig.EditRequests[0].Name);
    }

    [Fact]
    public void Cancelling_the_camera_window_adds_nothing()
    {
        using var rig = new NdiRig(cameras: 1);
        rig.Edits.Enqueue(null);

        rig.Vm.AddCameraCommand.Execute(null);

        Assert.Single(rig.Vm.Cameras);
    }

    [Fact]
    public void The_window_check_needs_a_name_and_a_source_and_limits_the_name()
    {
        using var rig = new NdiRig(cameras: 0);
        rig.Edits.Enqueue(new CameraEditResult("Central", string.Empty));
        rig.Vm.AddCameraCommand.Execute(null);
        var check = rig.EditRequests[0].Check;

        Assert.NotNull(check(string.Empty, "PC (Cam1)"));
        Assert.NotNull(check("Central", string.Empty));
        Assert.NotNull(check(new string('x', 33), "PC (Cam1)"));
        Assert.Null(check("Central", "PC (Cam1)"));
        Assert.Null(check("Central", "192.168.1.5"));
        Assert.Empty(rig.Vm.Cameras);
    }

    [Fact]
    public void The_plus_tile_goes_away_after_six_cameras()
    {
        using var rig = new NdiRig(cameras: 5);
        rig.Edits.Enqueue(new CameraEditResult("OBS", "PC (OBS)"));

        rig.Vm.AddCameraCommand.Execute(null);

        Assert.Equal(6, rig.Vm.Cameras.Count);
        Assert.False(rig.Vm.CanAddCamera);
        Assert.Equal(6, rig.Vm.Tiles.Count);
        Assert.All(rig.Vm.Tiles, tile => Assert.IsType<NdiCameraItem>(tile));

        rig.Vm.AddCameraCommand.Execute(null);
        Assert.Equal(6, rig.Vm.Cameras.Count);
    }

    [Fact]
    public void Editing_a_camera_changes_the_name_and_the_source()
    {
        using var rig = new NdiRig();
        rig.Edits.Enqueue(new CameraEditResult("Central", "PC (Nova)"));

        rig.Vm.Cameras[0].EditCommand.Execute(null);

        Assert.Equal("Central", rig.Vm.Cameras[0].Name);
        Assert.Equal("PC (Nova)", rig.Ndi.Cameras[0].Source);
        Assert.Equal("Editar c\U000000E2mera", rig.EditRequests[0].Title);
        Assert.Equal("Cam1", rig.EditRequests[0].Name);
        Assert.Equal("PC (Cam1)", rig.EditRequests[0].Source);
    }

    [Fact]
    public void The_window_is_offered_the_sources_the_finder_found()
    {
        using var rig = new NdiRig();
        rig.Runtime.Found.Add(new NdiSourceInfo("STUDIO (Central)", "10.0.0.5:5961"));
        rig.Vm.SetActive(true);
        WaitFor(() => rig.Vm.SourceCount == 1);
        rig.Edits.Enqueue(null);

        rig.Vm.Cameras[0].EditCommand.Execute(null);

        Assert.Single(rig.EditRequests[0].Sources);
        Assert.Equal("STUDIO (Central)", rig.EditRequests[0].Sources[0].Name);
        rig.Vm.SetActive(false);
    }

    [Fact]
    public void Removing_a_camera_asks_first_and_No_keeps_it()
    {
        using var rig = new NdiRig();
        rig.ConfirmAnswer = false;

        rig.Vm.Cameras[1].DeleteCommand.Execute(null);

        Assert.Equal(3, rig.Vm.Cameras.Count);
        Assert.Single(rig.Confirmations);
        Assert.Contains("Cam2", rig.Confirmations[0].Title);
        Assert.Equal("PC (Cam2)", rig.Ndi.Cameras[1].Source);
    }

    [Fact]
    public void Removing_a_camera_with_Sim_empties_its_slot_and_it_comes_back_as_the_next_to_add()
    {
        using var rig = new NdiRig();
        rig.Vm.Cameras[1].SelectCommand.Execute(null);
        rig.Vm.Preview.Position = 20;

        rig.Vm.Cameras[1].DeleteCommand.Execute(null);

        Assert.Equal(["Cam1", "Cam3"], rig.Vm.Cameras.Select(c => c.Name));
        Assert.Equal(string.Empty, rig.Ndi.Cameras[1].Source);
        Assert.Equal(50, rig.Ndi.Cameras[1].VerticalPosition);
        Assert.False(rig.Vm.Preview.HasCamera);

        rig.Edits.Enqueue(new CameraEditResult("Lateral", "PC (Lateral)"));
        rig.Vm.AddCameraCommand.Execute(null);
        Assert.Equal(["Cam1", "Lateral", "Cam3"], rig.Vm.Cameras.Select(c => c.Name));
    }

    [Fact]
    public void The_camera_on_the_telao_switch_is_off_when_the_app_opens_and_drives_the_engine()
    {
        using var rig = new NdiRig();

        Assert.False(rig.Vm.LayerOn);

        rig.Vm.LayerOn = true;
        Assert.True(rig.Service.LayerOn);
        rig.Vm.LayerOn = false;
        Assert.False(rig.Service.LayerOn);
    }

    [Fact]
    public void The_test_bars_toggle_drives_the_engine()
    {
        using var rig = new NdiRig();

        rig.Vm.TestPattern = true;

        Assert.True(rig.Service.GetStatus().Switcher.TestPattern);
        rig.Service.SetTestPattern(false);
        Assert.False(rig.Vm.TestPattern);
    }

    [Fact]
    public void Hide_from_the_live_starts_off_and_the_toggles_are_saved()
    {
        using var rig = new NdiRig();
        Assert.False(rig.Vm.HideFromCapture);
        Assert.True(rig.Vm.ShortcutsEnabled);

        rig.Vm.HideFromCapture = true;
        rig.Vm.ShortcutsEnabled = false;

        Assert.True(rig.Ndi.HideFromCapture);
        Assert.False(rig.Ndi.ShortcutsEnabled);
        rig.Settings.Rest();
        rig.Settings.Settings.Flush();
        Assert.True(rig.Settings.OnDisk().Ndi.HideFromCapture);
        Assert.False(rig.Settings.OnDisk().Ndi.ShortcutsEnabled);
    }

    [Fact]
    public void The_status_chips_say_fps_resolution_and_signal()
    {
        using var rig = new NdiRig();
        Assert.Equal(NdiSignal.Off, rig.Vm.Signal);
        Assert.Equal("-", rig.Vm.FpsText);

        rig.GoOnAir(1);
        Assert.Equal(NdiSignal.Off, rig.Vm.Signal);
        rig.Vm.LayerOn = true;
        rig.LaneOf(1).Push();
        rig.Vm.Tick();
        rig.Vm.SetActive(true);
        rig.Vm.Tick();

        Assert.Equal("640 x 360", rig.Vm.ResolutionText);
        Assert.EndsWith("fps", rig.Vm.FpsText);
        Assert.Equal("0", rig.Vm.DroppedText);
    }

    [Fact]
    public void With_the_layer_on_and_no_camera_on_air_the_signal_says_so()
    {
        using var rig = new NdiRig();

        rig.Vm.LayerOn = true;

        Assert.Equal(NdiSignal.NoCamera, rig.Vm.Signal);
    }

    [Fact]
    public void The_tally_is_red_only_while_the_camera_is_really_on_the_telao()
    {
        using var rig = new NdiRig();
        rig.GoOnAir(1);
        Assert.False(rig.Vm.OnAir.TallyRed);

        rig.Vm.LayerOn = true;
        rig.LaneOf(1).Push();
        rig.Vm.Tick();

        Assert.True(rig.Vm.OnAir.TallyRed);
        Assert.Equal(NdiSignal.Live, rig.Vm.Signal);
    }

    [Fact]
    public void A_source_that_stopped_shows_no_signal_on_its_picture()
    {
        using var rig = new NdiRig();
        rig.GoOnAir(1);
        rig.LaneOf(1).Set(NdiConnectionState.Lost);

        Assert.True(rig.Vm.OnAir.NoSignal);
    }

    [Fact]
    public void Without_the_NDI_runtime_the_banner_state_is_set_and_Cortar_is_off()
    {
        using var rig = new NdiRig(runtime: false);

        Assert.False(rig.Vm.RuntimeOk);
        Assert.True(rig.Vm.RuntimeMissing);
        Assert.Contains("ndi.video/tools", rig.Vm.RuntimeText);
        Assert.False(rig.Vm.CanTake);
    }

    [Fact]
    public void Checking_again_finds_the_runtime_when_it_was_installed_meanwhile()
    {
        using var rig = new NdiRig(runtime: false);
        rig.Vm.SetActive(true);

        rig.Runtime.IsAvailable = true;
        rig.Rechecks.Enqueue(true);
        rig.Vm.RecheckCommand.Execute(null);

        Assert.True(rig.Vm.RuntimeOk);
        Assert.False(rig.Vm.RuntimeMissing);
        Assert.False(rig.Vm.HasNotice);
        rig.Vm.SetActive(false);
    }

    [Fact]
    public void Checking_again_with_nothing_installed_says_so()
    {
        using var rig = new NdiRig(runtime: false);
        rig.Rechecks.Enqueue(false);

        rig.Vm.RecheckCommand.Execute(null);

        Assert.True(rig.Vm.RuntimeMissing);
        Assert.True(rig.Vm.HasNotice);
    }

    [Fact]
    public void The_links_open_the_download_page_and_the_ndi_site_and_the_trademark_line_is_the_one_NDI_asks_for()
    {
        using var rig = new NdiRig(runtime: false);

        rig.Vm.OpenDownloadCommand.Execute(null);
        rig.Vm.OpenSiteCommand.Execute(null);

        Assert.Equal(["https://ndi.video/tools/", "https://ndi.video"], rig.Opened);
        Assert.Equal(NdiConstants.TrademarkNotice, rig.Vm.TrademarkText);
        Assert.Contains("Vizrt NDI AB", rig.Vm.TrademarkText);
    }

    [Fact]
    public void The_monitor_is_locked_on_the_2560_by_768_one_by_default()
    {
        using var rig = new NdiRig();

        Assert.True(rig.Vm.MonitorLocked);
        Assert.False(rig.Vm.MonitorUnlocked);
        Assert.Equal("\\\\.\\DISPLAY3", rig.Vm.SelectedMonitor);
        Assert.Equal(2, rig.Vm.Monitors.Count);
        Assert.Contains("2560 x 768", rig.Vm.Monitors[1].Display);
        Assert.Equal(2560, rig.Vm.Preview.CropWidth);
        Assert.Equal(768, rig.Vm.Preview.CropHeight);
    }

    [Fact]
    public void Unlocking_preselects_the_2560_by_768_monitor_again_and_a_pick_is_saved()
    {
        using var rig = new NdiRig();

        rig.Vm.MonitorLocked = false;
        Assert.Equal("\\\\.\\DISPLAY3", rig.Ndi.MonitorDeviceName);
        Assert.False(rig.Ndi.MonitorLocked);

        rig.Vm.SelectedMonitor = "\\\\.\\DISPLAY1";
        Assert.Equal("\\\\.\\DISPLAY1", rig.Ndi.MonitorDeviceName);
        Assert.Equal(1920, rig.Vm.Preview.CropWidth);

        // lock and unlock again: back to the LED monitor
        rig.Vm.MonitorLocked = true;
        rig.Vm.MonitorLocked = false;
        Assert.Equal("\\\\.\\DISPLAY3", rig.Vm.SelectedMonitor);
        Assert.Equal("\\\\.\\DISPLAY3", rig.Ndi.MonitorDeviceName);
    }

    [Fact]
    public void The_fixed_ip_manager_saves_a_clean_list_and_cancel_keeps_it()
    {
        using var rig = new NdiRig();
        rig.Ips.Enqueue([" 192.168.1.50 ", "10.0.0.8:5961", "192.168.1.50", string.Empty]);

        rig.Vm.ManageIpsCommand.Execute(null);

        Assert.Equal(["192.168.1.50", "10.0.0.8:5961"], rig.Ndi.FixedIps);
        Assert.Equal(["192.168.1.50", "10.0.0.8:5961"], rig.Vm.FixedIps);

        rig.Ips.Enqueue(null);
        rig.Vm.ManageIpsCommand.Execute(null);
        Assert.Equal(2, rig.Ndi.FixedIps.Count);
    }

    [Fact]
    public void The_finder_only_runs_while_the_tab_is_on_screen()
    {
        using var rig = new NdiRig();
        rig.Runtime.Found.Add(new NdiSourceInfo("STUDIO (Central)", "10.0.0.5:5961"));
        Assert.Equal(0, rig.Vm.SourceCount);

        rig.Vm.SetActive(true);
        WaitFor(() => rig.Vm.SourceCount == 1);
        Assert.Equal(1, rig.Vm.SourceCount);
        rig.Vm.SetActive(false);
        Assert.Single(rig.Runtime.FinderIps);
    }

    [Fact]
    public void After_dispose_the_tab_no_longer_follows_the_engine()
    {
        var rig = new NdiRig();
        rig.Vm.Dispose();

        rig.Service.SelectPreview(0);

        Assert.False(rig.Vm.Cameras[0].IsPreview);
        rig.Service.Dispose();
        rig.Settings.Dispose();
    }

    [Fact]
    public void The_lane_copies_its_thumbnail_without_allocating_a_picture_per_call()
    {
        using var rig = new NdiRig();
        rig.Preview(1);
        var buffer = new byte[480 * 480 * 4];

        Assert.True(rig.Vm.Preview.TryCopyThumbnail(buffer, out var w, out var h, out var v1));
        Assert.Equal(480, w);
        Assert.Equal(270, h);
        rig.LaneOf(1).Push(2);
        Assert.True(rig.Vm.Preview.TryCopyThumbnail(buffer, out _, out _, out var v2));
        Assert.True(v2 > v1);
    }

    [Fact]
    public void A_shortcut_another_program_owns_becomes_a_warning_and_goes_away_when_the_shortcuts_are_off()
    {
        using var rig = new NdiRig();
        rig.Hotkeys = new NdiHotkeyController(new DenyingRegistry("F2"), rig.Service);
        rig.Hotkeys.Apply(true);

        rig.Vm.SetActive(true);

        Assert.True(rig.Vm.HasShortcutWarning);
        Assert.Contains("F2", rig.Vm.ShortcutWarning);
        Assert.DoesNotContain("F1", rig.Vm.ShortcutWarning);

        rig.Vm.ShortcutsEnabled = false;
        rig.Hotkeys.Apply(false);
        Assert.False(rig.Vm.HasShortcutWarning);
        rig.Vm.SetActive(false);
    }

    private sealed class DenyingRegistry(string denied) : INdiHotkeyRegistry
    {
        private readonly Dictionary<int, string> _names = [];

        public bool Register(int id, AlertaIBT.Core.Input.HotkeyModifiers modifiers, int virtualKey)
        {
            var text = virtualKey is >= 0x70 and <= 0x7B ? $"F{virtualKey - 0x70 + 1}" : "Ctrl+Espa\U000000E7o";
            _names[id] = text;
            return !string.Equals(text, denied, StringComparison.Ordinal);
        }

        public void Unregister(int id) => _names.Remove(id);
    }
    private static void WaitFor(Func<bool> condition)
    {
        var end = DateTime.UtcNow.AddSeconds(3);
        while (!condition() && DateTime.UtcNow < end)
        {
            Thread.Sleep(20);
        }

        Assert.True(condition());
    }
}
