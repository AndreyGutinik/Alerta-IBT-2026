using AlertaIBT.Core.Control;
using AlertaIBT.Core.Settings;
using Microsoft.Extensions.Time.Testing;

namespace AlertaIBT.Core.Tests.Control;

// The mapping engine: baseline first, edges, debounce, state follow, select, soft pickup, rate limit, signal loss.
public sealed class ControlEngineTests
{
    private readonly FakeTimeProvider _clock = new();
    private readonly Dictionary<ControlAction, double?> _app = [];
    private readonly byte[] _levels = new byte[512];
    private ControlEngine _engine;

    public ControlEngineTests()
    {
        _engine = Make(new ControlSettings().Rows);
    }

    private ControlEngine Make(IReadOnlyList<ControlRow> rows)
    {
        var engine = new ControlEngine(action => _app.TryGetValue(action, out var value) ? value : null, _clock);
        engine.SetRows(rows);
        return engine;
    }

    private IReadOnlyList<ControlCommand> Send(params (int Channel, byte Value)[] changes)
    {
        foreach (var (channel, value) in changes)
        {
            _levels[channel - 1] = value;
        }

        return _engine.Process(_levels);
    }

    private void Baseline() => Assert.Empty(Send());

    private void Wait(int ms) => _clock.Advance(TimeSpan.FromMilliseconds(ms));

    [Fact]
    public void The_first_packet_is_only_a_baseline_even_with_channels_held_on()
    {
        _levels[7] = 255; // ch 8 (Cortar) held ON by the console
        _levels[6] = 255; // ch 7 (state) held ON
        _levels[2] = 255; // ch 3 (select camera 3) held ON

        Assert.Empty(_engine.Process(_levels));
        Assert.True(_engine.HasBaseline);
    }

    [Fact]
    public void After_the_baseline_a_button_fires_on_any_change_by_default()
    {
        Baseline();

        var on = Send((8, 255));
        Wait(200);
        var off = Send((8, 0));

        Assert.Equal([new ControlCommand(8, ControlAction.Take, ControlCommandKind.Trigger)], on);
        Assert.Equal([new ControlCommand(8, ControlAction.Take, ControlCommandKind.Trigger)], off);
    }

    [Fact]
    public void A_rising_only_button_ignores_the_falling_edge()
    {
        var rows = new ControlSettings().Rows.Select(r => r.Channel == 8 ? r with { Trigger = ControlTrigger.RisingOnly } : r).ToList();
        _engine = Make(rows);
        Baseline();

        Assert.Single(Send((8, 255)));
        Wait(200);
        Assert.Empty(Send((8, 0)));
        Wait(200);
        Assert.Single(Send((8, 255)));
    }

    [Fact]
    public void A_button_does_not_fire_twice_within_100_ms()
    {
        Baseline();

        Assert.Single(Send((8, 255)));
        Wait(50);
        Assert.Empty(Send((8, 0))); // a bounce
        Wait(60);
        Assert.Single(Send((8, 255)));
    }

    [Fact]
    public void The_threshold_is_128()
    {
        Baseline();

        Assert.Empty(Send((8, 127)));
        Wait(200);
        Assert.Single(Send((8, 128)));
    }

    [Fact]
    public void A_held_value_does_not_repeat()
    {
        Baseline();

        Assert.Single(Send((8, 255)));
        Wait(200);
        Assert.Empty(Send((8, 255)));
        Assert.Empty(Send((8, 200)));
    }

    [Fact]
    public void Select_picks_the_camera_only_on_the_rising_edge()
    {
        Baseline();

        var up = Send((3, 255));
        Wait(200);
        var down = Send((3, 0));

        Assert.Equal([new ControlCommand(3, ControlAction.SelectCamera, ControlCommandKind.Select, Parameter: 3)], up);
        Assert.Empty(down);
    }

    [Fact]
    public void State_follows_the_channel_on_and_off_without_debounce()
    {
        Baseline();

        var on = Send((7, 255));
        var off = Send((7, 0));

        Assert.Equal([new ControlCommand(7, ControlAction.CameraOnTelao, ControlCommandKind.SetState, On: true)], on);
        Assert.Equal([new ControlCommand(7, ControlAction.CameraOnTelao, ControlCommandKind.SetState, On: false)], off);
    }

    [Fact]
    public void A_disabled_row_and_a_holyrics_row_never_fire_but_keep_tracking()
    {
        var rows = new ControlSettings().Rows.Select(r => r.Channel == 9 ? r with { Enabled = false } : r).ToList();
        _engine = Make(rows);
        Baseline();

        Assert.Empty(Send((9, 255))); // disabled
        Assert.Empty(Send((13, 255))); // Holyrics: slide next, not available
        Wait(200);

        // Enabling it later does not fire from the level that is already there.
        _engine.SetRows(new ControlSettings().Rows);
        Assert.Empty(Send((9, 255)));
    }

    [Fact]
    public void A_duplicate_channel_runs_both_actions()
    {
        var rows = new List<ControlRow>
        {
            new(5, ControlAction.Take, ControlMode.Button),
            new(5, ControlAction.Stop, ControlMode.Button),
        };
        _engine = Make(rows);
        Baseline();

        var commands = Send((5, 255));

        Assert.Equal([ControlAction.Take, ControlAction.Stop], commands.Select(c => c.Action));
    }

    [Fact]
    public void A_packet_shorter_than_the_channel_changes_nothing()
    {
        Baseline();

        Assert.Empty(_engine.Process(new byte[4]));
    }

    [Fact]
    public void The_levels_are_exposed_for_the_screen()
    {
        Send((8, 200), (21, 17));

        var states = _engine.GetRowStates();

        Assert.Equal(200, states[7].Level);
        Assert.True(states[7].On);
        Assert.Equal(17, states[20].Level);
        Assert.Equal(200, _engine.GetLevels()[7]);
    }

    // ----- faders and the soft pickup -----

    private IReadOnlyList<ControlCommand> Fader(int channel, byte value)
    {
        Wait(40); // longer than the 33 ms rate limit
        return Send((channel, value));
    }

    private static double Percent(byte value) => value * 100.0 / 255.0;

    [Fact]
    public void A_fader_waits_until_it_crosses_the_app_value_from_below()
    {
        _app[ControlAction.VolumePlayer] = 50;
        Send((21, 0));
        Baseline();

        Assert.Empty(Fader(21, 40)); // 15.7 %, below 50 %
        Assert.Empty(Fader(21, 100)); // 39 %
        Assert.Equal(FaderPickup.Waiting, _engine.GetRowStates()[20].Pickup);

        var crossing = Fader(21, 140); // 54.9 %: it crossed 50

        Assert.Equal(ControlCommandKind.SetLevel, Assert.Single(crossing).Kind);
        Assert.Equal(Percent(140), crossing[0].Value, 3);
        Assert.Equal(FaderPickup.Following, _engine.GetRowStates()[20].Pickup);
    }

    [Fact]
    public void A_fader_waits_until_it_crosses_the_app_value_from_above()
    {
        _app[ControlAction.VolumePlayer] = 30;
        Send((21, 255));
        Baseline();

        Assert.Empty(Fader(21, 200));
        Assert.Empty(Fader(21, 120)); // 47 %, still above 30
        var crossing = Fader(21, 60); // 23.5 %: crossed

        Assert.Single(crossing);
        Assert.Equal(Percent(60), crossing[0].Value, 3);
    }

    [Fact]
    public void A_fader_already_at_the_app_value_follows_from_its_first_move()
    {
        _app[ControlAction.VolumePlayer] = Percent(128);
        Send((21, 128));
        Baseline();

        Assert.Equal(FaderPickup.Following, _engine.GetRowStates()[20].Pickup);
        Assert.Empty(Send((21, 128)));
        Assert.Single(Fader(21, 150));
    }

    [Fact]
    public void A_following_fader_goes_back_to_waiting_when_the_app_value_changes_elsewhere()
    {
        _app[ControlAction.VolumePlayer] = 50;
        Send((21, 128));
        Baseline();
        Assert.Single(Fader(21, 140)); // following; the app now holds the last sent value
        _app[ControlAction.VolumePlayer] = Percent(140);

        _app[ControlAction.VolumePlayer] = 10; // the slider on the screen was dragged far away
        Assert.Empty(Fader(21, 150)); // the fader (59 %) moved but must not take over
        Assert.Empty(Fader(21, 170));
        Assert.Equal(FaderPickup.Waiting, _engine.GetRowStates()[20].Pickup);

        // Moving back down across 10 % picks it up again.
        Assert.Empty(Fader(21, 90));
        Assert.Empty(Fader(21, 40));
        Assert.Single(Fader(21, 20));
    }

    [Fact]
    public void With_the_pickup_off_a_fader_follows_at_once_and_never_waits()
    {
        var rows = new ControlSettings().Rows.Select(r => r.Channel == 21 ? r with { Pickup = false } : r).ToList();
        _engine = Make(rows);
        _app[ControlAction.VolumePlayer] = 90;
        Send((21, 0));
        Baseline();

        Assert.Equal(FaderPickup.None, _engine.GetRowStates()[20].Pickup);
        Assert.Empty(Send((21, 0)));
        var moved = Fader(21, 50);

        Assert.Equal(Percent(50), Assert.Single(moved).Value, 3);
    }

    [Fact]
    public void A_fader_whose_app_value_is_unknown_stays_waiting()
    {
        _app[ControlAction.PreviewVertical] = null; // no camera in Preview
        Send((17, 10));
        Baseline();

        Assert.Empty(Fader(17, 200));
        Assert.Equal(FaderPickup.Waiting, _engine.GetRowStates()[16].Pickup);
    }

    [Fact]
    public void Fader_updates_are_limited_to_about_30_per_second_and_the_newest_value_wins()
    {
        var rows = new ControlSettings().Rows.Select(r => r.Channel == 21 ? r with { Pickup = false } : r).ToList();
        _engine = Make(rows);
        Send((21, 0));
        Baseline();
        Wait(100);

        var first = Send((21, 10)); // quiet row: at once
        Wait(10);
        var second = Send((21, 20)); // held back
        Wait(10);
        var third = Send((21, 30)); // replaces the held one
        Assert.Single(first);
        Assert.Empty(second);
        Assert.Empty(third);
        Assert.Empty(_engine.Tick()); // 20 ms after the first: too early

        Wait(20);
        var flushed = _engine.Tick();

        Assert.Equal(Percent(30), Assert.Single(flushed).Value, 3);
        Wait(40);
        Assert.Empty(_engine.Tick());
    }

    // ----- signal -----

    [Fact]
    public void No_packet_for_three_seconds_is_no_signal_and_the_next_packet_is_a_new_baseline()
    {
        Baseline();
        Assert.True(_engine.HasSignal);

        Wait(2900);
        _engine.Tick();
        Assert.True(_engine.HasSignal);
        Wait(200);
        Assert.Empty(_engine.Tick());
        Assert.False(_engine.HasSignal);
        Assert.False(_engine.HasBaseline);
        Assert.True(_engine.SinceLastPacket >= TimeSpan.FromSeconds(3));

        // The console comes back holding Cortar ON: that is the baseline, not a cut.
        _levels[7] = 255;
        Assert.Empty(_engine.Process(_levels));
        Assert.True(_engine.HasSignal);
        Wait(200);
        _levels[7] = 0;
        Assert.Single(_engine.Process(_levels));
    }

    [Fact]
    public void Losing_the_signal_makes_every_fader_wait_again()
    {
        _app[ControlAction.VolumePlayer] = 50;
        Send((21, 0));
        Baseline();
        Fader(21, 140);
        Assert.Equal(FaderPickup.Following, _engine.GetRowStates()[20].Pickup);

        Wait(3100);
        _engine.Tick();

        Assert.Equal(FaderPickup.Waiting, _engine.GetRowStates()[20].Pickup);
    }

    [Fact]
    public void Reset_forgets_the_baseline()
    {
        Baseline();
        _engine.Reset();

        Assert.False(_engine.HasSignal);
        Assert.Null(_engine.SinceLastPacket);
        Assert.Empty(_engine.Process(_levels));
    }
}
