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
        Assert.Equal(0.01f, buffer[^1], 4);

        var last = new float[2];
        mixer.Read(last, 0, 2);

        Assert.Equal(1, done);
        Assert.Equal(0f, last[0]);
        Assert.Equal(0f, last[1]);
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
