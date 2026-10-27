using System.Windows;
using AlertaIBT.App.Tests.Platform;
using AlertaIBT.App.Tray;
using AlertaIBT.App.ViewModels;
using AlertaIBT.Core.Audio;
using AlertaIBT.Core.Player;
using AlertaIBT.Core.Services;
using AlertaIBT.Core.Settings;
using Microsoft.Extensions.Time.Testing;

namespace AlertaIBT.App.Tests.ViewModels;

// The player's view-model and the quick panel row, on the real engine with fakes: no device is opened, no file is decoded, no sound.
public sealed class PlayerViewModelTests : IDisposable
{
    private sealed class Track(string path) : IPcmTrack
    {
        private int _read;

        public string Path { get; } = path;

        public TimeSpan Duration => TimeSpan.FromSeconds(200);

        public TimeSpan Position => TimeSpan.FromSeconds(_read / 96000.0);

        public int Read(float[] buffer, int offset, int count)
        {
            Array.Fill(buffer, 0.5f, offset, count);
            _read += count;
            return count;
        }

        public void Dispose()
        {
        }
    }

    private sealed class Output(IPcmSource source) : IPlayerOutput
    {
        public IPcmSource Source { get; } = source;

        public string? DeviceId => "dev";

        public string? DeviceName => "dev";

        public event Action<Exception?>? Failed
        {
            add { }
            remove { }
        }

        public void Dispose()
        {
        }
    }

    private sealed class Backend : IPlayerBackend
    {
        public List<Output> Outputs { get; } = [];

        public IPlayerOutput OpenOutput(string? deviceId, Func<int, int, IPcmSource> createSource)
        {
            var output = new Output(createSource(48000, 2));
            Outputs.Add(output);
            return output;
        }

        public IPcmTrack OpenTrack(string path, int sampleRate, int channels) => new Track(path);
    }

    private sealed class Audio : IAudioSystem
    {
        public List<AudioDevice> Devices { get; } = [new AudioDevice("id-1", "Alto-falantes (Realtek)")];

        public event Action? DevicesChanged
        {
            add { }
            remove { }
        }

        public event Action? DefaultDeviceChanged
        {
            add { }
            remove { }
        }

        public event Action? SessionCreated
        {
            add { }
            remove { }
        }

        public IReadOnlyList<AudioDevice> ActiveRenderDevices() => Devices;

        public DefaultDevices GetDefaultDevices() => new("id-1", "id-1", "id-1");

        public bool SetDefaultRenderDevice(string deviceId) => true;

        public bool SetMasterVolume(string deviceId, int percent, bool muted) => true;

        public IReadOnlyList<AudioSessionInfo> GetSessions(string deviceId) => [];

        public bool SetSessionVolume(string deviceId, string sessionKey, int percent, bool muted) => true;

        public void WatchSessions(string? deviceId)
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class Files : IMediaFileSystem
    {
        public bool DirectoryExists(string folder) => true;

        public IEnumerable<string> EnumerateFiles(string folder) => ["/m/Amazing Grace - Josh.flac", "/m/Be Still - Rivers.flac", "/m/notes.txt"];
    }

    private sealed class Metadata : ITrackMetadataReader
    {
        public TrackInfo Read(string path) => new(path, System.IO.Path.GetFileName(path), "Song", "Artist");
    }

    private readonly SettingsRig _rig = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 25, 10, 0, 0, TimeSpan.Zero));
    private readonly Audio _audio = new();
    private readonly Backend _backend = new();
    private readonly PlaylistLibrary _library;
    private readonly MusicPlayer _player;
    private readonly AlarmScheduler _alarms;
    private readonly PlayerViewModel _vm;

    public PlayerViewModelTests()
    {
        _library = new PlaylistLibrary(new Files(), _ => null, _clock, action => action());
        _player = new MusicPlayer(_rig.Settings, _library, _backend, _audio, new Metadata(), _clock, null, action => action(), new Random(1));
        _alarms = new AlarmScheduler(_rig.Settings, _player, _clock);
        _player.Start();
        _vm = new PlayerViewModel(_rig.Settings, _player, _alarms, _audio, action => action(), action => action(), _clock);
    }

    public void Dispose()
    {
        _vm.Dispose();
        _alarms.Dispose();
        _player.Dispose();
        _library.Dispose();
        _rig.Dispose();
    }

    private void Change(Func<PlayerSettings, PlayerSettings> edit) => _rig.Settings.Update(s => s with { Player = edit(s.Player) });

    // ---- state ----

    [Fact]
    public void At_start_it_is_stopped_and_says_what_the_quick_row_and_the_tab_need()
    {
        Assert.True(_vm.IsEnabled);
        Assert.False(_vm.IsPlaying);
        Assert.Equal("Parado", _vm.StateText);
        Assert.Equal("Nada tocando", _vm.QuickTitle);
        Assert.Equal("LucidePlay", _vm.PlayPauseIconKey);
        Assert.Equal("Igreja", _vm.PlaylistName);
        Assert.Equal(2, _vm.TrackCount); // the .txt is not music
        Assert.Equal("2 m\U000000FAsicas", _vm.TrackCountText);
        Assert.Equal("Pr\U000000F3ximo despertador: hoje 18:00", _vm.NextAlarmText); // the test clock is a Sunday morning
        Assert.StartsWith("Sa\U000000EDda do player: WING + 3-4", _vm.OutputText, StringComparison.Ordinal);
        Assert.False(_vm.HasError);
        Assert.Single(_vm.OutputDevices);
    }

    [Fact]
    public void Play_pause_starts_the_music_and_the_screen_follows()
    {
        var changed = new List<string>();
        _vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName!);

        _vm.PlayPauseCommand.Execute(null);

        Assert.True(_vm.IsPlaying);
        Assert.Equal("Tocando", _vm.StateText);
        Assert.True(_vm.HasTrack);
        Assert.Equal("Song", _vm.TrackTitle);
        Assert.Equal("Artist", _vm.TrackArtist);
        Assert.Equal("Song \U00002014 Artist", _vm.QuickTitle);
        Assert.Equal("LucidePause", _vm.PlayPauseIconKey);
        Assert.Contains(nameof(PlayerViewModel.IsPlaying), changed);
        Assert.Contains(nameof(PlayerViewModel.QuickTitle), changed);
    }

    [Fact]
    public void The_position_moves_with_the_timer_while_it_plays()
    {
        _vm.PlayCommand.Execute(null);
        _backend.Outputs[^1].Source.Read(new float[96000], 0, 96000); // one second of sound goes out

        _clock.Advance(PlayerViewModel.PositionInterval);

        Assert.Equal(TimeSpan.FromSeconds(200), _vm.Duration);
        Assert.Equal("0:01 / 3:20", _vm.PositionText);
        Assert.InRange(_vm.Progress, 0.004, 0.006);
    }

    // ---- the settings ----

    [Fact]
    public void Writing_a_property_saves_it_and_a_saved_change_updates_the_property()
    {
        var changed = new List<string>();
        _vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName!);

        _vm.Volume = 55;
        _vm.StartMuted = true;
        _vm.StopFadeSeconds = 3;

        Assert.Equal(55, _rig.Settings.Current.Player.Volume);
        Assert.True(_rig.Settings.Current.Player.StartMuted);
        Assert.Equal(3, _rig.Settings.Current.Player.StopFadeSeconds);

        Change(p => p with { Volume = 10, AlarmFadeSeconds = 0 });

        Assert.Equal(10, _vm.Volume);
        Assert.Equal(0, _vm.AlarmFadeSeconds);
        Assert.Contains(nameof(PlayerViewModel.Volume), changed);
    }

    [Fact]
    public void The_toggles_and_the_repeat_cycle_write_the_settings()
    {
        _vm.ToggleShuffleCommand.Execute(null);
        Assert.False(_rig.Settings.Current.Player.Shuffle);

        _vm.CycleRepeatCommand.Execute(null); // all -> one
        Assert.Equal("one", _rig.Settings.Current.Player.Repeat);
        _vm.CycleRepeatCommand.Execute(null); // one -> off
        Assert.Equal("off", _rig.Settings.Current.Player.Repeat);
        Assert.Equal("Repetir: desligado", _vm.RepeatText);

        _vm.MediaKeys = false;
        _vm.AlarmsEnabled = false;
        Assert.False(_rig.Settings.Current.Player.MediaKeys);
        Assert.False(_rig.Settings.Current.Player.AlarmsEnabled);
        Assert.Equal("Despertadores desligados", _alarms.NextAlarmText);
    }

    [Fact]
    public void With_the_player_off_a_command_shows_the_reason_and_the_row_hides()
    {
        _vm.PlayerOn = false;

        _vm.PlayCommand.Execute(null);

        Assert.False(_vm.IsEnabled);
        Assert.False(_vm.IsPlaying);
        Assert.True(_vm.HasError);
        Assert.Equal("player desligado", _vm.LastError);

        _vm.PlayerOn = true;
        _vm.PlayCommand.Execute(null);
        Assert.False(_vm.HasError);
        Assert.True(_vm.IsPlaying);
    }

    // ---- lists and output ----

    [Fact]
    public void Playlists_are_added_once_and_removed()
    {
        Assert.True(_vm.AddPlaylist("Culto", "/m/culto"));
        Assert.False(_vm.AddPlaylist("culto", "/m/other")); // same name
        Assert.False(_vm.AddPlaylist(" ", "/m/x"));
        Assert.Equal(["Igreja", "Culto"], _vm.Playlists.Select(p => p.Name));

        _vm.CurrentPlaylist = "Culto";
        Assert.Equal("Culto", _vm.PlaylistName);

        _vm.RemovePlaylist("Culto");

        Assert.Equal(["Igreja"], _vm.Playlists.Select(p => p.Name));
        Assert.Equal("Igreja", _rig.Settings.Current.Player.CurrentPlaylist);
    }

    [Fact]
    public void A_new_alarm_has_the_owner_defaults_and_a_changed_start_lets_it_fire_again()
    {
        var alarm = _vm.AddAlarm();

        Assert.Equal(2, _vm.Alarms.Count);
        Assert.Equal("18:00", alarm.Start);
        Assert.Equal("18:25", alarm.Latest);
        Assert.Equal(AlarmEntry.SundayOnly, alarm.Days);

        Change(p => p with { Alarms = p.Alarms.Select(a => a.Id == alarm.Id ? a with { LastFiredOn = "2026-10-25" } : a).ToList() });
        _vm.UpdateAlarm(_vm.Alarms.Single(a => a.Id == alarm.Id) with { Latest = "18:40" });
        Assert.Equal("2026-10-25", _vm.Alarms.Single(a => a.Id == alarm.Id).LastFiredOn); // same start: still fired today

        _vm.UpdateAlarm(_vm.Alarms.Single(a => a.Id == alarm.Id) with { Start = "19:00", Latest = "19:30" });
        Assert.Null(_vm.Alarms.Single(a => a.Id == alarm.Id).LastFiredOn);

        _vm.RemoveAlarm(alarm.Id);
        Assert.Single(_vm.Alarms);
    }

    [Fact]
    public void Choosing_an_output_keeps_id_and_name_and_none_goes_back_to_the_church_rule()
    {
        _vm.ChooseOutput(_audio.Devices[0]);

        Assert.Equal("id-1", _rig.Settings.Current.Player.OutputDeviceId);
        Assert.Equal("Alto-falantes (Realtek)", _vm.OutputDeviceName);
        Assert.Equal("Alto-falantes (Realtek)", _vm.OutputRuleText);

        _vm.ChooseOutput(null);

        Assert.Null(_rig.Settings.Current.Player.OutputDeviceId);
        Assert.Equal("WING + 3-4", _vm.OutputRuleText);

        _vm.SetOutputRule("WING + 1-2");
        Assert.Equal(["WING", "1-2"], _rig.Settings.Current.Player.OutputMatch);
    }

    // ---- the quick panel row ----

    [Fact]
    public void The_quick_panel_shows_the_player_row_only_while_the_player_is_on()
    {
        var gateway = new FakeGateway(_clock);
        using var withPlayer = new QuickPanelViewModel(gateway, new StatusHub(), clock: _clock, player: _vm);
        using var without = new QuickPanelViewModel(gateway, new StatusHub(), clock: _clock);
        var raised = new List<string>();
        withPlayer.PropertyChanged += (_, e) => raised.Add(e.PropertyName!);

        Assert.True(withPlayer.ShowPlayer);
        Assert.False(without.ShowPlayer);
        Assert.Null(without.Player);

        _vm.PlayerOn = false;

        Assert.False(withPlayer.ShowPlayer);
        Assert.Contains(nameof(QuickPanelViewModel.ShowPlayer), raised);
    }
    // The window is built and laid out without being shown: the row adds height only while the player is on.
    [Fact]
    public void The_panel_window_has_the_extra_row_only_while_the_player_is_on_and_is_unchanged_without_a_player()
    {
        WpfTestHost.Run(() =>
        {
            var gateway = new FakeGateway(_clock);
            using var plain = new QuickPanelViewModel(gateway, new StatusHub(), clock: _clock);
            using var withPlayer = new QuickPanelViewModel(gateway, new StatusHub(), clock: _clock, player: _vm);

            double HeightOf(QuickPanelViewModel model)
            {
                var window = new QuickPanel(model, null);
                var root = (FrameworkElement)window.Content;
                root.Measure(new Size(354, double.PositiveInfinity));
                return root.DesiredSize.Height;
            }

            var without = HeightOf(plain);
            var on = HeightOf(withPlayer);
            _vm.PlayerOn = false;
            var off = HeightOf(withPlayer);

            Assert.True(on > without + 20, $"the row should add height ({without} -> {on})");
            Assert.Equal(without, off);
        });
    }
}
