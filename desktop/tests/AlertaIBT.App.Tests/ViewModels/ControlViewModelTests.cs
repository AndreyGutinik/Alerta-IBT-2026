using AlertaIBT.App.ViewModels;
using AlertaIBT.Core.Control;
using AlertaIBT.Core.Settings;

namespace AlertaIBT.App.Tests.ViewModels;

// The Controle tab's logic: the switch, the live levels (only while the page is on screen), the editing of the map, "Testar", the dialogs.
public sealed class ControlViewModelTests
{
    private static bool WaitFor(Func<bool> condition, int ms = 3000)
    {
        var until = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < until)
        {
            if (condition())
            {
                return true;
            }

            Thread.Sleep(5);
        }

        return condition();
    }

    [Fact]
    public void It_starts_off_with_the_21_rows_of_the_owner()
    {
        using var rig = new ControlRig();

        Assert.False(rig.Vm.Enabled);
        Assert.Equal(ControlSignal.Off, rig.Vm.Signal);
        Assert.Equal("Desligado", rig.Vm.SignalText);
        Assert.Equal(21, rig.Vm.Rows.Count);
        Assert.Equal(100, int.Parse(rig.Vm.UniverseBox.Text));
        Assert.True(rig.Vm.UniverseOffset);
        Assert.Equal(string.Empty, rig.Vm.SourceIpText);
    }

    [Fact]
    public void The_master_switch_saves_and_the_signal_chip_follows_the_packets()
    {
        using var rig = new ControlRig();

        rig.Vm.Enabled = true;
        Assert.True(rig.Control.Enabled);
        Assert.Equal("Sem sinal", rig.Vm.SignalText);
        Assert.Equal("warn", rig.Vm.SignalTone);

        rig.Socket.Send(ControlRig.Levels());
        rig.Vm.SetActive(true);
        rig.Vm.Tick();

        Assert.Equal("Sinal ok", rig.Vm.SignalText);
        Assert.Equal("ok", rig.Vm.SignalTone);
        Assert.True(rig.Vm.HasPacket);
        Assert.Equal("2.0.0.2", rig.Vm.LastSource);
        Assert.Contains("\U000000FAltimo pacote", rig.Vm.SignalTip, StringComparison.OrdinalIgnoreCase);

        rig.Vm.Enabled = false;
        Assert.Equal("Desligado", rig.Vm.SignalText);
    }

    [Fact]
    public void After_three_seconds_without_packets_it_says_sem_sinal_with_the_time_since_the_last_one()
    {
        using var rig = new ControlRig();
        rig.Vm.Enabled = true;
        rig.Socket.Send(ControlRig.Levels());
        rig.Vm.SetActive(true);

        rig.Chrome.Clock.Advance(TimeSpan.FromSeconds(4));
        rig.Vm.Tick();

        Assert.Equal("Sem sinal", rig.Vm.SignalText);
        Assert.StartsWith("h\U000000E1 4", rig.Vm.SinceText);
    }

    [Fact]
    public void A_port_in_use_shows_the_clear_words()
    {
        using var rig = new ControlRig();
        rig.Vm.Enabled = true;

        rig.Socket.SetState(ArtNetState.PortInUse);

        Assert.Equal("Porta 6454 em uso", rig.Vm.SignalText);
        Assert.Equal("bad", rig.Vm.SignalTone);
    }

    [Fact]
    public void The_live_levels_are_read_only_while_the_page_is_on_screen()
    {
        using var rig = new ControlRig();
        rig.Vm.Enabled = true;
        rig.Socket.Send(ControlRig.Levels());

        rig.Socket.Send(ControlRig.Levels((8, 200)));
        rig.Vm.Tick(); // not active: nothing is read
        Assert.Equal(0, rig.Vm.Rows[7].Level);

        rig.Vm.SetActive(true);
        Assert.Equal(200, rig.Vm.Rows[7].Level);
        Assert.True(rig.Vm.Rows[7].IsOn);
        Assert.Equal("200", rig.Vm.Rows[7].LevelText);

        rig.Vm.SetActive(false);
        rig.Socket.Send(ControlRig.Levels((8, 10)));
        rig.Vm.Tick();
        Assert.Equal(200, rig.Vm.Rows[7].Level);
    }

    [Fact]
    public void The_pickup_state_of_a_fader_reaches_its_row()
    {
        using var rig = new ControlRig();
        rig.Targets.Levels[ControlAction.VolumePlayer] = 50;
        rig.Vm.Enabled = true;
        rig.Socket.Send(ControlRig.Levels((21, 0)));
        rig.Vm.SetActive(true);

        Assert.Equal(FaderPickup.Waiting, rig.Vm.Rows[20].PickupState);
        Assert.Contains("Esperando", rig.Vm.Rows[20].PickupTip);
        Assert.Equal(FaderPickup.None, rig.Vm.Rows[7].PickupState);
    }

    [Fact]
    public void Testar_runs_the_action_once_and_a_fader_has_nothing_to_test()
    {
        using var rig = new ControlRig();

        rig.Vm.Rows[7].TestCommand.Execute(null); // Cortar
        rig.Vm.Rows[2].TestCommand.Execute(null); // camera 3

        Assert.True(WaitFor(() => rig.Targets.Ran.Count == 2));
        Assert.Equal(new ControlCommand(8, ControlAction.Take, ControlCommandKind.Trigger), rig.Targets.Ran[0]);
        Assert.Equal(new ControlCommand(3, ControlAction.SelectCamera, ControlCommandKind.Select, Parameter: 3), rig.Targets.Ran[1]);
        Assert.False(rig.Vm.Rows[20].CanTest);
        Assert.True(rig.Vm.Rows[12].CanTest); // Holyrics: slide next is a real action now
    }

    [Fact]
    public void Changing_the_action_fixes_the_mode_and_saves()
    {
        using var rig = new ControlRig();
        var row = rig.Vm.Rows[7]; // Cortar, button

        row.SelectedAction = ControlCatalog.Get(ControlAction.VolumePlayer);

        Assert.Equal(ControlAction.VolumePlayer, rig.Control.Rows[7].Action);
        Assert.Equal(ControlMode.Fader, rig.Control.Rows[7].Mode);
        Assert.True(row.ShowPickup);
        Assert.False(row.ShowTrigger);
    }

    [Fact]
    public void The_mode_trigger_camera_and_pickup_are_saved_per_row()
    {
        using var rig = new ControlRig();

        rig.Vm.Rows[6].SelectedMode = ModeChoice.For(ControlMode.Button); // camera on the telao as a button
        rig.Vm.Rows[7].SelectedTrigger = TriggerChoice.All[1];
        rig.Vm.Rows[0].Camera = 5;
        rig.Vm.Rows[20].PickupOn = false;

        Assert.Equal(ControlMode.Button, rig.Control.Rows[6].Mode);
        Assert.Equal(ControlTrigger.RisingOnly, rig.Control.Rows[7].Trigger);
        Assert.Equal(5, rig.Control.Rows[0].Parameter);
        Assert.False(rig.Control.Rows[20].Pickup);
    }

    [Fact]
    public void A_channel_typed_in_the_box_is_saved()
    {
        using var rig = new ControlRig();

        rig.Vm.Rows[7].ChannelBox.Text = "40";

        Assert.Equal(40, rig.Control.Rows[7].Channel);
    }

    [Fact]
    public void A_repeated_channel_is_flagged_and_allowed()
    {
        using var rig = new ControlRig();

        rig.Vm.Rows[8].ChannelBox.Text = "8"; // both Cortar and Tocar on channel 8

        Assert.True(rig.Vm.Rows[7].IsDuplicate);
        Assert.True(rig.Vm.Rows[8].IsDuplicate);
        Assert.True(rig.Vm.HasDuplicates);
        Assert.False(rig.Vm.Rows[0].IsDuplicate);
    }

    [Fact]
    public void A_row_can_be_added_and_removed()
    {
        using var rig = new ControlRig();

        rig.Vm.AddRowCommand.Execute(null);
        Assert.Equal(22, rig.Vm.Rows.Count);
        Assert.Equal(22, rig.Control.Rows[21].Channel); // the first free channel

        rig.Vm.Rows[21].RemoveCommand.Execute(null);
        rig.Vm.Rows[0].RemoveCommand.Execute(null);
        Assert.Equal(20, rig.Vm.Rows.Count);
        Assert.Equal(2, rig.Control.Rows[0].Channel);
    }

    [Fact]
    public void Restore_asks_first_and_only_restores_on_yes()
    {
        using var rig = new ControlRig();
        rig.Vm.Rows[0].RemoveCommand.Execute(null);
        rig.Answer = false;

        rig.Vm.RestoreCommand.Execute(null);
        Assert.Single(rig.Asked);
        Assert.Equal(20, rig.Control.Rows.Count);

        rig.Answer = true;
        rig.Vm.RestoreCommand.Execute(null);
        Assert.Equal(21, rig.Control.Rows.Count);
        Assert.Equal(new ControlSettings().Rows, rig.Control.Rows);
        Assert.Equal(21, rig.Vm.Rows.Count);
    }

    [Fact]
    public void The_universe_the_offset_and_the_source_are_saved()
    {
        using var rig = new ControlRig();

        rig.Vm.UniverseBox.Text = "7";
        rig.Vm.UniverseOffset = false;
        rig.Vm.SourceIpText = "2.0.0.2";

        Assert.Equal(7, rig.Control.Universe);
        Assert.False(rig.Control.UniverseOffset);
        Assert.Equal("2.0.0.2", rig.Control.SourceIp);
        Assert.Equal(7, rig.Vm.ArtNetUniverse);
    }

    [Fact]
    public void A_source_that_is_not_an_address_is_marked_and_not_saved()
    {
        using var rig = new ControlRig();

        rig.Vm.SourceIpText = "2.0.0.2";
        rig.Vm.SourceIpText = "2.0.0.";

        Assert.True(rig.Vm.SourceIpInvalid);
        Assert.Equal("2.0.0.2", rig.Control.SourceIp);

        rig.Vm.SourceIpText = string.Empty; // empty means any source
        Assert.False(rig.Vm.SourceIpInvalid);
        Assert.Equal(string.Empty, rig.Control.SourceIp);
    }

    [Fact]
    public void Holyrics_rows_are_active_and_can_be_turned_off_and_on()
    {
        using var rig = new ControlRig();

        var row = rig.Vm.Rows[12];
        Assert.False(row.IsSoon);
        Assert.True(row.Enabled);

        row.Enabled = false;
        Assert.False(rig.Control.Rows[12].Enabled);
        rig.Vm.Rows[12].Enabled = true;
        Assert.True(rig.Control.Rows[12].Enabled);
    }

    [Fact]
    public void The_screen_follows_settings_changed_from_outside()
    {
        using var rig = new ControlRig();

        rig.Update(c => c with { Universe = 12, Rows = c.Rows.Take(3).ToList() });

        Assert.Equal("12", rig.Vm.UniverseBox.Text);
        Assert.Equal(3, rig.Vm.Rows.Count);
    }

    [Fact]
    public void The_help_card_lists_the_default_map_without_inventing_rows()
    {
        using var rig = new ControlRig();

        Assert.Equal(16, rig.Vm.HelpMap.Count); // 1-6 in one line, then channels 7..21 (15 lines)
        Assert.Contains(rig.Vm.HelpMap, line => line.StartsWith("1 a 6", StringComparison.Ordinal));
        Assert.Contains(rig.Vm.HelpMap, line => line.StartsWith("16 ", StringComparison.Ordinal) && line.Contains("Logo do Holyrics", StringComparison.Ordinal) && !line.Contains("em breve", StringComparison.Ordinal));
        Assert.Contains(rig.Vm.HelpMap, line => line.StartsWith("21 ", StringComparison.Ordinal));
    }
}
