using AlertaIBT.Core.Settings;
using AlertaIBT.Core.Player;

namespace AlertaIBT.Core.Tests.Player;

// The engine's gapless change and crossfade against fakes: the queue/shuffle/repeat rules decide the next track, the player decodes it ahead
// and the mixer joins it. Tracks are told apart by their level (track 01 = 0.2, 02 = 0.4, 03 = 0.6, ...).
public sealed class CrossfadePlayerTests : IDisposable
{
    private const int Rate = 48000;

    private readonly PlayerRig _rig;

    public CrossfadePlayerTests()
        : this(3)
    {
    }

    private CrossfadePlayerTests(int tracks)
    {
        _rig = new PlayerRig(tracks);
        Backend.TrackFrames = Rate * 4;
        Backend.LevelFor = path => 0.2f * int.Parse(System.IO.Path.GetFileNameWithoutExtension(path)[^2..], System.Globalization.CultureInfo.InvariantCulture);
        _rig.Change(p => p with { Shuffle = false, Repeat = "all", FadeInSeconds = 0, FadeOutSeconds = 1, CrossfadeSeconds = 0, CrossfadeCurve = CrossfadeCurve.ConstantPower });
    }

    public void Dispose() => _rig.Dispose();

    private MusicPlayer Player => _rig.Player;

    private FakePlayerBackend Backend => _rig.Backend;

    // The left channel of `seconds` of what the sound card pulls.
    private float[] Pull(double seconds)
    {
        var output = Backend.Output;
        var frames = (int)Math.Round(seconds * Rate);
        var result = new float[frames];
        for (var done = 0; done < frames; done += 480)
        {
            var block = output.Pump(Math.Min(480, frames - done));
            for (var i = 0; i < block.Length / 2; i++)
            {
                result[done + i] = block[i * 2];
            }
        }

        return result;
    }

    private string TrackName => Path.GetFileNameWithoutExtension(Player.GetStatus().Track!.Path);

    // ---- gapless ----

    [Fact]
    public void With_no_crossfade_the_next_track_is_decoded_ahead_and_follows_with_no_silence()
    {
        Player.Play();

        Assert.Equal(2, Backend.Tracks.Count); // the playing one and the next, already open
        var sound = Pull(3.9);
        Assert.Equal("track 01", TrackName);
        sound = [.. sound.Skip(Rate / 20), .. Pull(0.2)]; // past the 10 ms start ramp, across the boundary at 4.0 s

        Assert.Equal("track 02", TrackName);
        Assert.All(sound, sample => Assert.InRange(sample, 0.19f, 0.41f)); // never a gap, never a click to zero
        Assert.Contains(sound, sample => sample > 0.39f);
        Assert.Equal(PlayerState.Playing, Player.GetStatus().State);
    }

    [Fact]
    public void Consecutive_tracks_keep_following_each_other_gapless_and_each_is_disposed_after_it_plays()
    {
        Player.Play();
        var first = Backend.Tracks[0];

        var sound = Pull(4 * 3 + 0.5); // 01, 02, 03, then 01 again (repeat all)

        Assert.True(first.Disposed);
        Assert.Equal("track 01", TrackName);
        Assert.DoesNotContain(sound.Skip(Rate / 20), sample => sample < 0.19f);
        Assert.Equal(PlayerState.Playing, Player.GetStatus().State);
        // Only the one playing and the one decoded ahead are alive.
        Assert.Equal(2, Backend.Tracks.Count(t => !t.Disposed));
    }

    // ---- crossfade ----

    [Fact]
    public void The_crossfade_starts_the_length_before_the_end_and_the_screen_follows_at_its_first_sample()
    {
        _rig.Change(p => p with { CrossfadeSeconds = 1 });
        Player.Play();

        Pull(2.9);
        Assert.Equal("track 01", TrackName);

        var overlap = Pull(0.3); // 2.9 to 3.2 s: the overlap starts at 3.0 s

        Assert.Equal("track 02", TrackName);
        Assert.Equal(2, Player.GetStatus().TrackIndex);
        Assert.InRange(Player.GetStatus().Position.TotalSeconds, 0.1, 0.25); // the timeline restarted with the new track
        Assert.Equal(TimeSpan.FromSeconds(4), Player.GetStatus().Duration);
        Assert.InRange(overlap[0], 0.199f, 0.201f); // still track 01 alone at the very start
        var after = Pull(1.0); // the overlap ended at 4.0 s
        Assert.InRange(after[^1], 0.399f, 0.401f); // track 02 alone at its own level
        Assert.True(Backend.Tracks[0].Disposed);
    }

    [Fact]
    public void The_crossfade_is_anchored_on_the_last_sound_of_the_track_and_the_next_one_skips_its_leading_silence()
    {
        // Track 01 has its last sound at 3.0 s of its 4 s; track 02 starts to sound at 0.5 s.
        Backend.ProfileFor = path => path.Contains("track 01")
            ? new TrackProfile(TimeSpan.Zero, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(4))
            : new TrackProfile(TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(4));
        _rig.Change(p => p with { CrossfadeSeconds = 1 });
        Player.Play();

        Pull(1.9);
        Assert.Equal("track 01", TrackName);
        Pull(0.2); // 2.1 s: the overlap began at 2.0 s (3.0 minus 1), not at 3.0 s

        Assert.Equal("track 02", TrackName);
        Assert.InRange(Player.GetStatus().Position.TotalSeconds, 0.55, 0.7); // it started at its first sound (0.5 s)
        Assert.Contains(Backend.Tracks, t => t.Path.Contains("track 02") && t.Position >= TimeSpan.FromSeconds(0.5));
    }

    [Fact]
    public void Without_a_crossfade_nothing_is_measured_and_the_next_track_starts_at_zero()
    {
        Backend.ProfileFor = _ => new TrackProfile(TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(4));
        Player.Play();

        Pull(0.1);

        Assert.Empty(Backend.Analyzed);
        Assert.Equal("track 01", TrackName);
    }

    [Fact]
    public void In_the_middle_of_the_overlap_both_tracks_are_heard()
    {
        _rig.Change(p => p with { CrossfadeSeconds = 2 });
        Player.Play();

        Pull(3.0); // the overlap runs 2.0 to 4.0 s: now at its middle

        var middle = Pull(0.01)[0];
        var expected = (0.2 * Math.Cos(Math.PI / 4)) + (0.4 * Math.Sin(Math.PI / 4)); // 0.424: cos and sin of 45 degrees
        Assert.Equal(expected, middle, 2);
    }

    [Fact]
    public void A_crossfade_length_changed_while_playing_applies_to_the_overlap_that_has_not_started()
    {
        Player.Play();
        Pull(1.0);

        _rig.Change(p => p with { CrossfadeSeconds = 2 });
        Pull(1.2); // 2.2 s: the overlap, 2 s long, began at 2.0 s

        Assert.Equal("track 02", TrackName);
    }

    [Fact]
    public void A_track_shorter_than_twice_the_crossfade_gets_half_of_its_length()
    {
        Backend.TrackFrames = (int)(Rate * 1.2);
        _rig.Change(p => p with { CrossfadeSeconds = 4 });
        Player.Play();

        Pull(0.5);
        Assert.Equal("track 01", TrackName);
        Pull(0.2); // 0.7 s: the overlap began at 0.6 s

        Assert.Equal("track 02", TrackName);
    }

    // ---- the queue, shuffle and repeat decide the next ----

    [Fact]
    public void The_end_of_a_playlist_that_does_not_repeat_has_no_crossfade_into_nothing()
    {
        using var rig = new CrossfadePlayerTests(tracks: 2);
        rig._rig.Change(p => p with { Repeat = "off", CrossfadeSeconds = 1 });
        rig.Player.Play();
        rig.Pull(3.5); // track 01 crossfaded into 02 at 3 s
        Assert.Equal("track 02", rig.TrackName);

        var end = rig.Pull(0.4 + 3.0 - 0.7); // up to 6.2 s of the 4 s + 4 s - 1 s run (7 s total)
        var tail = rig.Pull(0.5); // up to 6.7 s, inside what would have been the second overlap

        Assert.Equal(0.4f, tail[^1], 3); // full level of track 02: not faded out towards nothing
        Assert.Equal(0.4f, end[^1], 3);
        rig.Pull(0.6);
        Assert.Equal(PlayerState.Stopped, rig.Player.GetStatus().State);
    }

    [Fact]
    public void Repeat_one_crosses_over_into_the_same_track_again()
    {
        _rig.Change(p => p with { Repeat = "one", CrossfadeSeconds = 1 });
        Player.Play();
        var path = Player.GetStatus().Track!.Path;

        Pull(3.3);

        Assert.Equal(path, Player.GetStatus().Track!.Path);
        Assert.True(Backend.Tracks.Count(t => t.Path == path) >= 2);
    }

    [Fact]
    public void With_shuffle_the_track_decoded_ahead_is_the_one_that_plays_and_a_round_never_repeats()
    {
        using var rig = new CrossfadePlayerTests(tracks: 5);
        rig._rig.Change(p => p with { Shuffle = true, CrossfadeSeconds = 0.5 });
        rig.Player.Play();
        var heard = new List<string> { rig.TrackName };

        for (var i = 0; i < 4; i++)
        {
            var decodedAhead = rig.Backend.Tracks[^1].Path;
            rig.Pull(4.0);
            Assert.Equal(decodedAhead, rig.Player.GetStatus().Track!.Path);
            heard.Add(rig.TrackName);
        }

        Assert.Equal(5, heard.Distinct().Count()); // every track once before any repeats
    }

    [Fact]
    public void Switching_shuffle_off_while_playing_replaces_the_track_decoded_ahead()
    {
        using var rig = new CrossfadePlayerTests(tracks: 5);
        rig._rig.Change(p => p with { Shuffle = true });
        rig.Player.Play();
        var ahead = rig.Backend.Tracks[1];

        rig._rig.Change(p => p with { Shuffle = false });
        var current = rig.TrackName;
        rig.Pull(4.1);

        var expected = $"track {int.Parse(current[^2..], System.Globalization.CultureInfo.InvariantCulture) + 1:00}";
        Assert.Equal(expected, rig.TrackName);
        Assert.True(ahead.Disposed || ahead.Path.EndsWith(expected + ".flac", StringComparison.Ordinal));
    }

    [Fact]
    public void A_file_that_cannot_be_decoded_ahead_is_skipped_when_the_track_ends()
    {
        Backend.BadFiles.Add("/music/igreja/track 02.flac");
        Player.Play();

        Pull(4.2);

        Assert.Equal(PlayerState.Playing, Player.GetStatus().State);
        Assert.Equal("track 03", TrackName);
    }

    // ---- the buttons keep their behaviour ----

    [Fact]
    public void Next_and_previous_do_not_crossfade_they_fade_out_briefly_and_the_decoded_ahead_track_is_dropped()
    {
        _rig.Change(p => p with { CrossfadeSeconds = 3 });
        Player.Play();
        Pull(1.0);
        var ahead = Backend.Tracks[1];

        Player.Next();
        var sound = Pull(0.4); // 150 ms down, then the new track rises from silence

        Assert.True(ahead.Disposed); // the preloaded one was let go; the skip opens its own
        Assert.Equal("track 02", TrackName);
        Assert.True(sound.Min() < 0.01f, "the cut passes through silence (a fade, not a mix)");
    }

    // ---- pause, stop and seek during the overlap ----

    [Fact]
    public void A_pause_during_the_crossfade_fades_both_tracks_out_together_and_lets_everything_go()
    {
        _rig.Change(p => p with { CrossfadeSeconds = 2 });
        Player.Play();
        Pull(3.0); // in the middle of the overlap (2 to 4 s)

        Player.Pause();
        var fade = Pull(1.1);

        Assert.Equal(PlayerState.Paused, Player.GetStatus().State);
        Assert.True(Backend.Outputs[0].Disposed);
        Assert.True(fade[^1] < 0.001f);
        Assert.All(fade.Skip(1), sample => Assert.InRange(sample, 0f, 0.44f));
        Assert.All(Backend.Tracks, track => Assert.True(track.Disposed, "no decoder is left open"));
    }

    [Fact]
    public void Play_after_a_pause_in_the_crossfade_goes_on_with_another_track_as_a_pause_always_does()
    {
        _rig.Change(p => p with { CrossfadeSeconds = 2 });
        Player.Play();
        Pull(3.0);
        Player.Pause();
        Pull(1.1);
        var pausedOn = TrackName;

        Assert.True(Player.Play().Success);

        Assert.Equal(PlayerState.Playing, Player.GetStatus().State);
        Assert.NotEqual(pausedOn, TrackName);
    }

    [Fact]
    public void A_stop_during_the_crossfade_stops_and_frees_both_tracks()
    {
        _rig.Change(p => p with { CrossfadeSeconds = 2 });
        Player.Play();
        Pull(3.0);

        Player.Stop();
        Pull(1.1);

        Assert.Equal(PlayerState.Stopped, Player.GetStatus().State);
        Assert.All(Backend.Tracks, track => Assert.True(track.Disposed));
    }

    [Fact]
    public void A_seek_during_the_crossfade_cancels_it_the_sought_track_goes_on_alone()
    {
        _rig.Change(p => p with { CrossfadeSeconds = 2 });
        Player.Play();
        Pull(3.0);

        Assert.True(Player.Seek(TimeSpan.FromSeconds(0.5)).Success);
        var sound = Pull(0.5);

        Assert.True(Backend.Tracks[0].Disposed);
        Assert.Equal(0.4f, sound[^1], 3); // track 02 at its own level, nothing of 01
        Assert.Equal("track 02", TrackName);
        Assert.Equal(PlayerState.Playing, Player.GetStatus().State);
    }

    [Fact]
    public void Disposing_the_player_in_the_middle_of_a_crossfade_lets_every_decoder_go()
    {
        _rig.Change(p => p with { CrossfadeSeconds = 2 });
        Player.Play();
        Pull(3.0);

        Player.Dispose();

        Assert.All(Backend.Tracks, track => Assert.True(track.Disposed));
    }
}
