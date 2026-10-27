using AlertaIBT.Core.Player;
using AlertaIBT.Core.Settings;
using AlertaIBT.Core.Tests.Services;
using Microsoft.Extensions.Time.Testing;

namespace AlertaIBT.Core.Tests.Player;

// The alarms against a fake clock and a fake player: nothing here plays sound.
public sealed class AlarmSchedulerTests : IDisposable
{
    private sealed class FakeTarget : IAlarmTarget
    {
        public List<string> Starts { get; } = [];

        public AlarmStartOutcome Outcome { get; set; } = AlarmStartOutcome.Started;

        public AlarmStart StartFromAlarm(string playlistName)
        {
            Starts.Add(playlistName);
            return new AlarmStart(Outcome, Outcome == AlarmStartOutcome.Failed ? "no folder" : null);
        }
    }

    // 2026-10-25 is a Sunday.
    private static readonly DateTimeOffset Sunday1700 = new(2026, 10, 25, 17, 0, 0, TimeSpan.Zero);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "alerta-ibt-alarm-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(Sunday1700);
    private readonly FakeTarget _target = new();
    private readonly CapturingLogger _log = new();
    private readonly List<IDisposable> _disposables = [];
    private SettingsService _settings;

    public AlarmSchedulerTests()
    {
        Directory.CreateDirectory(_dir);
        _settings = NewSettings();
    }

    public void Dispose()
    {
        foreach (var item in _disposables)
        {
            item.Dispose();
        }

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

    private SettingsService NewSettings() => new(new SettingsStore(Path.Combine(_dir, "settings.json"), _clock), _clock);

    private AlarmScheduler Scheduler(bool start = true)
    {
        var scheduler = new AlarmScheduler(_settings, _target, _clock, _log);
        _disposables.Add(scheduler);
        if (start)
        {
            scheduler.Start();
        }

        return scheduler;
    }

    private void Change(Func<PlayerSettings, PlayerSettings> edit) => _settings.Update(s => s with { Player = edit(s.Player) });

    private void SetAlarm(Func<AlarmEntry, AlarmEntry> edit) =>
        Change(p => p with { Alarms = p.Alarms.Select(edit).ToList() });

    private void GoTo(int day, int hour, int minute) =>
        _clock.SetUtcNow(new DateTimeOffset(2026, 10, day, hour, minute, 0, TimeSpan.Zero));

    // ---- firing ----

    [Fact]
    public void The_default_alarm_fires_on_sunday_at_18_00_once_and_is_remembered()
    {
        Scheduler();

        GoTo(25, 17, 59);
        _clock.Advance(AlarmScheduler.CheckInterval);
        Assert.Empty(_target.Starts);

        GoTo(25, 18, 0);
        _clock.Advance(AlarmScheduler.CheckInterval);
        _clock.Advance(AlarmScheduler.CheckInterval);
        _clock.Advance(TimeSpan.FromMinutes(5));

        Assert.Equal(["Igreja"], _target.Starts);
        Assert.Equal("2026-10-25", _settings.Current.Player.Alarms[0].LastFiredOn);
    }

    [Fact]
    public void It_does_not_fire_on_a_day_that_is_not_selected()
    {
        Scheduler();

        GoTo(26, 18, 0); // Monday
        _clock.Advance(AlarmScheduler.CheckInterval);
        GoTo(28, 18, 10); // Wednesday
        _clock.Advance(AlarmScheduler.CheckInterval);

        Assert.Empty(_target.Starts);
    }

    [Fact]
    public void Another_selected_weekday_fires_too()
    {
        SetAlarm(a => a.WithDay(DayOfWeek.Wednesday, true));
        Scheduler();

        GoTo(28, 18, 0);
        _clock.Advance(AlarmScheduler.CheckInterval);

        Assert.Single(_target.Starts);
    }

    [Fact]
    public void An_app_opened_after_the_start_but_before_the_latest_time_starts_at_once()
    {
        GoTo(25, 18, 10);

        Scheduler();

        Assert.Single(_target.Starts);
    }

    [Fact]
    public void The_latest_time_itself_still_starts_and_one_minute_after_it_does_not()
    {
        GoTo(25, 18, 25);
        Scheduler();
        Assert.Single(_target.Starts);

        SetAlarm(a => a with { LastFiredOn = null });
        _target.Starts.Clear();
        GoTo(25, 18, 26);
        _clock.Advance(AlarmScheduler.CheckInterval);

        Assert.Empty(_target.Starts);
    }

    [Fact]
    public void An_app_opened_after_the_latest_time_does_not_start()
    {
        GoTo(25, 19, 0);

        Scheduler();

        Assert.Empty(_target.Starts);
    }

    [Fact]
    public void A_restart_the_same_day_does_not_fire_it_again_because_the_date_was_saved()
    {
        GoTo(25, 18, 1);
        var first = Scheduler();
        Assert.Single(_target.Starts);
        _settings.Flush();
        first.Dispose();
        _settings.Dispose();

        _settings = NewSettings(); // the app restarted: same file
        _clock.Advance(TimeSpan.FromMinutes(3));
        Scheduler();

        Assert.Single(_target.Starts);
        Assert.Equal("2026-10-25", _settings.Current.Player.Alarms[0].LastFiredOn);
    }

    [Fact]
    public void The_next_sunday_fires_again()
    {
        GoTo(25, 18, 1);
        Scheduler();

        _clock.SetUtcNow(new DateTimeOffset(2026, 11, 1, 18, 0, 0, TimeSpan.Zero));
        _clock.Advance(AlarmScheduler.CheckInterval);

        Assert.Equal(2, _target.Starts.Count);
    }

    [Fact]
    public void Music_that_is_already_playing_is_left_alone_and_the_day_counts_as_fired()
    {
        _target.Outcome = AlarmStartOutcome.AlreadyPlaying;
        GoTo(25, 18, 0);
        Scheduler();

        _clock.Advance(AlarmScheduler.CheckInterval);
        _clock.Advance(AlarmScheduler.CheckInterval);

        Assert.Single(_target.Starts);
        Assert.Equal("2026-10-25", _settings.Current.Player.Alarms[0].LastFiredOn);
    }

    [Fact]
    public void A_failed_start_is_tried_again_until_the_latest_time_and_then_given_up()
    {
        _target.Outcome = AlarmStartOutcome.Failed;
        GoTo(25, 18, 0);
        Scheduler();
        Assert.Single(_target.Starts);
        Assert.Null(_settings.Current.Player.Alarms[0].LastFiredOn);

        _clock.Advance(AlarmScheduler.CheckInterval);
        Assert.Equal(2, _target.Starts.Count);

        _target.Outcome = AlarmStartOutcome.Started; // the folder showed up
        _clock.Advance(AlarmScheduler.CheckInterval);
        Assert.Equal(3, _target.Starts.Count);
        Assert.Equal("2026-10-25", _settings.Current.Player.Alarms[0].LastFiredOn);
        Assert.Single(_log.Entries, e => e.Message.Contains("nao foi possivel iniciar", StringComparison.Ordinal)); // reported once
    }

    [Fact]
    public void A_failed_start_stops_being_tried_after_the_latest_time()
    {
        _target.Outcome = AlarmStartOutcome.Failed;
        GoTo(25, 18, 24);
        Scheduler();
        var tries = _target.Starts.Count;

        GoTo(25, 18, 30);
        _clock.Advance(AlarmScheduler.CheckInterval);

        Assert.Equal(tries, _target.Starts.Count);
    }

    // ---- the switches ----

    [Fact]
    public void With_the_alarms_master_switch_off_nothing_fires_and_turning_it_on_inside_the_window_fires_at_once()
    {
        Change(p => p with { AlarmsEnabled = false });
        GoTo(25, 18, 5);
        Scheduler();
        _clock.Advance(AlarmScheduler.CheckInterval);
        Assert.Empty(_target.Starts);

        Change(p => p with { AlarmsEnabled = true });

        Assert.Single(_target.Starts);
    }

    [Fact]
    public void With_the_player_off_no_alarm_fires_and_it_is_not_marked_as_fired()
    {
        Change(p => p with { Enabled = false });
        GoTo(25, 18, 5);
        Scheduler();
        _clock.Advance(AlarmScheduler.CheckInterval);

        Assert.Empty(_target.Starts);
        Assert.Null(_settings.Current.Player.Alarms[0].LastFiredOn);
    }

    [Fact]
    public void A_disabled_alarm_never_fires()
    {
        SetAlarm(a => a with { Enabled = false });
        GoTo(25, 18, 5);
        Scheduler();

        Assert.Empty(_target.Starts);
    }

    [Fact]
    public void Two_alarms_at_the_same_time_each_fire_once_with_their_own_playlist()
    {
        Change(p => p with
        {
            Alarms = [.. p.Alarms, AlarmEntry.CreateDefault("segundo", "Outra")],
        });
        GoTo(25, 18, 0);

        Scheduler();

        Assert.Equal(["Igreja", "Outra"], _target.Starts);
    }

    [Fact]
    public void An_alarm_edited_to_a_time_that_is_now_fires_without_waiting_for_the_timer()
    {
        GoTo(25, 17, 0);
        Scheduler();

        SetAlarm(a => a with { Start = "16:55", Latest = "17:30" });

        Assert.Single(_target.Starts);
    }

    // ---- local time ----

    [Fact]
    public void The_alarm_follows_the_local_wall_clock_across_the_end_of_daylight_saving_time()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        _clock.SetLocalTimeZone(zone);
        Scheduler();

        // 2026-11-01 is a Sunday and the clocks go back at 02:00 local: 18:00 local is 23:00 UTC (UTC-5), not 22:00.
        _clock.SetUtcNow(new DateTimeOffset(2026, 11, 1, 22, 59, 0, TimeSpan.Zero));
        _clock.Advance(AlarmScheduler.CheckInterval);
        Assert.Empty(_target.Starts);

        _clock.SetUtcNow(new DateTimeOffset(2026, 11, 1, 23, 0, 0, TimeSpan.Zero));
        _clock.Advance(AlarmScheduler.CheckInterval);

        Assert.Single(_target.Starts);
    }

    // ---- the next alarm line ----

    [Fact]
    public void The_next_alarm_line_names_the_weekday_and_says_today_when_it_is_today()
    {
        var scheduler = Scheduler();
        GoTo(25, 17, 30); // Sunday afternoon
        _clock.Advance(AlarmScheduler.CheckInterval);
        Assert.Equal("Pr\U000000F3ximo despertador: hoje 18:00", scheduler.NextAlarmText);

        GoTo(25, 18, 5); // fired: the next one is a week away
        _clock.Advance(AlarmScheduler.CheckInterval);
        Assert.Equal("Pr\U000000F3ximo despertador: domingo 18:00", scheduler.NextAlarmText);

        GoTo(28, 12, 0); // Wednesday
        _clock.Advance(AlarmScheduler.CheckInterval);
        Assert.Equal("Pr\U000000F3ximo despertador: domingo 18:00", scheduler.NextAlarmText);
    }

    [Fact]
    public void The_line_says_why_there_is_no_next_alarm_and_announces_changes()
    {
        var scheduler = Scheduler();
        var announced = 0;
        scheduler.NextAlarmChanged += () => announced++;

        Change(p => p with { AlarmsEnabled = false });
        Assert.Equal("Despertadores desligados", scheduler.NextAlarmText);

        Change(p => p with { AlarmsEnabled = true, Alarms = [] });
        Assert.Equal("Nenhum despertador ativo", scheduler.NextAlarmText);

        Change(p => p with { Enabled = false });
        Assert.Equal("Player desligado", scheduler.NextAlarmText);
        Assert.True(announced >= 3);
    }

    [Fact]
    public void The_pure_rules_agree_with_the_scheduler()
    {
        var player = new PlayerSettings();

        Assert.Single(AlarmRules.Due(player, new DateTime(2026, 10, 25, 18, 0, 0)));
        Assert.Empty(AlarmRules.Due(player, new DateTime(2026, 10, 25, 17, 59, 59)));
        Assert.Empty(AlarmRules.Due(player with { Enabled = false }, new DateTime(2026, 10, 25, 18, 0, 0)));
        Assert.Equal(new DateTime(2026, 11, 1, 18, 0, 0), AlarmRules.Next(player, new DateTime(2026, 10, 25, 18, 26, 0))!.Value.When);
    }
}
