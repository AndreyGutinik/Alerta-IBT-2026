using AlertaIBT.App.ViewModels;
using AlertaIBT.Core.Audio;
using AlertaIBT.Core.Settings;

namespace AlertaIBT.App.Tests.ViewModels;

// The Som tab's logic on the real engines with fakes at the edges: nothing here can touch a real volume, output, program or file.
public sealed class SomViewModelTests : IDisposable
{
    private SomRig _rig = new();

    public void Dispose() => _rig.Dispose();

    private void Reopen(AppSettings initial)
    {
        _rig.Dispose();
        _rig = new SomRig(initial);
    }

    private AudioSettings Audio => _rig.Current.Audio;

    private PlayerSettings Player => _rig.Current.Player;

    // ---- volumes ----

    [Fact]
    public void The_four_rows_start_with_the_church_values_and_the_boxes_say_the_same_as_the_sliders()
    {
        var som = _rig.Som;

        Assert.Equal([80d, 100d, 100d, 0d], new[] { som.MasterRow.Volume, som.HolyricsRow.Volume, som.PlayerRow.Volume, som.SystemSoundsRow.Volume });
        Assert.Equal(["80", "100", "100", "0"], new[] { som.MasterRow.Box.Text, som.HolyricsRow.Box.Text, som.PlayerRow.Box.Text, som.SystemSoundsRow.Box.Text });
        Assert.All(new[] { som.MasterRow, som.HolyricsRow, som.PlayerRow, som.SystemSoundsRow }, row =>
        {
            Assert.True(row.WithSound);
            Assert.False(row.IsMuted);
        });
        Assert.True(som.AdjustAtStart);
        Assert.True(som.LockOutput);
        Assert.True(som.HolyricsRoute);
    }

    [Fact]
    public void Moving_the_slider_updates_the_number_and_saves_only_after_the_600_ms_rest()
    {
        _rig.Som.MasterRow.Volume = 55;

        Assert.Equal("55", _rig.Som.MasterRow.Box.Text);
        Assert.Equal(80, Audio.MasterVolume); // still resting
        _rig.Rest();

        Assert.Equal(55, Audio.MasterVolume);
        Assert.Equal(55, _rig.Settings.OnDisk().Audio.MasterVolume);
    }

    [Fact]
    public void Every_volume_row_reaches_the_audio_the_moment_it_changes_before_the_autosave()
    {
        _rig.Audio.Sessions.Add(new AudioSessionInfo("holy", "javaw", false, 10));
        _rig.Audio.Sessions.Add(new AudioSessionInfo("sys", null, true, 0));

        _rig.Som.MasterRow.Volume = 61;
        _rig.Som.HolyricsRow.Volume = 42;
        _rig.Som.SystemSoundsRow.Volume = 9;

        Assert.Contains("master:61:False", _rig.Audio.VolumeCalls);
        Assert.Contains("session:holy:42:False", _rig.Audio.VolumeCalls);
        Assert.Contains("session:sys:9:False", _rig.Audio.VolumeCalls);
        Assert.Equal(80, Audio.MasterVolume); // the file still waits for its rest: the sound did not
        Assert.True(_rig.Settings.Saver.HasPending);
    }

    [Fact]
    public void Every_mute_icon_reaches_the_audio_at_once_and_keeps_the_slider_value()
    {
        _rig.Audio.Sessions.Add(new AudioSessionInfo("holy", "javaw", false, 10));

        _rig.Som.MasterRow.IsMuted = true;
        _rig.Som.HolyricsRow.IsMuted = true;
        _rig.Som.MasterRow.WithSound = true;

        Assert.Contains("master:80:True", _rig.Audio.VolumeCalls);
        Assert.Contains("session:holy:100:True", _rig.Audio.VolumeCalls);
        Assert.Equal("master:80:False", _rig.Audio.VolumeCalls.Last(c => c.StartsWith("master", StringComparison.Ordinal)));
    }

    [Fact]
    public void The_player_row_is_heard_at_once_and_its_mute_too()
    {
        _rig.Player.Play();
        var source = _rig.Backend.Outputs[^1].Source;
        float Level(int frames)
        {
            var buffer = new float[frames * 2];
            source.Read(buffer, 0, buffer.Length);
            return Math.Abs(buffer[^2]);
        }

        Level(9600);
        _rig.Som.PlayerRow.Volume = 30;
        var level = Level(4800); // 100 ms later

        Assert.Equal(100, Player.Volume); // not saved yet
        Assert.InRange(level, 0.5f * 0.3f - 0.005f, 0.5f * 0.3f + 0.005f);

        _rig.Som.PlayerRow.IsMuted = true;
        Assert.InRange(Level(4800), 0f, 0.0001f);
    }

    [Fact]
    public void The_ramp_menu_offers_the_five_values_defaults_to_dry_and_saves_the_choice()
    {
        Assert.Equal(["seco", "0,5 s", "1 s", "2 s", "3 s"], _rig.Som.VolumeRampChoices.Select(o => o.Label));
        Assert.Equal(0, _rig.Som.VolumeRampSeconds);

        _rig.Som.VolumeRampSeconds = 2;

        Assert.Equal(2, Audio.VolumeRampSeconds);
        Assert.Equal(2, _rig.Settings.OnDisk().Audio.VolumeRampSeconds);
    }

    [Fact]
    public void Typing_a_number_moves_the_slider_and_saves_and_garbage_is_never_saved_and_a_big_number_is_held_to_100()
    {
        _rig.Som.MasterRow.Box.Text = "70";
        Assert.Equal(70, _rig.Som.MasterRow.Volume);
        _rig.Rest();
        Assert.Equal(70, Audio.MasterVolume);

        _rig.Som.MasterRow.Box.Text = "7x";
        _rig.Rest();
        Assert.Equal(70, Audio.MasterVolume);
        _rig.Som.MasterRow.Box.Normalize();
        Assert.Equal("70", _rig.Som.MasterRow.Box.Text);

        _rig.Som.MasterRow.Box.Text = "150";
        _rig.Rest();
        Assert.Equal(100, Audio.MasterVolume);
        Assert.Equal(100, _rig.Som.MasterRow.Volume);
    }

    [Fact]
    public void Mudo_and_Com_som_are_the_two_halves_of_one_switch()
    {
        _rig.Som.MasterRow.IsMuted = true;
        _rig.Rest();

        Assert.True(Audio.MasterMuted);
        Assert.False(_rig.Som.MasterRow.WithSound);

        _rig.Som.MasterRow.WithSound = true;
        _rig.Rest();

        Assert.False(Audio.MasterMuted);
        Assert.False(_rig.Som.MasterRow.IsMuted);
    }

    [Fact]
    public void The_holyrics_row_writes_the_holyrics_entry_and_the_hidden_spotify_entry_is_left_alone()
    {
        _rig.Som.HolyricsRow.Volume = 40;
        _rig.Som.HolyricsRow.IsMuted = true;
        _rig.Rest();

        var holy = Audio.Apps.Single(a => a.Name == "Holyrics");
        Assert.Equal(40, holy.Volume);
        Assert.True(holy.Muted);
        Assert.Equal(["Holyrics", "javaw"], holy.ProcessNames);
        var spotify = Audio.Apps.Single(a => a.Name == "Spotify");
        Assert.Equal(100, spotify.Volume);
        Assert.False(spotify.Muted);
    }

    [Fact]
    public void The_player_row_writes_the_players_own_volume_and_mute_and_the_system_sounds_row_its_own()
    {
        _rig.Som.PlayerRow.Volume = 65;
        _rig.Som.PlayerRow.IsMuted = true;
        _rig.Som.SystemSoundsRow.Volume = 12;
        _rig.Rest();

        Assert.Equal(65, Player.Volume);
        Assert.True(Player.StartMuted);
        Assert.Equal(65, _rig.PlayerView.Volume);
        Assert.Equal(12, Audio.SystemSoundsVolume);
    }

    [Fact]
    public void A_change_made_elsewhere_moves_the_row_but_a_slider_being_dragged_does_not_jump_back()
    {
        _rig.Settings.Settings.Update(s => s with { Audio = s.Audio with { MasterVolume = 30 } });
        Assert.Equal(30, _rig.Som.MasterRow.Volume);
        Assert.Equal("30", _rig.Som.MasterRow.Box.Text);

        _rig.Som.MasterRow.Volume = 90; // dragging: waiting for its rest
        _rig.Settings.Settings.Update(s => s with { Audio = s.Audio with { LockOutput = false } }); // something else changes meanwhile

        Assert.Equal(90, _rig.Som.MasterRow.Volume);
        _rig.Rest();
        Assert.Equal(90, Audio.MasterVolume);
        Assert.False(Audio.LockOutput);
    }

    [Fact]
    public void The_adjust_at_start_switch_saves_its_own_setting()
    {
        _rig.Som.AdjustAtStart = false;
        _rig.Rest();

        Assert.False(Audio.Enabled);
    }

    [Fact]
    public async Task Apply_now_sets_the_volumes_that_were_typed_even_before_the_rest_and_shows_what_was_applied()
    {
        Reopen(new AppSettings { Audio = new AudioSettings { OutputMatch = ["Realtek"], Enabled = false } });
        _rig.BootAudio();
        _rig.Som.MasterRow.Box.Text = "42"; // typed, not rested yet

        await _rig.Som.ApplyNowCommand.ExecuteAsync(null);

        Assert.Contains("Volume geral 42%", _rig.Controller.Status.AppliedSummary, StringComparison.Ordinal);
        Assert.StartsWith("Aplicado ", _rig.Som.AppliedLine, StringComparison.Ordinal);
        Assert.True(_rig.Som.HasAppliedLine);
        Assert.False(_rig.Som.IsApplying);
    }

    // ---- outputs ----

    [Fact]
    public void A_device_list_has_the_automatic_choice_every_active_output_and_the_remembered_one_marked_desconectado()
    {
        AudioDevice[] active = [new("a", "Alto-falantes (Realtek)"), new("b", "Alto-falantes (WING-USB Out 3-4)")];

        var none = DeviceChoice.Build(active, null, null, "Autom\U000000E1tico (WING)");
        Assert.Equal(["Autom\U000000E1tico (WING)", "Alto-falantes (Realtek)", "Alto-falantes (WING-USB Out 3-4)"], none.Select(c => c.Display));
        Assert.True(none[0].IsAuto);
        Assert.Same(none[0], DeviceChoice.Selected(none, active, null, null));

        var gone = DeviceChoice.Build(active, "{old}", "Alto-falantes (WING-USB Out 1-2)", "Auto");
        Assert.Equal("Alto-falantes (WING-USB Out 1-2) (desconectado)", gone[^1].Display);
        Assert.False(gone[^1].Present);
        Assert.Same(gone[^1], DeviceChoice.Selected(gone, active, "{old}", "Alto-falantes (WING-USB Out 1-2)"));

        var found = DeviceChoice.Build(active, "b", "Alto-falantes (WING-USB Out 3-4)", "Auto");
        Assert.Equal(3, found.Count); // the remembered one is active: no "desconectado" line
        Assert.Equal("Alto-falantes (WING-USB Out 3-4)", DeviceChoice.Selected(found, active, "b", "x").Display);

        var anotherPort = DeviceChoice.Build(active, "{moved}", "Alto-falantes (2- WING-USB Out 3-4)", "Auto");
        Assert.Equal(3, anotherPort.Count);
        Assert.Equal("b", DeviceChoice.Selected(anotherPort, active, "{moved}", "Alto-falantes (2- WING-USB Out 3-4)").Id);
    }

    [Fact]
    public void Choosing_a_device_for_the_default_output_saves_id_name_and_a_name_rule_at_once_and_auto_goes_back_to_the_church_rule()
    {
        var row = _rig.Som.DefaultOutput;
        var device = row.Choices.First(c => c.Id == "id-1");

        row.Selected = device;

        Assert.Equal("id-1", Audio.OutputDeviceId); // saved now: no 600 ms wait
        Assert.Equal("Alto-falantes (Realtek)", Audio.OutputDeviceName);
        Assert.Equal(["Alto-falantes (Realtek)"], Audio.OutputMatch);

        row.Selected = row.Choices[0];

        Assert.Null(Audio.OutputDeviceId);
        Assert.Null(Audio.OutputDeviceName);
        Assert.Equal(["WING"], Audio.OutputMatch);
    }

    [Fact]
    public void Choosing_the_players_output_uses_the_players_own_rule_and_auto_goes_back_to_WING_and_3_4()
    {
        var row = _rig.Som.PlayerOutput;
        row.Selected = row.Choices.First(c => c.Id == "id-1");

        Assert.Equal("id-1", Player.OutputDeviceId);
        Assert.Equal(["Alto-falantes (Realtek)"], Player.OutputMatch);

        row.Selected = row.Choices[0];

        Assert.Null(Player.OutputDeviceId);
        Assert.Equal(["WING", "3-4"], Player.OutputMatch);
        Assert.Equal("Autom\U000000E1tico (WING + 3-4)", row.Choices[0].Display);
    }

    [Fact]
    public void Choosing_the_holyrics_output_saves_it_and_the_switch_saves_at_once_and_auto_is_WING_and_1_2()
    {
        var row = _rig.Som.HolyricsOutput;
        row.Selected = row.Choices.First(c => c.Id == "id-1");
        Assert.Equal("id-1", Audio.HolyricsDeviceId);
        Assert.Equal(["Alto-falantes (Realtek)"], Audio.HolyricsMatch);

        row.Selected = row.Choices[0];
        Assert.Null(Audio.HolyricsDeviceId);
        Assert.Equal(["WING", "1-2"], Audio.HolyricsMatch);
        Assert.Equal("Autom\U000000E1tico (WING + 1-2)", row.Choices[0].Display);

        _rig.Som.HolyricsRoute = false;
        Assert.False(Audio.HolyricsRoute);
        _rig.Som.LockOutput = false;
        Assert.False(Audio.LockOutput);
    }

    [Fact]
    public void The_status_lines_say_what_is_connected_what_is_waiting_and_what_the_player_will_do()
    {
        Reopen(new AppSettings
        {
            Audio = new AudioSettings { OutputDeviceId = "{gone}", OutputDeviceName = "Alto-falantes (WING-USB Out 1-2)", OutputMatch = ["Alto-falantes (WING-USB Out 1-2)"] },
        });
        _rig.BootAudio();

        Assert.StartsWith("WING-USB Out 1-2 \U00002014 desconectado, aguardando.", _rig.Som.DefaultOutput.Status, StringComparison.Ordinal);
        Assert.Contains("Sa\U000000EDda do player: WING + 3-4", _rig.Som.PlayerOutput.Status, StringComparison.Ordinal);
        Assert.Contains("desconectado, deixando o Holyrics como est\U000000E1", _rig.Som.HolyricsOutput.Status, StringComparison.Ordinal);
        Assert.Equal("Alto-falantes (WING-USB Out 1-2) (desconectado)", _rig.Som.DefaultOutput.Selected!.Display);
    }

    [Fact]
    public void With_the_lock_off_the_status_says_windows_chooses()
    {
        Reopen(new AppSettings { Audio = new AudioSettings { LockOutput = false } });
        _rig.BootAudio();

        Assert.StartsWith("Travamento desligado:", _rig.Som.DefaultOutput.Status, StringComparison.Ordinal);
    }

    // ---- playlists ----

    [Fact]
    public void Adding_a_playlist_asks_for_a_folder_names_it_after_it_makes_the_name_unique_and_selects_it_and_cancel_adds_nothing()
    {
        _rig.Folders.Enqueue(@"C:\Musica\Louvor");
        _rig.Som.AddPlaylistCommand.Execute(null);

        Assert.Equal(["Igreja", "Louvor"], Player.Playlists.Select(p => p.Name));
        Assert.Equal(@"C:\Musica\Louvor", Player.FindPlaylist("Louvor")!.Folder);
        Assert.Equal("Louvor", Player.CurrentPlaylist);

        _rig.Folders.Enqueue(@"D:\Outra\Louvor\");
        _rig.Som.AddPlaylistCommand.Execute(null);
        Assert.Equal("Louvor 2", Player.CurrentPlaylist);

        _rig.Som.AddPlaylistCommand.Execute(null); // the picker answers cancel
        Assert.Equal(3, Player.Playlists.Count);
    }

    [Fact]
    public void Renaming_a_playlist_keeps_the_selection_and_the_alarms_that_play_it_and_a_taken_name_is_refused()
    {
        _rig.Folders.Enqueue(@"C:\Musica\Louvor");
        _rig.Som.AddPlaylistCommand.Execute(null);
        _rig.PlayerView.UpdateAlarm(Player.Alarms[0] with { Playlist = "Louvor" });

        // the name box opens with the current name; a name another list already has (any case) is refused, and so is an empty one
        Assert.Equal("J\U000000E1 existe uma lista com esse nome.", _rig.Som.PlaylistNameProblem("igreja", "Louvor"));
        Assert.Equal("Digite um nome.", _rig.Som.PlaylistNameProblem("   ", "Louvor"));
        Assert.Null(_rig.Som.PlaylistNameProblem("Louvor", "Louvor")); // the same name is not a clash with itself
        Assert.Null(_rig.Som.PlaylistNameProblem("LOUVOR", "Louvor"));

        _rig.Names.Enqueue("igreja"); // refused: nothing is renamed
        _rig.Som.RenamePlaylistCommand.Execute(null);
        Assert.Equal(("Renomear a lista", "Louvor"), _rig.NameRequests[0]);
        Assert.Equal("Louvor", Player.Playlists[1].Name);

        _rig.Names.Enqueue("  Louvor de domingo ");
        _rig.Som.RenamePlaylistCommand.Execute(null);

        Assert.Equal("Louvor de domingo", _rig.PlayerView.CurrentPlaylist);
        Assert.Equal(["Igreja", "Louvor de domingo"], _rig.PlayerView.Playlists.Select(p => p.Name));
        Assert.Equal("Louvor de domingo", _rig.PlayerView.Alarms[0].Playlist);

        _rig.Som.RenamePlaylistCommand.Execute(null); // the box is cancelled
        Assert.Equal("Louvor de domingo", _rig.PlayerView.CurrentPlaylist);
    }

    [Fact]
    public void Deleting_a_playlist_asks_first_says_only_the_list_goes_and_No_keeps_it()
    {
        _rig.Folders.Enqueue(@"C:\Musica\Louvor");
        _rig.Som.AddPlaylistCommand.Execute(null);

        _rig.ConfirmAnswer = false;
        _rig.Som.RemovePlaylistCommand.Execute(null);

        Assert.Equal(2, _rig.PlayerView.Playlists.Count);
        var (title, message) = Assert.Single(_rig.Confirmations);
        Assert.Contains("Louvor", title, StringComparison.Ordinal);
        Assert.Contains("continuam na pasta", message, StringComparison.Ordinal);

        _rig.ConfirmAnswer = true;
        _rig.Som.RemovePlaylistCommand.Execute(null);
        Assert.Equal(["Igreja"], _rig.PlayerView.Playlists.Select(p => p.Name));
    }

    [Fact]
    public void Removing_the_selected_playlist_moves_to_the_first_left_and_removing_the_last_leaves_none()
    {
        _rig.Folders.Enqueue(@"C:\Musica\Louvor");
        _rig.Som.AddPlaylistCommand.Execute(null);

        _rig.Som.RemovePlaylistCommand.Execute(null);
        Assert.Equal(["Igreja"], Player.Playlists.Select(p => p.Name));
        Assert.Equal("Igreja", Player.CurrentPlaylist);

        _rig.Som.RemovePlaylistCommand.Execute(null);
        Assert.Empty(Player.Playlists);
        Assert.False(_rig.Som.HasPlaylist);
    }

    [Fact]
    public void Changing_the_folder_of_the_selected_playlist_and_the_info_line_follow()
    {
        Assert.StartsWith(PlayerSettings.DefaultPlaylistFolder, _rig.Som.PlaylistInfo, StringComparison.Ordinal);

        _rig.Folders.Enqueue(@"E:\Nova");
        _rig.Som.ChangeFolderCommand.Execute(null);

        Assert.Equal(@"E:\Nova", Player.FindPlaylist("Igreja")!.Folder);
        Assert.StartsWith(@"E:\Nova", _rig.Som.PlaylistInfo, StringComparison.Ordinal);

        _rig.Som.ChangeFolderCommand.Execute(null); // cancelled
        Assert.Equal(@"E:\Nova", Player.FindPlaylist("Igreja")!.Folder);
    }

    [Fact]
    public void The_fade_and_crossfade_dropdowns_offer_the_allowed_values_and_follow_the_settings()
    {
        Assert.Equal(["seco", "0,5 s", "1 s", "2 s", "3 s", "4 s", "5 s", "6 s", "7 s", "8 s", "9 s", "10 s"], _rig.Som.FadeInChoices.Select(o => o.Label));
        Assert.Equal(_rig.Som.FadeInChoices.Select(o => o.Seconds), _rig.Som.FadeOutChoices.Select(o => o.Seconds));
        Assert.Equal(["sem mescla", "0,5 s", "1 s", "2 s", "3 s", "4 s", "5 s", "6 s", "7 s", "8 s", "9 s", "10 s", "11 s", "12 s"], _rig.Som.CrossfadeChoices.Select(o => o.Label));
        Assert.Equal(new double[] { 0, 0.5, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 }, _rig.Som.CrossfadeChoices.Select(o => o.Seconds));
        Assert.Equal(0, Player.FadeInSeconds);
        Assert.Equal(5, Player.FadeOutSeconds);
        Assert.Equal(0, Player.CrossfadeSeconds);

        _rig.PlayerView.FadeOutSeconds = 0.5;
        _rig.PlayerView.FadeInSeconds = 0;
        _rig.PlayerView.CrossfadeSeconds = 7;

        var saved = _rig.Settings.Settings.Current.Player;
        Assert.Equal(0.5, saved.FadeOutSeconds);
        Assert.Equal(0, saved.FadeInSeconds);
        Assert.Equal(7, saved.CrossfadeSeconds);
    }

    // ---- alarms ----

    [Fact]
    public void The_alarm_list_follows_the_settings_and_adding_and_removing_work()
    {
        Assert.Equal(["domingo"], _rig.Som.Alarms.Select(a => a.Id));
        Assert.Equal("18:00", _rig.Som.Alarms[0].StartText);
        Assert.Equal("18:25", _rig.Som.Alarms[0].LatestText);
        Assert.Equal([true, false, false, false, false, false, false], _rig.Som.Alarms[0].Days.Select(d => d.IsOn));

        _rig.Som.AddAlarmCommand.Execute(null);
        Assert.Equal(2, _rig.Som.Alarms.Count);
        Assert.Equal(2, Player.Alarms.Count);
        Assert.Equal("Igreja", Player.Alarms[1].Playlist);

        _rig.Som.Alarms[1].RemoveCommand.Execute(null);
        Assert.Single(_rig.Som.Alarms);
        Assert.Equal("domingo", Player.Alarms[0].Id);
    }

    [Fact]
    public void Editing_an_alarm_saves_a_complete_time_ignores_half_typed_ones_and_goes_back_when_the_box_loses_the_focus()
    {
        var row = _rig.Som.Alarms[0];
        _rig.PlayerView.UpdateAlarm(Player.Alarms[0] with { LastFiredOn = "2026-10-25" });

        row.StartText = "19:4"; // still typing
        Assert.Equal("18:00", Player.Alarms[0].Start);
        row.StartText = "19:45";
        Assert.Equal("19:45", Player.Alarms[0].Start);
        Assert.Null(Player.Alarms[0].LastFiredOn); // a new time can fire again today
        Assert.Equal("20:10", Player.Alarms[0].Latest); // "nao iniciar depois de" cannot be before the start: the settings move it 25 minutes after it
        Assert.Equal("20:10", row.LatestText); // and the box follows

        row.StartText = "9:05"; // H:mm is a time too, saved as HH:mm
        Assert.Equal("09:05", Player.Alarms[0].Start);

        row.LatestText = "25:99";
        Assert.Equal("20:10", Player.Alarms[0].Latest);
        row.NormalizeLatest();
        Assert.Equal("20:10", row.LatestText);

        row.StartText = "xx";
        row.NormalizeStart();
        Assert.Equal("09:05", row.StartText);
    }

    [Fact]
    public void The_weekday_chips_the_switch_and_the_playlist_of_an_alarm_save_through_the_player()
    {
        var row = _rig.Som.Alarms[0];

        row.Days[3].IsOn = true; // Wednesday (Q)
        row.Days[0].IsOn = false; // no more Sunday
        row.Enabled = false;
        _rig.Folders.Enqueue(@"C:\Musica\Louvor");
        _rig.Som.AddPlaylistCommand.Execute(null);
        row.Playlist = "Louvor";

        var alarm = Player.Alarms[0];
        Assert.True(alarm.HasDay(DayOfWeek.Wednesday));
        Assert.False(alarm.HasDay(DayOfWeek.Sunday));
        Assert.False(alarm.Enabled);
        Assert.Equal("Louvor", alarm.Playlist);
        Assert.Equal(["Igreja", "Louvor"], row.PlaylistNames);
        Assert.Equal([false, false, false, true, false, false, false], row.Days.Select(d => d.IsOn));
    }

    [Fact]
    public void The_next_alarm_line_comes_from_the_scheduler_and_the_alarms_switch_saves()
    {
        Assert.StartsWith("Pr\U000000F3ximo despertador:", _rig.Som.Player.NextAlarmText, StringComparison.Ordinal);

        _rig.Som.Player.AlarmsEnabled = false;
        Assert.False(Player.AlarmsEnabled);
    }

    [Fact]
    public void The_alarm_grid_is_the_alarms_then_the_plus_block_which_has_a_word_only_while_there_is_no_alarm()
    {
        Assert.Equal(2, _rig.Som.AlarmTiles.Count);
        Assert.Same(_rig.Som.Alarms[0], _rig.Som.AlarmTiles[0]);
        var tile = Assert.IsType<AddAlarmTile>(_rig.Som.AlarmTiles[^1]);
        Assert.False(tile.ShowLabel);

        _rig.Som.AddAlarmCommand.Execute(null);
        Assert.Equal(3, _rig.Som.AlarmTiles.Count);
        Assert.Same(_rig.Som.Alarms[1], _rig.Som.AlarmTiles[1]);
        Assert.Same(tile, _rig.Som.AlarmTiles[^1]); // the plus block stays last

        _rig.Som.Alarms[1].RemoveCommand.Execute(null);
        _rig.Som.Alarms[0].RemoveCommand.Execute(null);
        Assert.Single(_rig.Som.AlarmTiles);
        Assert.True(tile.ShowLabel);
        tile.AddCommand.Execute(null);
        Assert.Equal(2, _rig.Som.AlarmTiles.Count);
        Assert.False(tile.ShowLabel);
    }

    [Fact]
    public void Removing_an_alarm_asks_first_and_No_keeps_it()
    {
        _rig.Som.AddAlarmCommand.Execute(null);
        _rig.ConfirmAnswer = false;
        _rig.Som.Alarms[1].RemoveCommand.Execute(null);

        Assert.Equal(2, _rig.Som.Alarms.Count);
        var (title, message) = Assert.Single(_rig.Confirmations);
        Assert.Equal("Remover este despertador?", title);
        Assert.Contains("18:00", message, StringComparison.Ordinal);

        _rig.ConfirmAnswer = true;
        _rig.Som.Alarms[1].RemoveCommand.Execute(null);
        Assert.Single(_rig.Som.Alarms);
    }

    [Fact]
    public void The_next_alarm_of_the_header_is_short()
    {
        Assert.StartsWith("pr\U000000F3ximo: ", _rig.Som.NextAlarmShort, StringComparison.Ordinal);
        Assert.DoesNotContain("despertador", _rig.Som.NextAlarmShort, StringComparison.Ordinal);

        _rig.Som.Player.AlarmsEnabled = false;
        Assert.Equal("Despertadores desligados", _rig.Som.NextAlarmShort); // what is not a next alarm is shown as it is
    }

    [Fact]
    public void The_volume_rows_know_their_short_name_icon_and_tooltip_and_the_header_tips_follow_the_master()
    {
        Assert.Equal(["Windows", "Holyrics", "Player", "Sons do sistema"], _rig.Som.VolumeRows.Select(r => r.Label));
        Assert.Equal(["LucideMonitor", "LucidePresentation", "LucideMusic", "LucideBell"], _rig.Som.VolumeRows.Select(r => r.IconKey));
        Assert.All(_rig.Som.VolumeRows, row => Assert.Contains(row.Label, row.Tip, StringComparison.Ordinal));
        Assert.Same(_rig.Som.MasterRow, _rig.Som.VolumeRows[0]);

        Assert.Equal(AlertaIBT.Core.Services.StatusLight.Green, _rig.Som.AdjustLight);
        Assert.StartsWith("Ajustar o som ao iniciar: ligado", _rig.Som.AdjustSwitchTip, StringComparison.Ordinal);
        _rig.Som.AdjustAtStart = false;
        Assert.Equal(AlertaIBT.Core.Services.StatusLight.Gray, _rig.Som.AdjustLight);
        Assert.StartsWith("Ajuste autom\U000000E1tico desligado", _rig.Som.AdjustStatusTip, StringComparison.Ordinal);
        Assert.StartsWith("Aplicar agora", _rig.Som.ApplyTip, StringComparison.Ordinal);
    }

    [Fact]
    public void The_holyrics_dot_is_gray_yellow_red_or_green_from_what_the_audio_controller_says()
    {
        AudioStatus With(string? text) => AudioStatus.Empty with { Known = true, HolyricsText = text };

        Assert.Equal(AlertaIBT.Core.Services.StatusLight.Gray, SomViewModel.HolyricsLight(AudioStatus.Empty));
        Assert.Equal(AlertaIBT.Core.Services.StatusLight.Gray, SomViewModel.HolyricsLight(With(null)));
        Assert.Equal(AlertaIBT.Core.Services.StatusLight.Yellow, SomViewModel.HolyricsLight(With("WING \U00002014 desconectado, deixando o Holyrics como est\U000000E1")));
        Assert.Equal(AlertaIBT.Core.Services.StatusLight.Red, SomViewModel.HolyricsLight(With("WING \U00002014 sem acesso \U000000E0 sa\U000000EDda por programa neste Windows")));
        Assert.Equal(AlertaIBT.Core.Services.StatusLight.Red, SomViewModel.HolyricsLight(With("WING \U00002014 n\U000000E3o foi poss\U000000EDvel mudar a sa\U000000EDda do Holyrics")));
        Assert.Equal(AlertaIBT.Core.Services.StatusLight.Green, SomViewModel.HolyricsLight(With("WING \U00002014 aguardando o Holyrics tocar")));
        Assert.Equal(AlertaIBT.Core.Services.StatusLight.Green, SomViewModel.HolyricsLight(With("Holyrics toca em WING")));
    }

    [Fact]
    public void The_playlist_folder_alone_is_what_the_strip_shows_and_the_info_keeps_the_count_for_the_tooltip()
    {
        Assert.Equal(PlayerSettings.DefaultPlaylistFolder, _rig.Som.PlaylistFolder);
        Assert.Contains(PlayerSettings.DefaultPlaylistFolder, _rig.Som.PlaylistInfo, StringComparison.Ordinal);
        Assert.Contains("m\U000000FAsicas", _rig.Som.PlaylistInfo, StringComparison.Ordinal);

        _rig.Folders.Enqueue(@"E:\Nova");
        _rig.Som.ChangeFolderCommand.Execute(null);
        Assert.Equal(@"E:\Nova", _rig.Som.PlaylistFolder);
    }

    [Fact]
    public void Renaming_through_the_player_view_model_refuses_a_missing_playlist_a_blank_name_and_a_taken_name()
    {
        Assert.False(_rig.PlayerView.RenamePlaylist("nope", "x"));
        Assert.False(_rig.PlayerView.RenamePlaylist("Igreja", " "));
        _rig.PlayerView.AddPlaylist("Outra", @"C:\o");
        Assert.False(_rig.PlayerView.RenamePlaylist("Igreja", "OUTRA"));
        Assert.True(_rig.PlayerView.RenamePlaylist("Igreja", "IGREJA")); // only the case changes
        Assert.Equal(["IGREJA", "Outra"], Player.Playlists.Select(p => p.Name));
        Assert.Equal("IGREJA", Player.CurrentPlaylist);
    }
}
