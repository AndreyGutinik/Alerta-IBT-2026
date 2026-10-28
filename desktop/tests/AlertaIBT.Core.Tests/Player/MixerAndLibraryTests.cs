using AlertaIBT.Core.Player;
using AlertaIBT.Core.Settings;
using Microsoft.Extensions.Time.Testing;

namespace AlertaIBT.Core.Tests.Player;

public sealed class MixerAndLibraryTests
{
    // ---- the mixer ----

    [Fact]
    public void The_mixer_fills_silence_after_the_track_and_announces_the_end_once()
    {
        var mixer = new PlayerMixer(1000, 2, action => action());
        var ended = 0;
        mixer.TrackEnded += () => ended++;
        mixer.SetTrack(new FakeTrack("a", 1000, 2, totalFrames: 10, level: 1f));

        var first = new float[40];
        var second = new float[40];
        mixer.Read(first, 0, 40);
        mixer.Read(second, 0, 40);

        Assert.Equal(1f, first[19]); // the 20 samples of the track
        Assert.Equal(0f, first[20]); // then silence
        Assert.Equal(1, ended);
        Assert.All(second, sample => Assert.Equal(0f, sample));
    }

    [Fact]
    public void A_fade_is_sample_exact_and_its_callback_runs_after_the_last_frame()
    {
        var mixer = new PlayerMixer(1000, 1, action => action());
        var done = 0;
        mixer.SetTrack(new FakeTrack("a", 1000, 1, totalFrames: 1000, level: 1f));

        mixer.FadeTo(0f, 100, () => done++);
        var buffer = new float[99];
        mixer.Read(buffer, 0, 99);
        Assert.Equal(0, done);
        Assert.Equal(0.0001f, buffer[^1], 5); // quadratic curve: (1 frame left of 100) squared

        var last = new float[2];
        mixer.Read(last, 0, 2);

        Assert.Equal(1, done);
        Assert.Equal(0f, last[0]);
        Assert.Equal(0f, last[1]);
    }

    [Fact]
    public void Start_track_never_lets_the_new_track_out_at_full_gain()
    {
        var mixer = new PlayerMixer(1000, 1, action => action());
        mixer.StartTrack(new FakeTrack("a", 1000, 1, totalFrames: 1000, level: 1f), frames: 100);

        var first = new float[10];
        mixer.Read(first, 0, 10);

        Assert.All(first, sample => Assert.InRange(sample, 0f, 0.0101f));
    }

    [Fact]
    public void A_seek_ducks_jumps_while_silent_and_rises_again_without_a_step()
    {
        var mixer = new PlayerMixer(48000, 1, action => action());
        var track = new FakeTrack("a", 48000, 1, totalFrames: 48000 * 20, level: 1f);
        mixer.SetTrack(track);
        mixer.Read(new float[4800], 0, 4800);

        Assert.True(mixer.Seek(TimeSpan.FromSeconds(12)));
        Assert.Equal(12.0, mixer.Position.TotalSeconds, 3); // the screen sees the target at once
        var block = new float[4800];
        mixer.Read(block, 0, block.Length); // ducks to silence
        mixer.Read(block, 0, block.Length); // jumps and rises

        Assert.Equal(12.1, track.Position.TotalSeconds, 1);
        var steps = Enumerable.Range(1, block.Length - 1).Select(i => Math.Abs(block[i] - block[i - 1])).Max();
        Assert.True(steps < 0.01f, $"no click: the biggest step is {steps}");
        Assert.Equal(1f, block[^1], 3);
    }

    [Fact]
    public void A_seek_keeps_the_fade_and_the_volume_and_is_clamped_to_the_track()
    {
        var mixer = new PlayerMixer(1000, 1, action => action());
        var track = new FakeTrack("a", 1000, 1, totalFrames: 100_000, level: 1f);
        mixer.SetTrack(track);
        mixer.SetVolume(0.5f, false, immediate: true);
        mixer.Read(new float[100], 0, 100);

        mixer.Seek(TimeSpan.FromSeconds(500));
        mixer.Read(new float[100], 0, 100);
        var settled = new float[100];
        mixer.Read(settled, 0, 100);

        Assert.InRange(track.Position.TotalSeconds, 99.7, 100);
        Assert.Equal(0.5f, settled[^1], 3); // the volume is intact
    }

    [Fact]
    public void Seeking_with_no_track_does_nothing()
    {
        var mixer = new PlayerMixer(1000, 1, action => action());

        Assert.False(mixer.Seek(TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public void Disposing_the_replaced_track_is_the_mixers_job()
    {
        var mixer = new PlayerMixer(1000, 1, action => action());
        var first = new FakeTrack("a", 1000, 1, 10, 1f);
        mixer.SetTrack(first);

        mixer.SetTrack(new FakeTrack("b", 1000, 1, 10, 1f));

        Assert.True(first.Disposed);
    }

    // ---- the library ----

    private static (PlaylistLibrary Library, FakeMediaFileSystem Fs, List<FakeFolderWatcher> Watchers, FakeTimeProvider Clock, List<string> Changes) Make()
    {
        var fs = new FakeMediaFileSystem();
        var watchers = new List<FakeFolderWatcher>();
        var clock = new FakeTimeProvider();
        var library = new PlaylistLibrary(fs, _ =>
        {
            var watcher = new FakeFolderWatcher();
            watchers.Add(watcher);
            return watcher;
        }, clock, action => action());
        var changes = new List<string>();
        library.Changed += changes.Add;
        return (library, fs, watchers, clock, changes);
    }

    [Fact]
    public void A_burst_of_folder_events_gives_one_rescan_two_seconds_after_the_last_one()
    {
        var (library, fs, watchers, clock, changes) = Make();
        fs.Folders["/m"] = ["/m/a.flac"];
        library.Configure([new PlaylistEntry("L", "/m")]);
        var scans = fs.Scans;

        fs.Folders["/m"].Add("/m/b.flac");
        watchers[0].Raise();
        clock.Advance(TimeSpan.FromSeconds(1.5));
        watchers[0].Raise();
        clock.Advance(TimeSpan.FromSeconds(1.5));
        Assert.Equal(scans, fs.Scans);
        clock.Advance(TimeSpan.FromSeconds(0.5));

        Assert.Equal(scans + 1, fs.Scans);
        Assert.Equal(["/m/a.flac", "/m/b.flac"], library.Tracks("L"));
        Assert.Equal(["L", "L"], changes);
        library.Dispose();
    }

    [Fact]
    public void A_missing_folder_is_an_empty_playlist_and_a_manual_refresh_finds_it_later()
    {
        var (library, fs, _, _, _) = Make();

        library.Configure([new PlaylistEntry("L", "/later")]);
        Assert.Empty(library.Tracks("L"));

        fs.Folders["/later"] = ["/later/x.mp3"];
        library.Refresh("L");

        Assert.Equal(["/later/x.mp3"], library.Tracks("L"));
        library.Dispose();
    }

    [Fact]
    public void Configuring_again_keeps_unchanged_playlists_and_drops_removed_ones()
    {
        var (library, fs, watchers, _, _) = Make();
        fs.Folders["/a"] = ["/a/1.flac"];
        fs.Folders["/b"] = ["/b/1.flac"];
        library.Configure([new PlaylistEntry("A", "/a"), new PlaylistEntry("B", "/b")]);
        var scans = fs.Scans;

        library.Configure([new PlaylistEntry("A", "/a")]);

        Assert.Equal(scans, fs.Scans);
        Assert.Empty(library.Tracks("B"));
        Assert.True(watchers[1].Disposed);
        Assert.False(watchers[0].Disposed);
        library.Dispose();
        Assert.True(watchers[0].Disposed);
    }
}
