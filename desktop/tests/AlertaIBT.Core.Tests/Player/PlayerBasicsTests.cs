using System.Text;
using AlertaIBT.Core.Audio;
using AlertaIBT.Core.Player;
using AlertaIBT.Core.Settings;

namespace AlertaIBT.Core.Tests.Player;

// The small pure parts of the player: output choice, settings, fades, the shuffle bag, file names and tags.
public sealed class PlayerBasicsTests
{
    private static readonly AudioDevice Speakers = new("id-speakers", "Alto-falantes (Realtek)");
    private static readonly AudioDevice Wing34 = new("id-wing-34", "WING-USB (Out 3-4)");
    private static readonly AudioDevice Wing12 = new("id-wing-12", "WING-USB (Out 1-2)");

    // ---- the output choice: id, else the name rule, else the Windows default ----

    [Fact]
    public void The_rule_needs_every_word_and_ignores_case()
    {
        var rule = DeviceMatchRule.Parse("WING + 3-4");

        Assert.True(rule.Matches("wing-usb (out 3-4)"));
        Assert.False(rule.Matches("WING-USB (Out 1-2)"));
        Assert.False(rule.Matches(null));
        Assert.False(DeviceMatchRule.None.Matches("anything"));
        Assert.Equal("WING + 3-4", rule.Text);
    }

    [Fact]
    public void The_exact_id_wins_even_when_another_device_matches_the_rule()
    {
        var choice = OutputResolver.Resolve([Speakers, Wing34], Speakers.Id, Speakers.Id, DeviceMatchRule.Parse("WING+3-4"));

        Assert.Equal(OutputChoiceKind.ById, choice.Kind);
        Assert.Same(Speakers, choice.Device);
    }

    [Fact]
    public void Without_the_id_the_first_active_device_matching_the_rule_is_used()
    {
        var choice = OutputResolver.Resolve([Speakers, Wing12, Wing34], Speakers.Id, "id-gone", DeviceMatchRule.Parse("WING+3-4"));

        Assert.Equal(OutputChoiceKind.ByRule, choice.Kind);
        Assert.Same(Wing34, choice.Device);
        Assert.False(choice.IsFallback);
    }

    [Fact]
    public void With_neither_the_windows_default_stands_in_and_with_no_device_there_is_none()
    {
        var fallback = OutputResolver.Resolve([Speakers, Wing12], Speakers.Id, null, DeviceMatchRule.Parse("WING+3-4"));
        var none = OutputResolver.Resolve([], null, null, DeviceMatchRule.Parse("WING+3-4"));

        Assert.Equal(OutputChoiceKind.Default, fallback.Kind);
        Assert.Same(Speakers, fallback.Device);
        Assert.True(fallback.IsFallback);
        Assert.Equal(OutputChoiceKind.None, none.Kind);
    }

    // ---- settings ----

    [Fact]
    public void An_old_settings_file_without_the_player_section_loads_with_the_owner_defaults()
    {
        var (settings, corrupt) = SettingsJson.Load("{ \"version\": 1, \"bar\": { \"height\": 60 } }");

        Assert.False(corrupt);
        var player = settings.Player;
        Assert.True(player.Enabled);
        Assert.True(player.AlarmsEnabled);
        Assert.True(player.MediaKeys);
        Assert.Equal(["WING", "3-4"], player.OutputMatch);
        Assert.Equal(5, player.StopFadeSeconds);
        Assert.Equal(5, player.AlarmFadeSeconds);
        Assert.True(player.Shuffle);
        Assert.Equal("all", player.Repeat);
        var playlist = Assert.Single(player.Playlists);
        Assert.Equal("Igreja", playlist.Name);
        var alarm = Assert.Single(player.Alarms);
        Assert.Equal(new TimeOnly(18, 0), alarm.StartTime);
        Assert.Equal(new TimeOnly(18, 25), alarm.LatestTime);
        Assert.True(alarm.HasDay(DayOfWeek.Sunday));
        Assert.Equal(AlarmEntry.SundayOnly, alarm.Days);
    }

    [Fact]
    public void The_spotify_entry_of_the_audio_settings_is_kept()
    {
        var (settings, _) = SettingsJson.Load("{}");

        Assert.Contains(settings.Audio.Apps, a => a.Name == "Spotify");
    }

    [Fact]
    public void The_player_settings_survive_a_save_and_load_and_are_held_in_range()
    {
        var odd = new AppSettings
        {
            Player = new PlayerSettings
            {
                Volume = 250,
                Repeat = "banana",
                StopFadeSeconds = 4,
                AlarmFadeSeconds = 1,
                OutputMatch = [" WING ", "", "1-2"],
                Playlists = [new PlaylistEntry("A", "/a"), new PlaylistEntry("a", "/dup"), new PlaylistEntry(" ", "/x")],
                CurrentPlaylist = "missing",
                Alarms =
                [
                    new AlarmEntry("", true, "25:99", "07:00", 0xFFFF, "A", "yesterday"),
                    new AlarmEntry("x", false, "9:05", "9:30", 6, "A", "2026-10-18"),
                ],
            },
        };

        var (loaded, corrupt) = SettingsJson.Load(SettingsJson.Save(odd));

        Assert.False(corrupt);
        var player = loaded.Player;
        Assert.Equal(100, player.Volume);
        Assert.Equal("all", player.Repeat);
        Assert.Equal(3, player.StopFadeSeconds);
        Assert.Equal(0, player.AlarmFadeSeconds);
        Assert.Equal(["WING", "1-2"], player.OutputMatch);
        Assert.Equal([new PlaylistEntry("A", "/a")], player.Playlists);
        Assert.Equal("A", player.CurrentPlaylist);
        Assert.Equal(2, player.Alarms.Count);
        Assert.Equal("alarme-1", player.Alarms[0].Id);
        Assert.Equal("18:00", player.Alarms[0].Start); // not a time: the default
        Assert.Equal(AlarmEntry.AllDays, player.Alarms[0].Days);
        Assert.Null(player.Alarms[0].LastFiredOn);
        Assert.Equal("09:05", player.Alarms[1].Start);
        Assert.Equal("2026-10-18", player.Alarms[1].LastFiredOn);
        Assert.Equal(player, SettingsJson.Load(SettingsJson.Save(loaded)).Settings.Player);
    }

    [Fact]
    public void A_removed_alarm_list_stays_empty_instead_of_coming_back()
    {
        var empty = new AppSettings { Player = new PlayerSettings { Alarms = [], Playlists = [] } };

        var player = SettingsJson.Load(SettingsJson.Save(empty)).Settings.Player;

        Assert.Empty(player.Alarms);
        Assert.Empty(player.Playlists);
    }

    [Fact]
    public void A_latest_time_before_the_start_is_moved_after_it()
    {
        var settings = SettingsJson.Normalize(new AppSettings
        {
            Player = new PlayerSettings { Alarms = [new AlarmEntry("a", true, "18:00", "17:00", 1, "Igreja")] },
        });

        Assert.Equal("18:25", settings.Player.Alarms[0].Latest);
    }

    [Fact]
    public void Weekday_bits_follow_dayofweek()
    {
        var alarm = AlarmEntry.CreateDefault("a", "Igreja").WithDay(DayOfWeek.Wednesday, true).WithDay(DayOfWeek.Sunday, false);

        Assert.True(alarm.HasDay(DayOfWeek.Wednesday));
        Assert.False(alarm.HasDay(DayOfWeek.Sunday));
        Assert.Equal(1 << 3, alarm.Days);
    }

    // ---- fades ----

    [Fact]
    public void A_fade_reaches_its_target_exactly_after_the_frames_and_holds()
    {
        var envelope = new FadeEnvelope();
        envelope.Set(0f);
        envelope.FadeTo(1f, 4);

        var gains = Enumerable.Range(0, 6).Select(_ => envelope.Next()).ToArray();

        Assert.Equal([0.25f, 0.5f, 0.75f, 1f, 1f, 1f], gains);
        Assert.False(envelope.IsFading);
    }

    [Theory]
    [InlineData(5, 48000, 240000)]
    [InlineData(3, 44100, 132300)]
    [InlineData(0, 48000, 480)]
    public void The_frames_of_a_fade_are_seconds_times_the_rate_and_a_dry_one_still_ramps_10_ms(double seconds, int rate, int frames)
    {
        Assert.Equal(frames, FadeEnvelope.FramesFor(seconds, rate));
    }

    // ---- the queue ----

    private static string[] Names(int count) => Enumerable.Range(1, count).Select(i => $"t{i}").ToArray();

    [Fact]
    public void Shuffle_plays_every_track_once_before_any_repeats_and_never_the_same_twice_in_a_row()
    {
        var queue = new PlaybackQueue(new Random(3)) { Shuffle = true, Repeat = PlayerRepeat.All };
        queue.SetTracks(Names(6));

        var order = Enumerable.Range(0, 18).Select(_ => queue.NextForPlay()!).ToList();

        for (var round = 0; round < 3; round++)
        {
            Assert.Equal(6, order.Skip(round * 6).Take(6).Distinct().Count());
        }

        for (var i = 1; i < order.Count; i++)
        {
            Assert.NotEqual(order[i - 1], order[i]);
        }
    }

    [Fact]
    public void In_order_the_end_wraps_for_a_user_and_for_repeat_all_but_not_for_repeat_off()
    {
        var queue = new PlaybackQueue { Shuffle = false, Repeat = PlayerRepeat.Off };
        queue.SetTracks(Names(2));

        Assert.Equal("t1", queue.NextForPlay());
        Assert.Equal("t2", queue.NextAuto());
        Assert.Null(queue.NextAuto()); // the list ended
        Assert.Equal("t1", queue.NextForPlay()); // a button press wraps anyway

        queue.Repeat = PlayerRepeat.All;
        Assert.Equal("t2", queue.NextAuto());
        Assert.Equal("t1", queue.NextAuto());
    }

    [Fact]
    public void A_shuffled_round_ends_with_repeat_off_and_repeat_one_keeps_the_track()
    {
        var queue = new PlaybackQueue(new Random(1)) { Shuffle = true, Repeat = PlayerRepeat.Off };
        queue.SetTracks(Names(3));

        var played = new List<string> { queue.NextForPlay()! };
        while (queue.NextAuto() is { } next)
        {
            played.Add(next);
        }

        Assert.Equal(3, played.Distinct().Count());

        queue.Repeat = PlayerRepeat.One;
        var current = queue.Current;
        Assert.Equal(current, queue.NextAuto());
        Assert.Equal(current, queue.NextAuto());
    }

    [Fact]
    public void Previous_walks_back_through_the_history_and_then_has_nothing()
    {
        var queue = new PlaybackQueue { Shuffle = false };
        queue.SetTracks(Names(4));
        queue.NextForPlay();
        queue.NextForPlay();
        queue.NextForPlay();

        Assert.Equal("t2", queue.Previous());
        Assert.Equal("t1", queue.Previous());
        Assert.Null(queue.Previous());
        Assert.Equal("t1", queue.Current);
    }

    [Fact]
    public void A_rescan_keeps_the_round_going_and_drops_what_was_removed()
    {
        var queue = new PlaybackQueue(new Random(5)) { Shuffle = true };
        queue.SetTracks(Names(3));
        var first = queue.NextForPlay();
        var second = queue.NextForPlay();

        queue.SetTracks(["t1", "t2", "t3", "t4"]);
        var third = queue.NextForPlay();
        var fourth = queue.NextForPlay();

        Assert.Equal(4, new[] { first, second, third, fourth }.Distinct().Count());
        queue.SetTracks(["t1"]);
        Assert.Equal(["t1"], queue.Tracks);
    }

    [Fact]
    public void An_empty_list_gives_nothing()
    {
        var queue = new PlaybackQueue();

        Assert.Null(queue.NextForPlay());
        Assert.Null(queue.NextAuto());
        Assert.Null(queue.Previous());
    }

    // ---- file names, extensions and tags ----

    [Fact]
    public void Natural_order_puts_2_before_10_and_ignores_case()
    {
        var sorted = new[] { "b10.flac", "B2.flac", "a1.flac", "b1.flac" }.Order(NaturalStringComparer.Instance).ToArray();

        Assert.Equal(["a1.flac", "b1.flac", "B2.flac", "b10.flac"], sorted);
    }

    [Fact]
    public void The_scan_keeps_only_the_supported_extensions_in_natural_order()
    {
        var fs = new FakeMediaFileSystem();
        fs.Folders["/m"] = ["/m/10.flac", "/m/2.MP3", "/m/cover.jpg", "/m/sub/1.m4a", "/m/notes.txt", "/m/3.wav", "/m/1.ogg"];

        var found = PlaylistScanner.Scan(fs, "/m");

        Assert.Equal(["/m/2.MP3", "/m/3.wav", "/m/10.flac", "/m/sub/1.m4a"], found);
    }

    [Theory]
    [InlineData("/m/Amazing Grace - Josh Snodgrass.flac", "Amazing Grace", "Josh Snodgrass")]
    [InlineData("/m/Act Justly - Instrumental - Hillside Recording.flac", "Act Justly - Instrumental", "Hillside Recording")]
    [InlineData("/m/01 - Be Still - Rivers.flac", "Be Still", "Rivers")]
    [InlineData("/m/06 00 Sunrise.flac", "06 00 Sunrise", null)]
    public void The_title_and_artist_come_from_the_file_name_when_there_are_no_tags(string path, string title, string? artist)
    {
        Assert.Equal((title, artist), FileTrackMetadataReader.FromFileName(path));
    }

    [Fact]
    public void Flac_vorbis_comments_give_title_and_artist()
    {
        using var stream = new MemoryStream(FlacBytes("TITLE=Gra\U000000E7a Maravilhosa", "ARTIST=Dan", "title=ignored"));

        var (title, artist) = FileTrackMetadataReader.ReadFlacTags(stream);

        Assert.Equal("Gra\U000000E7a Maravilhosa", title);
        Assert.Equal("Dan", artist);
    }

    [Fact]
    public void A_file_that_is_not_flac_has_no_tags()
    {
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes("RIFF....WAVE"));

        Assert.Equal((null, null), FileTrackMetadataReader.ReadFlacTags(stream));
    }

    // A minimal FLAC header: the marker, a STREAMINFO block (34 zero bytes) and a last VORBIS_COMMENT block.
    private static byte[] FlacBytes(params string[] comments)
    {
        using var body = new MemoryStream();
        using var writer = new BinaryWriter(body);
        var vendor = Encoding.UTF8.GetBytes("test");
        writer.Write(vendor.Length);
        writer.Write(vendor);
        writer.Write(comments.Length);
        foreach (var comment in comments)
        {
            var bytes = Encoding.UTF8.GetBytes(comment);
            writer.Write(bytes.Length);
            writer.Write(bytes);
        }

        writer.Flush();
        var block = body.ToArray();
        using var file = new MemoryStream();
        file.Write("fLaC"u8);
        file.Write([0x00, 0x00, 0x00, 34]);
        file.Write(new byte[34]);
        file.Write([0x84, (byte)(block.Length >> 16), (byte)(block.Length >> 8), (byte)block.Length]);
        file.Write(block);
        return file.ToArray();
    }

    // ---- channels ----

    [Fact]
    public void Mono_becomes_stereo_stereo_becomes_mono_and_extra_device_channels_stay_silent()
    {
        var stereo = new float[4];
        ChannelMapper.Map([0.5f, 0.5f], 1, stereo, 2, 1);
        ChannelMapper.Map([0.25f, 0.75f], 2, stereo.AsSpan(2), 1, 1);
        var wide = new float[4];
        ChannelMapper.Map([0.1f, 0.2f], 2, wide, 4, 1);

        Assert.Equal([0.5f, 0.5f, 0.5f], stereo.Take(3));
        Assert.Equal([0.1f, 0.2f, 0f, 0f], wide);
    }
}
