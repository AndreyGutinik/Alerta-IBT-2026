using AlertaIBT.Core.Audio;
using AlertaIBT.Core.Settings;
using Microsoft.Extensions.Time.Testing;

namespace AlertaIBT.Core.Tests.Audio;

// A slider, a number or a mute reaches the (fake) Windows audio the moment it is set: no 600 ms autosave, no 500 ms look, no session listing per
// change. With "Suavizar mudanca de volume" the volume walks to the target on a 25 ms timer (fake clock), and a newer change retargets the walk.
public sealed class LiveVolumeTests : IDisposable
{
    private const string Speakers = FakeAudioSystem.SpeakersId;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "alerta-ibt-live-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 22, 8, 0, 0, TimeSpan.Zero));
    private readonly FakeAudioSystem _audio = new();
    private readonly SettingsService _settings;
    private readonly AudioController _controller;

    public LiveVolumeTests()
    {
        Directory.CreateDirectory(_dir);
        _settings = new SettingsService(new SettingsStore(Path.Combine(_dir, "settings.json"), _clock), _clock);
        _controller = new AudioController(_audio, _settings, _clock, null, null, action => action());
    }

    public void Dispose()
    {
        _controller.Dispose();
        _settings.Dispose();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // A scanner may hold a file for a moment.
        }
    }

    private void Ramp(double seconds) => _settings.Update(s => s with { Audio = s.Audio with { VolumeRampSeconds = seconds } });

    private List<(int Volume, bool Muted)> MasterCalls() =>
        [.. _audio.Writes("master").Select(c => c.Split(':')).Select(p => (int.Parse(p[^2]), bool.Parse(p[^1])))];

    // Advances the fake clock in 25 ms steps (as the timer fires) for `seconds`.
    private void Run(double seconds)
    {
        for (var i = 0; i < (int)Math.Round(seconds / 0.025); i++)
        {
            _clock.Advance(AudioController.RampStep);
        }
    }

    [Fact]
    public void A_slider_change_reaches_the_audio_with_no_clock_moving_at_all()
    {
        _controller.SetLive(AudioController.LiveMaster, 55, false);

        Assert.Equal([(55, false)], MasterCalls()); // the clock never advanced: no debounce, no autosave in the way
        Assert.Equal(0, _audio.SessionReads); // and no session listing for a master change
    }

    [Fact]
    public void Every_change_is_a_write_and_the_master_never_lists_sessions()
    {
        foreach (var volume in new[] { 10, 20, 30, 40 })
        {
            _controller.SetLive(AudioController.LiveMaster, volume, false);
        }

        Assert.Equal([10, 20, 30, 40], MasterCalls().Select(c => c.Volume));
        Assert.Equal(0, _audio.SessionReads);
    }

    [Fact]
    public void A_mute_applies_at_once_with_no_ramp()
    {
        _controller.SetLive(AudioController.LiveMaster, 80, true);
        _controller.SetLive(AudioController.LiveMaster, 80, false);

        Assert.Equal([(80, true), (80, false)], MasterCalls());
    }

    [Fact]
    public void A_program_volume_reaches_its_sessions_and_the_session_list_is_read_once()
    {
        _audio.AddSession(Speakers, "holy-1", "javaw", raise: false);
        _audio.AddSession(Speakers, "other", "chrome", raise: false);

        _controller.SetLive(AudioController.LiveApp("Holyrics"), 40, false);
        _controller.SetLive(AudioController.LiveApp("Holyrics"), 45, false);
        _controller.SetLive(AudioController.LiveApp("Holyrics"), 50, true);

        Assert.Equal(
            [$"session:{Speakers}:holy-1:40:False", $"session:{Speakers}:holy-1:45:False", $"session:{Speakers}:holy-1:50:True"],
            _audio.Writes("session"));
        Assert.Equal(1, _audio.SessionReads);
    }

    [Fact]
    public void The_system_sounds_row_reaches_the_system_session()
    {
        _audio.AddSession(Speakers, "sys", null, system: true, raise: false);
        _audio.AddSession(Speakers, "holy-1", "javaw", raise: false);

        _controller.SetLive(AudioController.LiveSystemSounds, 7, false);

        Assert.Equal([$"session:{Speakers}:sys:7:False"], _audio.Writes("session"));
    }

    [Fact]
    public void A_session_that_went_away_is_looked_for_again_once()
    {
        _audio.AddSession(Speakers, "holy-1", "javaw", raise: false);
        _controller.SetLive(AudioController.LiveApp("Holyrics"), 40, false);
        _audio.Sessions[Speakers].Clear();
        _audio.AddSession(Speakers, "holy-2", "javaw", raise: false);
        _audio.FailSetVolume = false;

        _controller.SetLive(AudioController.LiveApp("Holyrics"), 60, false);

        // The cached list still names holy-1 (the fake accepts any write), so the new session is found on the next event of Windows.
        Assert.Contains($"session:{Speakers}:holy-1:60:False", _audio.Writes("session"));
    }

    [Fact]
    public void With_a_ramp_the_volume_walks_to_the_target_monotonically_in_that_time()
    {
        Ramp(2);
        _controller.SetLive(AudioController.LiveMaster, 20, false); // the saved master is 80

        Assert.Empty(MasterCalls()); // nothing jumps at the first instant
        Run(2.5);

        var volumes = MasterCalls().Select(c => c.Volume).ToList();
        Assert.Equal(20, volumes[^1]);
        Assert.True(volumes.Zip(volumes.Skip(1)).All(p => p.Second <= p.First), "monotonic down");
        Assert.InRange(volumes[0], 78, 80);
        Assert.InRange(volumes.Count, 70, 90); // about one write per 25 ms for 2 s, then it stops
        var middle = volumes[volumes.Count / 2];
        Assert.InRange(middle, 45, 55); // the smooth curve crosses the middle at the middle
        var before = volumes.Count;
        Run(1);
        Assert.Equal(before, MasterCalls().Count); // the timer rests when the target is reached
    }

    [Fact]
    public void The_walk_takes_the_chosen_time()
    {
        Ramp(1);
        _controller.SetLive(AudioController.LiveMaster, 0, false);

        Run(0.9);
        Assert.True(MasterCalls()[^1].Volume > 0, "still on its way at 0.9 s");
        Run(0.2);
        Assert.Equal(0, MasterCalls()[^1].Volume);
    }

    [Fact]
    public void A_new_change_retargets_a_running_ramp_from_where_it_is()
    {
        Ramp(2);
        _controller.SetLive(AudioController.LiveMaster, 20, false); // down from 80
        Run(1.0);
        var atChange = MasterCalls()[^1].Volume;
        Assert.InRange(atChange, 40, 60);

        _controller.SetLive(AudioController.LiveMaster, 90, false); // changed his mind
        Run(2.5);

        var after = MasterCalls().Skip(MasterCalls().Count(c => true) - 85).Select(c => c.Volume).ToList();
        Assert.Equal(90, MasterCalls()[^1].Volume);
        var rising = MasterCalls().SkipWhile(c => c.Volume != atChange).Skip(1).Select(c => c.Volume).ToList();
        Assert.True(rising.Zip(rising.Skip(1)).All(p => p.Second >= p.First - 1), "it turns around and rises");
        Assert.True(rising[0] >= atChange - 2 && rising[0] <= atChange + 6, "it continues from the level it had, not from 80");
        _ = after;
    }

    [Fact]
    public void A_mute_with_a_ramp_fades_to_silence_then_mutes_and_the_unmute_rises_from_silence()
    {
        Ramp(1);
        _controller.SetLive(AudioController.LiveMaster, 80, true);
        Run(1.2);

        var down = MasterCalls();
        Assert.Equal((0, true), down[^1]);
        Assert.True(down.Take(down.Count - 1).All(c => !c.Muted), "the fade is audible: unmuted until the end");
        Assert.True(down.Zip(down.Skip(1)).All(p => p.Second.Volume <= p.First.Volume));

        var marker = down.Count;
        _controller.SetLive(AudioController.LiveMaster, 80, false);
        Run(1.2);

        var up = MasterCalls().Skip(marker).ToList();
        Assert.Equal((0, false), up[0]); // unmuted at silence
        Assert.True(up.Zip(up.Skip(1)).All(p => p.Second.Volume >= p.First.Volume));
        Assert.Equal((80, false), up[^1]);
    }

    [Fact]
    public void Each_volume_ramps_on_its_own()
    {
        Ramp(1);
        _audio.AddSession(Speakers, "holy-1", "javaw", raise: false);
        _controller.SetLive(AudioController.LiveMaster, 10, false);
        _controller.SetLive(AudioController.LiveApp("Holyrics"), 10, false); // saved at 100

        Run(1.2);

        Assert.Equal(10, MasterCalls()[^1].Volume);
        Assert.Equal($"session:{Speakers}:holy-1:10:False", _audio.Writes("session").Last());
        Assert.True(_audio.Writes("session").Count() > 20);
    }

    [Fact]
    public void The_curve_is_smooth_at_both_ends()
    {
        Assert.Equal(0, AudioController.RampCurve(0));
        Assert.Equal(1, AudioController.RampCurve(1));
        Assert.InRange(AudioController.RampCurve(0.5), 0.499, 0.501);
        Assert.True(AudioController.RampCurve(0.1) < 0.1, "gentle start");
        Assert.True(AudioController.RampCurve(0.9) > 0.9, "gentle end");
    }
}
