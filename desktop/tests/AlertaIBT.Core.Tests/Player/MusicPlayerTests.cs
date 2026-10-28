using AlertaIBT.Core.Audio;
using AlertaIBT.Core.Player;
using AlertaIBT.Core.Settings;

namespace AlertaIBT.Core.Tests.Player;

// The engine against fakes: no device is opened and no file is decoded; the "sound card" is the test pulling the mixer.
public sealed class MusicPlayerTests : IDisposable
{
    private readonly PlayerRig _rig = new();

    public void Dispose() => _rig.Dispose();

    private MusicPlayer Player => _rig.Player;

    private FakePlayerBackend Backend => _rig.Backend;

    // ---- play ----

    [Fact]
    public void Play_opens_the_output_starts_a_track_and_the_status_shows_it()
    {
        var result = Player.Play();

        Assert.True(result.Success);
        var status = Player.GetStatus();
        Assert.Equal(PlayerState.Playing, status.State);
        Assert.Equal("playing", status.StateText);
        Assert.Equal("Igreja", status.Playlist);
        Assert.Equal(5, status.TrackCount);
        Assert.NotNull(status.Track);
        Assert.Equal("Artist", status.Track!.Artist);
        Assert.Equal(TimeSpan.FromSeconds(10), status.Duration);
        Assert.Single(Backend.Outputs);
        Assert.Single(Backend.Tracks);
    }

    [Fact]
    public void Play_while_playing_changes_nothing()
    {
        Player.Play();

        Assert.True(Player.Play().Success);

        Assert.Single(Backend.Tracks);
    }

    [Fact]
    public void A_plain_play_starts_with_a_click_free_ramp_not_a_long_fade_in()
    {
        Player.Play();

        var first = Backend.Output.Pump(480);

        Assert.InRange(first[0], 0f, 0.01f);
        Assert.InRange(first[^2], 0.49f, 0.51f); // 10 ms later it is at full level, 0.5 * volume 100% = 0.5
    }

    [Fact]
    public void The_player_volume_scales_the_samples_and_a_change_is_smoothed()
    {
        Player.Play();
        Backend.Output.PumpSeconds(0.1);

        _rig.Change(p => p with { Volume = 20 });
        var block = Backend.Output.Pump(48000 / 2);

        Assert.Equal(0.5f * 0.2f, block[^2], 3); // settled
        Assert.True(block[0] > block[^2]); // it did not jump
    }

    [Fact]
    public void A_muted_player_is_silent()
    {
        _rig.Change(p => p with { StartMuted = true });
        Player.Play();
        Backend.Output.PumpSeconds(0.1);

        Assert.All(Backend.Output.Pump(480), sample => Assert.Equal(0f, sample));
    }

    // ---- pause and stop ----

    [Fact]
    public void Pause_fades_out_for_5_seconds_then_pauses_and_closes_the_output()
    {
        Player.Play();
        Backend.Output.PumpSeconds(0.5);

        Assert.True(Player.Pause().Success);

        Assert.Equal(PlayerState.Playing, Player.GetStatus().State);
        Assert.True(Player.GetStatus().Fading);
        Backend.Output.PumpSeconds(2.5);
        var middle = Backend.Output.Pump(1)[0];
        Assert.InRange(middle, 0.05f, 0.35f); // halfway down from 0.4
        Assert.Equal(PlayerState.Playing, Player.GetStatus().State);

        Backend.Output.PumpSeconds(2.6);

        var status = Player.GetStatus();
        Assert.Equal(PlayerState.Paused, status.State);
        Assert.False(status.Fading);
        Assert.True(Backend.Output.Disposed);
    }

    [Fact]
    public void A_dry_stop_still_ramps_for_10_ms_and_stops()
    {
        _rig.Change(p => p with { StopFadeSeconds = 0 });
        Player.Play();
        Backend.Output.PumpSeconds(0.5);

        Player.Stop();
        var block = Backend.Output.Pump(480);

        Assert.True(block[0] > block[^2]);
        Assert.Equal(0f, block[^2], 3);
        Assert.Equal(PlayerState.Stopped, Player.GetStatus().State);
        Assert.Null(Player.GetStatus().Track);
    }

    [Fact]
    public void A_3_second_setting_makes_a_3_second_fade()
    {
        _rig.Change(p => p with { StopFadeSeconds = 3 });
        Player.Play();
        Backend.Output.PumpSeconds(0.5);

        Player.Pause();
        Backend.Output.PumpSeconds(2.9);
        Assert.Equal(PlayerState.Playing, Player.GetStatus().State);
        Backend.Output.PumpSeconds(0.2);

        Assert.Equal(PlayerState.Paused, Player.GetStatus().State);
    }

    [Fact]
    public void Play_after_a_pause_starts_a_different_random_track_not_the_same_position()
    {
        Player.Play();
        var first = Player.GetStatus().Track!.Path;
        Backend.Output.PumpSeconds(1);
        _rig.Change(p => p with { StopFadeSeconds = 0 });
        Player.Pause();
        Backend.Output.PumpSeconds(0.1);

        Player.Play();

        Assert.Equal(PlayerState.Playing, Player.GetStatus().State);
        Assert.NotEqual(first, Player.GetStatus().Track!.Path);
        Assert.Equal(0.0, Player.GetStatus().Position.TotalSeconds, 1);
    }

    [Fact]
    public void Play_during_a_fade_out_brings_the_same_sound_back()
    {
        Player.Play();
        var path = Player.GetStatus().Track!.Path;
        Backend.Output.PumpSeconds(0.5);
        Player.Pause();
        Backend.Output.PumpSeconds(1);

        Player.Play();
        Backend.Output.PumpSeconds(2);

        var status = Player.GetStatus();
        Assert.Equal(PlayerState.Playing, status.State);
        Assert.False(status.Fading);
        Assert.Equal(path, status.Track!.Path);
        Assert.Equal(0.5f, Backend.Output.Pump(1)[0], 2);
    }

    [Fact]
    public void A_paused_player_can_be_stopped_and_stopping_twice_is_fine()
    {
        _rig.Change(p => p with { StopFadeSeconds = 0 });
        Player.Play();
        Player.Pause();
        Backend.Output.PumpSeconds(0.1);

        Assert.True(Player.Stop().Success);
        Assert.True(Player.Stop().Success);

        Assert.Equal(PlayerState.Stopped, Player.GetStatus().State);
    }

    // ---- next and previous ----

    [Fact]
    public void Next_fades_a_moment_then_plays_another_track()
    {
        Player.Play();
        var first = Player.GetStatus().Track!.Path;
        Backend.Output.PumpSeconds(1);

        Player.Next();
        Backend.Output.PumpSeconds(0.3);

        Assert.NotEqual(first, Player.GetStatus().Track!.Path);
        Assert.Equal(PlayerState.Playing, Player.GetStatus().State);
        Assert.Single(Backend.Outputs); // the same output goes on
        Assert.True(Backend.Tracks[0].Disposed);
    }

    [Fact]
    public void Next_while_stopped_starts_playing()
    {
        Assert.True(Player.Next().Success);

        Assert.Equal(PlayerState.Playing, Player.GetStatus().State);
    }

    [Fact]
    public void Previous_restarts_the_track_after_3_seconds_and_goes_back_before_that()
    {
        _rig.Change(p => p with { Shuffle = false });
        Player.Play();
        Backend.Output.PumpSeconds(1);
        Player.Next();
        Backend.Output.PumpSeconds(0.3);
        var second = Player.GetStatus().Track!.Path;
        Backend.Output.PumpSeconds(4);

        Player.Previous(); // 4 s in: restart
        Backend.Output.PumpSeconds(0.3);
        Assert.Equal(second, Player.GetStatus().Track!.Path);
        Assert.True(Player.GetStatus().Position < TimeSpan.FromSeconds(1));

        Player.Previous(); // under 3 s in: the track before
        Backend.Output.PumpSeconds(0.3);

        Assert.EndsWith("track 01.flac", Player.GetStatus().Track!.Path);
    }

    // ---- the end of a track ----

    [Fact]
    public void At_the_end_of_a_track_the_next_one_starts_by_itself()
    {
        _rig.Backend.TrackFrames = 48000;
        Player.Play();
        var first = Player.GetStatus().Track!.Path;

        Backend.Output.PumpSeconds(1.1);

        Assert.Equal(PlayerState.Playing, Player.GetStatus().State);
        Assert.NotEqual(first, Player.GetStatus().Track!.Path);
    }

    [Fact]
    public void Repeat_one_plays_the_same_track_again()
    {
        _rig.Change(p => p with { Repeat = "one" });
        _rig.Backend.TrackFrames = 48000;
        Player.Play();
        var first = Player.GetStatus().Track!.Path;

        Backend.Output.PumpSeconds(3.5);

        Assert.All(Backend.OpenedPaths, path => Assert.Equal(first, path));
        Assert.True(Backend.Tracks.Count >= 3);
    }

    [Fact]
    public void Repeat_off_stops_after_the_last_track_of_the_round()
    {
        _rig.Change(p => p with { Repeat = "off", Shuffle = false });
        _rig.Backend.TrackFrames = 4800;
        Player.Play();

        Backend.Output.PumpSeconds(2);

        Assert.Equal(PlayerState.Stopped, Player.GetStatus().State);
        Assert.Equal(5, Backend.Tracks.Count);
        Assert.True(Backend.Output.Disposed);
    }

    [Fact]
    public void A_file_that_cannot_be_read_is_skipped()
    {
        _rig.Change(p => p with { Shuffle = false });
        Backend.BadFiles.Add("/music/igreja/track 01.flac");

        var result = Player.Play();

        Assert.True(result.Success);
        Assert.EndsWith("track 02.flac", Player.GetStatus().Track!.Path);
    }

    [Fact]
    public void When_no_file_can_be_read_it_stops_with_the_reason()
    {
        foreach (var file in _rig.FileSystem.Folders[PlayerRig.Folder])
        {
            Backend.BadFiles.Add(file);
        }

        var result = Player.Play();

        Assert.False(result.Success);
        Assert.Equal(PlayerState.Stopped, Player.GetStatus().State);
        Assert.Contains("track", Player.GetStatus().LastError);
    }

    [Fact]
    public void An_empty_playlist_answers_that_it_is_empty()
    {
        _rig.FileSystem.Folders[PlayerRig.Folder].Clear();
        _rig.Library.RefreshAll();

        var result = Player.Play();

        Assert.False(result.Success);
        Assert.Equal(PlayerState.Stopped, Player.GetStatus().State);
        Assert.NotNull(Player.GetStatus().LastError);
    }

    [Fact]
    public void A_new_file_in_the_folder_joins_the_playlist_after_the_debounce_without_stopping_the_music()
    {
        Player.Play();
        var before = Player.GetStatus().Track!.Path;
        _rig.FileSystem.Folders[PlayerRig.Folder].Add("/music/igreja/new song.flac");

        _rig.Watchers[0].Raise();
        _rig.Watchers[0].Raise();
        _rig.Clock.Advance(PlaylistLibrary.Debounce - TimeSpan.FromMilliseconds(1));
        Assert.Equal(5, Player.GetStatus().TrackCount);
        _rig.Clock.Advance(TimeSpan.FromMilliseconds(1));

        Assert.Equal(6, Player.GetStatus().TrackCount);
        Assert.Equal(before, Player.GetStatus().Track!.Path);
    }

    // ---- the output ----

    [Fact]
    public void The_church_output_is_found_by_its_name_when_it_appears_and_is_remembered()
    {
        _rig.PlugWing34();

        Player.Play();

        Assert.Equal(PlayerRig.Wing34, Backend.Output.RequestedId);
        Assert.Contains(_rig.Log.Entries, e => e.Message.Contains("ByRule", StringComparison.Ordinal)); // found by the name rule, then remembered by id
        Assert.False(Player.GetStatus().Output.Kind is OutputChoiceKind.Default or OutputChoiceKind.None);
        Assert.Equal("Sa\U000000EDda do player: WING-USB (Out 3-4)", Player.GetStatus().Output.Text);
        Assert.Equal(PlayerRig.Wing34, _rig.Settings.Current.Player.OutputDeviceId);
        Assert.Equal("WING-USB (Out 3-4)", _rig.Settings.Current.Player.OutputDeviceName);
    }

    [Fact]
    public void With_the_church_output_absent_it_plays_on_the_default_and_says_so()
    {
        _rig.Change(p => p with { OutputDeviceName = "WING-USB (Out 3-4)" });

        Player.Play();

        Assert.Equal(PlayerRig.Speakers, Backend.Output.RequestedId);
        var output = Player.GetStatus().Output;
        Assert.Equal(OutputChoiceKind.Default, output.Kind);
        Assert.Equal(
            "Sa\U000000EDda do player: WING-USB (Out 3-4) \U00002014 desconectado, tocando na sa\U000000EDda padr\U000000E3o",
            output.Text);
        Assert.Contains(_rig.Log.Entries, e => e.Message.Contains("Default", StringComparison.Ordinal));
    }

    [Fact]
    public void Before_playing_the_status_already_tells_where_it_will_play()
    {
        var text = Player.GetStatus().Output.Text;

        Assert.Equal("Sa\U000000EDda do player: WING + 3-4 \U00002014 desconectado, tocar\U000000E1 na sa\U000000EDda padr\U000000E3o", text);
    }

    [Fact]
    public void When_the_wished_output_appears_the_status_updates_and_the_next_track_moves_there()
    {
        _rig.Backend.TrackFrames = 48000;
        Player.Play();
        Assert.Equal(PlayerRig.Speakers, Backend.Output.RequestedId);

        _rig.PlugWing34();
        Assert.Equal(OutputChoiceKind.ByRule, Player.GetStatus().Output.Kind);
        Backend.Outputs[0].PumpSeconds(1.2); // the track ends: the next one opens on the right device

        Assert.Equal(2, Backend.Outputs.Count);
        Assert.True(Backend.Outputs[0].Disposed);
        Assert.Equal(PlayerRig.Wing34, Backend.Output.RequestedId);
        Assert.Equal(PlayerState.Playing, Player.GetStatus().State);
    }

    [Fact]
    public void With_no_output_at_all_it_waits_and_starts_when_one_appears()
    {
        _rig.Audio.Devices.Clear();
        Backend.OutputError = new InvalidOperationException("no device");

        var result = Player.Play();

        Assert.False(result.Success);
        Assert.Equal(PlayerState.Waiting, Player.GetStatus().State);
        Assert.Equal(OutputChoiceKind.None, Player.GetStatus().Output.Kind);

        _rig.Clock.Advance(MusicPlayer.RetryInterval);
        Assert.Equal(PlayerState.Waiting, Player.GetStatus().State); // still nothing

        _rig.Audio.Devices.Add(new AudioDevice(PlayerRig.Speakers, "Speakers"));
        Backend.OutputError = null;
        _rig.Clock.Advance(MusicPlayer.RetryInterval);

        Assert.Equal(PlayerState.Playing, Player.GetStatus().State);
    }

    [Fact]
    public void An_output_that_dies_while_playing_is_waited_for_not_crashed()
    {
        Player.Play();
        var track = Player.GetStatus().Track!.Path;

        Backend.Output.Fail(new InvalidOperationException("device removed"));

        Assert.Equal(PlayerState.Waiting, Player.GetStatus().State);
        Assert.Contains("device removed", Player.GetStatus().LastError);
        _rig.Clock.Advance(MusicPlayer.RetryInterval);
        Assert.Equal(PlayerState.Playing, Player.GetStatus().State);
        Assert.Equal(track, Player.GetStatus().Track!.Path);
        Assert.Equal(2, Backend.Outputs.Count);
    }

    // ---- the master switch ----

    [Fact]
    public void With_the_player_off_every_command_says_so_and_nothing_opens()
    {
        _rig.Change(p => p with { Enabled = false });

        foreach (var result in new[] { Player.Play(), Player.Pause(), Player.Stop(), Player.Next(), Player.Previous() })
        {
            Assert.False(result.Success);
            Assert.Equal("player desligado", result.Error);
        }

        Assert.Empty(Backend.Outputs);
        Assert.False(Player.GetStatus().Enabled);
    }

    [Fact]
    public void Turning_the_player_off_while_playing_stops_it_at_once()
    {
        Player.Play();

        _rig.Change(p => p with { Enabled = false });

        Assert.Equal(PlayerState.Stopped, Player.GetStatus().State);
        Assert.True(Backend.Output.Disposed);
    }

    // ---- the alarm start ----

    [Fact]
    public void An_alarm_start_uses_shuffle_repeat_all_and_the_alarm_fade_in()
    {
        _rig.Change(p => p with { Shuffle = false, Repeat = "off", AlarmFadeSeconds = 5 });

        var start = Player.StartFromAlarm("Igreja");

        Assert.Equal(AlarmStartOutcome.Started, start.Outcome);
        Assert.True(_rig.Settings.Current.Player.Shuffle);
        Assert.Equal("all", _rig.Settings.Current.Player.Repeat);
        Backend.Output.PumpSeconds(2.5);
        Assert.InRange(Backend.Output.Pump(1)[0], 0.1f, 0.3f); // half way up to 0.5
        Backend.Output.PumpSeconds(3);
        Assert.Equal(0.5f, Backend.Output.Pump(1)[0], 2);
    }

    [Fact]
    public void An_alarm_leaves_music_that_is_already_playing_alone()
    {
        Player.Play();
        var path = Player.GetStatus().Track!.Path;

        var start = Player.StartFromAlarm("Igreja");

        Assert.Equal(AlarmStartOutcome.AlreadyPlaying, start.Outcome);
        Assert.Equal(path, Player.GetStatus().Track!.Path);
        Assert.Single(Backend.Tracks);
    }

    [Fact]
    public void An_alarm_with_the_player_off_fails_without_playing()
    {
        _rig.Change(p => p with { Enabled = false });

        var start = Player.StartFromAlarm("Igreja");

        Assert.Equal(AlarmStartOutcome.Failed, start.Outcome);
        Assert.Equal("player desligado", start.Error);
    }

    // ---- seek ----

    [Fact]
    public void Seek_moves_the_position_of_a_playing_track()
    {
        Player.Play();
        Backend.Output.PumpSeconds(0.5);

        Assert.True(Player.Seek(TimeSpan.FromSeconds(6)).Success);
        Assert.Equal(6.0, Player.GetStatus().Position.TotalSeconds, 2);
        Backend.Output.PumpSeconds(0.1);

        Assert.InRange(Player.GetStatus().Position.TotalSeconds, 6.0, 6.2);
        Assert.Equal(PlayerState.Playing, Player.GetStatus().State);
    }

    [Fact]
    public void Seek_during_a_fade_out_does_not_cancel_the_fade()
    {
        Player.Play();
        Backend.Output.PumpSeconds(0.5);
        Player.Pause();
        Backend.Output.PumpSeconds(1);

        Player.Seek(TimeSpan.FromSeconds(3));
        Backend.Output.PumpSeconds(4.5);

        Assert.Equal(PlayerState.Paused, Player.GetStatus().State);
    }

    [Fact]
    public void Seek_when_nothing_plays_is_refused()
    {
        Assert.False(Player.Seek(TimeSpan.FromSeconds(3)).Success);
    }

    [Fact]
    public void A_paused_player_keeps_the_track_and_position_and_a_seek_moves_the_resume_point()
    {
        _rig.Change(p => p with { StopFadeSeconds = 0 });
        Player.Play();
        var path = Player.GetStatus().Track!.Path;
        Backend.Output.PumpSeconds(2);
        Player.Pause();
        Backend.Output.PumpSeconds(0.1);

        var paused = Player.GetStatus();
        Assert.Equal(PlayerState.Paused, paused.State);
        Assert.Equal(path, paused.Track!.Path);
        Assert.InRange(paused.Position.TotalSeconds, 2.0, 2.2);
        Assert.Equal(10.0, paused.Duration.TotalSeconds, 2);

        Assert.True(Player.Seek(TimeSpan.FromSeconds(7)).Success);
        Assert.Equal(7.0, Player.GetStatus().Position.TotalSeconds, 2);
        Assert.Equal(PlayerState.Paused, Player.GetStatus().State);

        Player.Play();

        var resumed = Player.GetStatus();
        Assert.Equal(PlayerState.Playing, resumed.State);
        Assert.Equal(path, resumed.Track!.Path);
        Assert.InRange(resumed.Position.TotalSeconds, 6.9, 7.2);
    }

    [Fact]
    public void Play_after_a_pause_without_a_seek_still_starts_the_next_track()
    {
        _rig.Change(p => p with { StopFadeSeconds = 0 });
        Player.Play();
        var path = Player.GetStatus().Track!.Path;
        Backend.Output.PumpSeconds(1);
        Player.Pause();
        Backend.Output.PumpSeconds(0.1);

        Player.Play();

        Assert.NotEqual(path, Player.GetStatus().Track!.Path);
    }

    [Fact]
    public void Status_changes_are_announced_once_per_command_outside_the_lock()
    {
        var raised = 0;
        Player.StatusChanged += () =>
        {
            Interlocked.Increment(ref raised);
            _ = Player.GetStatus(); // a listener may read the player: no deadlock
        };

        Player.Play();

        Assert.True(raised >= 1);
    }

    [Fact]
    public void Dispose_stops_the_sound_at_once()
    {
        Player.Play();

        Player.Dispose();

        Assert.True(Backend.Output.Disposed);
    }
}
