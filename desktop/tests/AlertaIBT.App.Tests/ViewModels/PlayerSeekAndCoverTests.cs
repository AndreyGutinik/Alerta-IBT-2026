using System.Windows.Media;
using System.Windows.Media.Imaging;
using AlertaIBT.App.Services;
using AlertaIBT.App.ViewModels;
using AlertaIBT.Core.Player;
using AlertaIBT.Core.Settings;
using Microsoft.Extensions.Time.Testing;

namespace AlertaIBT.App.Tests.ViewModels;

// The timeline (drag, click, keys, paused) and the album art of the player's view-model, on the real engine with fakes at the edges.
public sealed class PlayerSeekAndCoverTests : IDisposable
{
    private readonly SettingsRig _rig = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 25, 10, 0, 0, TimeSpan.Zero));
    private readonly FakePlayerBackend _backend = new();
    private readonly FakeCovers _covers = new();
    private readonly PlaylistLibrary _library;
    private readonly MusicPlayer _player;
    private readonly AlarmScheduler _alarms;
    private readonly PlayerViewModel _vm;

    public PlayerSeekAndCoverTests()
    {
        _library = new PlaylistLibrary(new FakeFiles(), _ => null, _clock, action => action());
        _player = new MusicPlayer(_rig.Settings, _library, _backend, new FakeAudio(), new FakeMetadata(), _clock, null, action => action(), new Random(1));
        _alarms = new AlarmScheduler(_rig.Settings, _player, _clock);
        _player.Start();
        _vm = new PlayerViewModel(_rig.Settings, _player, _alarms, new FakeAudio(), action => action(), action => action(), _clock, covers: _covers);
    }

    public void Dispose()
    {
        _vm.Dispose();
        _alarms.Dispose();
        _player.Dispose();
        _library.Dispose();
        _rig.Dispose();
    }

    private IPcmSource Sound => _backend.Outputs[^1].Source;

    // The sound card pulls `seconds` of sound and the half-second timer ticks as often as it would.
    private void Listen(double seconds)
    {
        var block = new float[9600]; // 100 ms of 48 kHz stereo
        var steps = (int)(seconds * 10);
        for (var i = 0; i < steps; i++)
        {
            Sound.Read(block, 0, block.Length);
            _clock.Advance(TimeSpan.FromMilliseconds(100));
        }
    }

    private void StartPlaying(double listenSeconds = 2)
    {
        _vm.PlayCommand.Execute(null);
        Listen(listenSeconds);
    }

    private void Pause()
    {
        _rig.Settings.Update(s => s with { Player = s.Player with { StopFadeSeconds = 0 } });
        _vm.PauseCommand.Execute(null);
        Listen(0.2);
    }

    // ---- the timeline ----

    [Fact]
    public void The_labels_show_elapsed_and_total_and_the_slider_range_is_the_length()
    {
        StartPlaying(2);

        Assert.Equal("0:02", _vm.ElapsedText);
        Assert.Equal("3:20", _vm.DurationText);
        Assert.Equal(200, _vm.SeekMaximum);
        Assert.InRange(_vm.SeekValue, 1.9, 2.3);
        Assert.True(_vm.CanSeek);
    }

    [Fact]
    public void Dragging_moves_the_thumb_and_the_label_but_playback_does_not_jump()
    {
        StartPlaying(2);

        _vm.BeginSeek();
        _vm.UpdateSeek(100);
        Listen(2); // the engine keeps playing and the timer keeps ticking while the thumb is held

        Assert.True(_vm.IsSeeking);
        Assert.Equal("1:40", _vm.ElapsedText);
        Assert.Equal(100, _vm.SeekValue, 3);
        Assert.StartsWith("1:40 / ", _vm.PositionText, StringComparison.Ordinal);
        Assert.InRange(_player.GetStatus().Position.TotalSeconds, 3.5, 5); // it never jumped to 100
    }

    [Fact]
    public void Letting_go_seeks_and_the_position_then_follows_the_player_again()
    {
        StartPlaying(2);
        _vm.BeginSeek();
        _vm.UpdateSeek(100);

        _vm.CommitSeek();
        Listen(0.3);

        Assert.False(_vm.IsSeeking);
        Assert.InRange(_player.GetStatus().Position.TotalSeconds, 100, 101);
        Listen(2);
        Assert.InRange(_vm.SeekValue, 101.5, 103.2); // the timer moves it again
        Assert.StartsWith("1:4", _vm.ElapsedText, StringComparison.Ordinal);
    }

    [Fact]
    public void A_click_seeks_at_once_and_a_write_that_is_not_a_drag_is_ignored()
    {
        StartPlaying(2);

        _vm.SeekValue = 150; // a slider that coerced its value: not a drag, so nothing happens
        Assert.InRange(_vm.SeekValue, 1.9, 2.5);

        _vm.SeekToCommand.Execute(50.0);
        Listen(0.3);

        Assert.InRange(_player.GetStatus().Position.TotalSeconds, 50, 51);
    }

    [Fact]
    public void The_keys_move_five_seconds_either_way_and_stop_at_the_ends()
    {
        StartPlaying(10);

        _vm.SeekByCommand.Execute(PlayerViewModel.KeySeekSeconds);
        Listen(0.2);
        Assert.InRange(_player.GetStatus().Position.TotalSeconds, 15, 16);

        _vm.SeekByCommand.Execute(-PlayerViewModel.KeySeekSeconds);
        Listen(0.2);
        Assert.InRange(_player.GetStatus().Position.TotalSeconds, 10, 11.5);

        _vm.SeekByCommand.Execute(-500.0);
        Listen(0.2);
        Assert.InRange(_player.GetStatus().Position.TotalSeconds, 0, 1);
    }

    [Fact]
    public void A_burst_of_seeks_ends_at_the_last_target()
    {
        StartPlaying(2);

        _vm.SeekTo(10);
        _vm.SeekTo(20);
        _vm.SeekTo(30);
        Listen(0.3);

        Assert.InRange(_player.GetStatus().Position.TotalSeconds, 30, 31);
    }

    [Fact]
    public void The_timeline_works_while_paused_and_play_continues_from_there()
    {
        StartPlaying(3);
        var path = _player.GetStatus().Track!.Path;
        Pause();
        Assert.Equal(PlayerState.Paused, _vm.State);
        Assert.True(_vm.CanSeek);
        Assert.Equal("3:20", _vm.DurationText);
        Assert.InRange(_vm.SeekValue, 2.8, 3.6);

        _vm.BeginSeek();
        _vm.UpdateSeek(90);
        Assert.Equal("1:30", _vm.ElapsedText);
        _vm.CommitSeek();

        Assert.Equal("1:30", _vm.ElapsedText);
        Assert.Equal(PlayerState.Paused, _vm.State);

        _vm.PlayCommand.Execute(null);
        Listen(0.3);
        Assert.Equal(PlayerState.Playing, _vm.State);
        Assert.Equal(path, _player.GetStatus().Track!.Path);
        Assert.InRange(_player.GetStatus().Position.TotalSeconds, 90, 91.5);
    }

    [Fact]
    public void Nothing_to_seek_in_when_stopped_or_when_the_player_is_off()
    {
        Assert.False(_vm.CanSeek);
        _vm.BeginSeek();
        Assert.False(_vm.IsSeeking);
        _vm.SeekTo(30);
        Assert.Equal(0, _vm.SeekValue);

        StartPlaying(1);
        Assert.True(_vm.CanSeek);
        _vm.PlayerOn = false;
        Assert.False(_vm.CanSeek);
    }

    [Fact]
    public void Cancelling_a_drag_puts_the_thumb_back_on_the_player()
    {
        StartPlaying(2);
        _vm.BeginSeek();
        _vm.UpdateSeek(100);

        _vm.CancelSeek();

        Assert.False(_vm.IsSeeking);
        Assert.InRange(_vm.SeekValue, 1.9, 2.5);
    }

    // ---- the hero text ----

    [Fact]
    public void The_hero_shows_the_title_the_artist_and_the_place_in_the_playlist()
    {
        Assert.Equal("Nada tocando", _vm.HeroTitle);
        Assert.Equal("2 m\U000000FAsicas", _vm.TrackIndexText);

        StartPlaying(1);

        Assert.Equal("Song", _vm.HeroTitle);
        Assert.Equal("Artist", _vm.HeroSubtitle);
        Assert.Matches(@"^[12] de 2$", _vm.TrackIndexText);
        Assert.Contains("Song", _vm.HeroTitleTip, StringComparison.Ordinal);
    }

    // ---- the album art ----

    [Fact]
    public void The_cover_arrives_for_the_track_and_is_replaced_when_the_track_changes()
    {
        StartPlaying(1);
        var firstImage = _vm.CoverArt;
        Assert.True(_vm.HasCover);
        var first = _player.GetStatus().Track!.Path;

        _vm.NextCommand.Execute(null);
        Listen(0.5);

        Assert.NotEqual(first, _player.GetStatus().Track!.Path);
        Assert.True(_vm.HasCover);
        Assert.NotSame(firstImage, _vm.CoverArt);
        Assert.Same(_covers.Images[_player.GetStatus().Track!.Path], _vm.CoverArt);
    }

    [Fact]
    public void A_track_without_art_shows_none_and_never_the_art_of_the_previous_track()
    {
        StartPlaying(1);
        Assert.True(_vm.HasCover);
        _covers.HasArt = false;

        _vm.NextCommand.Execute(null);
        Listen(0.5);

        Assert.False(_vm.HasCover);
        Assert.Null(_vm.CoverArt);
    }

    [Fact]
    public void A_late_cover_of_a_track_that_is_gone_is_dropped()
    {
        _covers.Hold = true;
        StartPlaying(1);
        var path = _player.GetStatus().Track!.Path;
        Assert.False(_vm.HasCover); // still loading

        _rig.Settings.Update(s => s with { Player = s.Player with { StopFadeSeconds = 0 } });
        _vm.StopCommand.Execute(null);
        Listen(0.5);
        Assert.Equal(PlayerState.Stopped, _vm.State);
        _covers.Release(path); // it arrives after the track went away

        Assert.False(_vm.HasCover);
    }

    private sealed class FakeCovers : ICoverArtLoader
    {
        private readonly Dictionary<string, TaskCompletionSource<ImageSource?>> _pending = [];

        public Dictionary<string, ImageSource> Images { get; } = [];

        public bool HasArt { get; set; } = true;

        public bool Hold { get; set; }

        public Task<ImageSource?> LoadAsync(string trackPath)
        {
            ImageSource? image = null;
            if (HasArt)
            {
                var bitmap = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgr32, null, new byte[16], 8);
                bitmap.Freeze();
                Images[trackPath] = bitmap;
                image = bitmap;
            }

            if (!Hold)
            {
                return Task.FromResult(image);
            }

            var source = new TaskCompletionSource<ImageSource?>();
            _pending[trackPath] = source;
            return source.Task;
        }

        public void Release(string trackPath) => _pending[trackPath].SetResult(Images.GetValueOrDefault(trackPath));
    }
}
