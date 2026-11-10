using AlertaIBT.Core.Holyrics;
using AlertaIBT.Core.Player;
using AlertaIBT.Core.Settings;
using AlertaIBT.Core.Tests.Services;
using AlertaIBT.Core.Tests.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace AlertaIBT.Core.Tests.Holyrics;

// "Parar a musica quando o cronometro chegar a N s": the trigger, the reading of the real Holyrics answers (checked against the real Holyrics on 2026-11-01),
// the watcher (fake source, fake player, fake clock) and the client against the fake API server.
public sealed class CountdownWatcherTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ibt-countdown-" + Guid.NewGuid().ToString("N"));
    private readonly FakeHolyricsServer _server = new();
    private readonly FakeProtector _protector = new();
    private readonly CapturingLogger _log = new();
    private readonly SettingsService _settings;
    private HolyricsClient? _client;

    public CountdownWatcherTests()
    {
        Directory.CreateDirectory(_dir);
        _settings = new SettingsService(new SettingsStore(Path.Combine(_dir, "settings.json")));
        _settings.Update(s =>
        {
            var withToken = s.WithHolyricsToken(FakeHolyricsServer.GoodToken, _protector);
            return withToken with { Holyrics = withToken.Holyrics with { Host = "127.0.0.1", Port = _server.Port } };
        });
    }

    public void Dispose()
    {
        _client?.Dispose();
        _server.Dispose();
        _settings.Dispose();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // a late write of the settings
        }
    }

    private HolyricsClient Client => _client ??= new HolyricsClient(_settings, _protector, _log, minInterval: TimeSpan.Zero);

    // ---- the trigger -------------------------------------------------------------------------------------------------------------------------------

    private static int Fires(IEnumerable<double?> readings, double threshold = 2)
    {
        var trigger = new CountdownTrigger();
        return readings.Count(r => trigger.Observe(r, threshold));
    }

    [Fact]
    public void A_ten_second_countdown_fires_once_at_two_and_not_at_three()
    {
        var trigger = new CountdownTrigger();
        var firedAt = new List<double>();
        for (var seconds = 10; seconds >= 0; seconds--)
        {
            // each value is seen four times (the fast poll)
            for (var look = 0; look < 4; look++)
            {
                if (trigger.Observe(seconds, 2))
                {
                    firedAt.Add(seconds);
                }
            }
        }

        Assert.Equal([2d], firedAt);
    }

    [Fact]
    public void The_panel_countdown_going_below_zero_does_not_fire_again()
    {
        Assert.Equal(1, Fires([5, 4, 3, 2, 1, 0, -1, -2, -3]));
    }

    [Fact]
    public void A_countdown_that_is_hidden_and_shown_again_is_a_new_run()
    {
        Assert.Equal(2, Fires([4, 3, 2, 1, null, null, 6, 5, 4, 3, 2, 1]));
    }

    [Fact]
    public void A_restart_with_more_time_is_a_new_run_even_without_a_hidden_reading()
    {
        Assert.Equal(2, Fires([3, 2, 1, 10, 9, 3, 2, 1]));
    }

    [Fact]
    public void A_restart_at_a_value_still_inside_the_threshold_is_also_a_new_run()
    {
        Assert.Equal(2, Fires([2, 1, 0, 2, 1]));
    }

    [Fact]
    public void A_countdown_first_seen_at_or_below_the_threshold_fires_once()
    {
        Assert.Equal(1, Fires([1, 1, 0]));
        Assert.Equal(1, Fires([2, 2, 1]));
    }

    [Fact]
    public void Three_seconds_is_not_two()
    {
        Assert.Equal(0, Fires([10, 9, 8, 7, 6, 5, 4, 3, 3, 3]));
    }

    [Fact]
    public void The_threshold_is_the_owners_choice()
    {
        Assert.Equal(1, Fires([10, 9, 8, 7, 6, 5, 4, 3], threshold: 5));
        Assert.Equal(1, Fires([10, 9, 1], threshold: 1));
        Assert.Equal(0, Fires([10, 9, 2], threshold: 1));
    }

    [Fact]
    public void Reset_makes_the_next_reading_a_new_run()
    {
        var trigger = new CountdownTrigger();
        Assert.True(trigger.Observe(2, 2));
        Assert.False(trigger.Observe(1, 2));
        trigger.Reset();
        Assert.True(trigger.Observe(1, 2));
    }

    // ---- the units ---------------------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("0:10", 10)]
    [InlineData("0:02", 2)]
    [InlineData("0:00", 0)]
    [InlineData("10:00", 600)]
    [InlineData("1:02", 62)]
    [InlineData("1:00:05", 3605)]
    [InlineData(" 0:07 ", 7)]
    public void The_presentation_countdown_time_is_read_as_minutes_and_seconds(string text, double expected)
    {
        Assert.Equal(expected, CountdownText.Parse(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("7")]
    [InlineData("abc")]
    [InlineData("1:2:3:4")]
    [InlineData("-1:00")]
    [InlineData("0:x")]
    public void Anything_else_is_not_a_time(string? text)
    {
        Assert.Null(CountdownText.Parse(text));
    }

    [Theory]
    [InlineData(7, "00:07")]
    [InlineData(0, "00:00")]
    [InlineData(-3, "00:00")]
    [InlineData(62, "01:02")]
    [InlineData(600, "10:00")]
    [InlineData(3725, "1:02:05")]
    [InlineData(7.9, "00:07")]
    public void The_chip_shows_the_countdown_as_minutes_and_seconds(double seconds, string expected)
    {
        Assert.Equal(expected, CountdownText.Format(seconds));
    }

    // ---- the cadence -------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_pace_is_slow_when_nothing_is_shown_fast_while_a_countdown_is_and_off_when_the_watch_is_off()
    {
        Assert.Equal(TimeSpan.FromSeconds(1), CountdownWatcher.NextDelay(active: true, countdownShown: false, failures: 0));
        Assert.Equal(TimeSpan.FromMilliseconds(250), CountdownWatcher.NextDelay(active: true, countdownShown: true, failures: 0));
        Assert.Equal(TimeSpan.FromSeconds(1), CountdownWatcher.NextDelay(active: false, countdownShown: true, failures: 0));
    }

    [Fact]
    public void An_unreachable_holyrics_is_asked_less_and_less_up_to_a_minute()
    {
        var delays = Enumerable.Range(1, 8).Select(f => CountdownWatcher.NextDelay(active: true, countdownShown: false, failures: f).TotalSeconds).ToArray();

        Assert.Equal([2d, 5, 15, 30, 60, 60, 60, 60], delays);
    }

    // ---- the watcher -------------------------------------------------------------------------------------------------------------------------------

    private sealed class FakeSource : IHolyricsCountdownSource
    {
        private readonly Queue<CountdownReading> _script = new();
        private int _reads;

        public bool IsConfigured { get; set; } = true;

        public int Reads => Volatile.Read(ref _reads);

        public CountdownReading Fallback { get; set; } = new(HolyricsOutcome.Ok);

        public void Script(params CountdownReading[] readings)
        {
            lock (_script)
            {
                foreach (var reading in readings)
                {
                    _script.Enqueue(reading);
                }
            }
        }

        public Task<CountdownReading> ReadCountdownAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _reads);
            lock (_script)
            {
                return Task.FromResult(_script.Count > 0 ? _script.Dequeue() : Fallback);
            }
        }
    }

    private sealed class FakePlayer : IPlayerControl
    {
        public PlayerState State { get; set; } = PlayerState.Playing;

        public int Pauses { get; private set; }

        public int Stops { get; private set; }

        public PlayerResult PauseResult { get; set; } = PlayerResult.Ok;

        public bool Throws { get; set; }

        public PlayerResult Play() => PlayerResult.Ok;

        public PlayerResult Pause()
        {
            Pauses++;
            if (Throws)
            {
                throw new InvalidOperationException("boom");
            }

            return PauseResult;
        }

        public PlayerResult Stop()
        {
            Stops++;
            return PlayerResult.Ok;
        }

        public PlayerResult Next() => PlayerResult.Ok;

        public PlayerResult Previous() => PlayerResult.Ok;

        public PlayerStatus GetStatus() =>
            new(true, State, false, null, TimeSpan.Zero, TimeSpan.Zero, "x", 0, 100, false, false, PlayerRepeat.Off, OutputStatus.Unknown, null);
    }

    private static CountdownReading Panel(double seconds) => new(HolyricsOutcome.Ok, CountdownKind.Panel, seconds);

    private static readonly CountdownReading Nothing = new(HolyricsOutcome.Ok);

    private (CountdownWatcher Watcher, FakeSource Source, FakePlayer Player) Make(FakeTimeProvider? clock = null)
    {
        var source = new FakeSource();
        var player = new FakePlayer();
        return (new CountdownWatcher(_settings, source, player, _log, clock), source, player);
    }

    [Fact]
    public async Task The_countdown_ten_to_zero_pauses_the_playing_music_exactly_once_with_the_fade_out_path()
    {
        var (watcher, source, player) = Make();
        for (var s = 10; s >= 0; s--)
        {
            source.Script(Panel(s), Panel(s), Panel(s));
        }

        for (var i = 0; i < 33; i++)
        {
            await watcher.PollOnceAsync(TestContext.Current.CancellationToken);
        }

        Assert.Equal(1, player.Pauses); // Pause() is the stop with the owner's fade-out: the same as /player/pause
        Assert.Equal(0, player.Stops);
        Assert.Equal(1, watcher.Triggers);
        Assert.Contains(_log.Entries, e => e.Level == LogLevel.Information && e.Message == "Cron\U000000F4metro do Holyrics chegou a 2 s: m\U000000FAsica parada");
        Assert.Single(_log.Entries, e => e.Message.StartsWith("Cron\U000000F4metro do Holyrics chegou", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Nothing_happens_before_the_threshold()
    {
        var (watcher, source, player) = Make();
        source.Script(Panel(10), Panel(5), Panel(3), Panel(3));

        for (var i = 0; i < 4; i++)
        {
            await watcher.PollOnceAsync(TestContext.Current.CancellationToken);
        }

        Assert.Equal(0, player.Pauses);
    }

    [Fact]
    public async Task A_countdown_that_starts_at_one_second_pauses_once_when_first_seen()
    {
        var (watcher, source, player) = Make();
        source.Script(Panel(1), Panel(1), Panel(0), Nothing);

        for (var i = 0; i < 4; i++)
        {
            await watcher.PollOnceAsync(TestContext.Current.CancellationToken);
        }

        Assert.Equal(1, player.Pauses);
    }

    [Fact]
    public async Task A_second_run_after_a_restart_pauses_again()
    {
        var (watcher, source, player) = Make();
        source.Script(Panel(3), Panel(2), Panel(1), Nothing, Panel(8), Panel(2), Panel(1));

        for (var i = 0; i < 7; i++)
        {
            await watcher.PollOnceAsync(TestContext.Current.CancellationToken);
        }

        Assert.Equal(2, player.Pauses);
    }

    [Fact]
    public async Task A_player_that_is_not_playing_is_left_alone_without_an_error()
    {
        var (watcher, source, player) = Make();
        player.State = PlayerState.Paused;
        source.Script(Panel(3), Panel(2), Panel(1));

        for (var i = 0; i < 3; i++)
        {
            await watcher.PollOnceAsync(TestContext.Current.CancellationToken);
        }

        Assert.Equal(0, player.Pauses);
        Assert.Equal(0, player.Stops);
        Assert.DoesNotContain(_log.Entries, e => e.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task A_player_that_refuses_or_throws_is_logged_once_and_never_breaks_the_watch()
    {
        var (watcher, source, player) = Make();
        player.PauseResult = PlayerResult.Fail("sem sa\U000000EDda");
        source.Script(Panel(2), Panel(1), Panel(0));
        for (var i = 0; i < 3; i++)
        {
            await watcher.PollOnceAsync(TestContext.Current.CancellationToken);
        }

        Assert.Equal(1, player.Pauses);
        Assert.Single(_log.Entries, e => e.Level == LogLevel.Warning);

        player.Throws = true;
        source.Script(Nothing, Panel(2));
        await watcher.PollOnceAsync(TestContext.Current.CancellationToken);
        await watcher.PollOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, player.Pauses);
    }

    [Fact]
    public async Task The_switch_off_or_no_connection_asks_nothing_and_stops_nothing()
    {
        var (watcher, source, player) = Make();
        source.Script(Panel(1));
        _settings.Update(s => s with { Holyrics = s.Holyrics with { CountdownStopEnabled = false } });

        var delay = await watcher.PollOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, source.Reads);
        Assert.Equal(TimeSpan.FromSeconds(1), delay);
        Assert.Equal(0, player.Pauses);

        _settings.Update(s => s with { Holyrics = s.Holyrics with { CountdownStopEnabled = true } });
        source.IsConfigured = false;
        await watcher.PollOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, source.Reads);
        Assert.Equal(CountdownSnapshot.Idle, watcher.Current);
    }

    [Fact]
    public async Task The_threshold_setting_is_used()
    {
        var (watcher, source, player) = Make();
        _settings.Update(s => s with { Holyrics = s.Holyrics with { CountdownStopAt = 5 } });
        source.Script(Panel(7), Panel(6), Panel(5), Panel(4));

        for (var i = 0; i < 4; i++)
        {
            await watcher.PollOnceAsync(TestContext.Current.CancellationToken);
        }

        Assert.Equal(1, player.Pauses);
        Assert.Contains(_log.Entries, e => e.Message.Contains("chegou a 5 s", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_pace_follows_what_was_seen_and_backs_off_when_holyrics_does_not_answer_and_comes_back_at_once()
    {
        var (watcher, source, _) = Make();
        var down = new CountdownReading(HolyricsOutcome.Unreachable, Message: "sem resposta");
        source.Script(Nothing, Panel(9), down, down, down, Panel(8), Nothing);

        var delays = new List<double>();
        for (var i = 0; i < 7; i++)
        {
            delays.Add((await watcher.PollOnceAsync(TestContext.Current.CancellationToken)).TotalMilliseconds);
        }

        Assert.Equal([1000d, 250, 2000, 5000, 15000, 250, 1000], delays);
    }

    [Fact]
    public async Task A_blink_of_holyrics_in_the_middle_of_a_run_does_not_stop_the_music_twice()
    {
        var (watcher, source, player) = Make();
        var down = new CountdownReading(HolyricsOutcome.Unreachable, Message: "sem resposta");
        source.Script(Panel(3), Panel(2), down, down, Panel(1), Panel(0));

        for (var i = 0; i < 6; i++)
        {
            await watcher.PollOnceAsync(TestContext.Current.CancellationToken);
        }

        Assert.Equal(1, player.Pauses);
    }

    [Fact]
    public async Task The_live_chip_shows_the_countdown_only_while_one_is_shown_and_a_token_problem_is_told()
    {
        var (watcher, source, _) = Make();
        var seen = new List<CountdownSnapshot>();
        watcher.Changed += () => seen.Add(watcher.Current);
        source.Script(Nothing, Panel(7), Panel(6), Nothing, new CountdownReading(HolyricsOutcome.Unauthorized, Message: "token sem permiss\U000000E3o para GetCPInfo"));

        for (var i = 0; i < 5; i++)
        {
            await watcher.PollOnceAsync(TestContext.Current.CancellationToken);
        }

        Assert.Equal([7d, 6d, null, null], seen.Select(s => s.Seconds));
        Assert.Equal("token sem permiss\U000000E3o para GetCPInfo", watcher.Current.Problem);
    }

    [Fact]
    public async Task The_background_loop_follows_the_fake_clock()
    {
        var clock = new FakeTimeProvider();
        var (watcher, source, player) = Make(clock);
        source.Script(Nothing, Panel(4), Panel(3), Panel(2), Panel(1));
        using (watcher)
        {
            watcher.Start();
            await WaitFor(() => source.Reads == 1);
            await Settle(); // the loop is asleep (Reads counts the look that has just begun)
            Assert.Equal(1, source.Reads); // nothing more until the clock moves

            clock.Advance(TimeSpan.FromMilliseconds(999));
            await Task.Delay(50, TestContext.Current.CancellationToken);
            Assert.Equal(1, source.Reads);

            clock.Advance(TimeSpan.FromMilliseconds(1)); // the slow pace is one second
            await WaitFor(() => source.Reads == 2);
            await Settle();

            clock.Advance(TimeSpan.FromMilliseconds(250)); // a countdown is shown: the fast pace
            await WaitFor(() => source.Reads == 3);
            await Settle();
            clock.Advance(TimeSpan.FromMilliseconds(250));
            await WaitFor(() => source.Reads == 4);
            await Settle();
            clock.Advance(TimeSpan.FromMilliseconds(250));
            await WaitFor(() => source.Reads == 5);
            await WaitFor(() => player.Pauses == 1);
        }

        Assert.Equal(1, player.Pauses);
    }


    // ---- an outage is told once ---------------------------------------------------------------------------------------------------------------------

    private static CountdownReading Down(string message = "sem resposta: conex\U000000E3o recusada em 192.168.1.13:8091") =>
        new(HolyricsOutcome.Unreachable, Message: message, Detail: "a\U000000E7\U000000E3o: GetCPInfo; endere\U000000E7o: 192.168.1.13:8091; tempo: 2002 ms");

    private IEnumerable<(LogLevel Level, string Message)> Told => _log.Entries.Where(e => e.Level >= LogLevel.Information);

    [Fact]
    public async Task A_hundred_failed_polls_leave_one_warning_with_the_full_reason_and_nothing_else()
    {
        var clock = new FakeTimeProvider();
        var (watcher, source, _) = Make(clock);
        source.Fallback = Down();

        for (var i = 0; i < 100; i++)
        {
            await watcher.PollOnceAsync(TestContext.Current.CancellationToken);
            clock.Advance(TimeSpan.FromSeconds(10)); // 100 polls over 16 minutes: no reminder yet
        }

        var line = Assert.Single(Told);
        Assert.Equal(LogLevel.Warning, line.Level);
        Assert.StartsWith("Holyrics: cron\U000000F4metro falhou (Unreachable): sem resposta: conex\U000000E3o recusada em 192.168.1.13:8091 [a\U000000E7\U000000E3o: GetCPInfo;", line.Message, StringComparison.Ordinal);
        Assert.Contains("tempo: 2002 ms]", line.Message, StringComparison.Ordinal);
        Assert.Equal(100, source.Reads);
    }

    [Fact]
    public async Task The_chip_and_the_pace_of_a_closed_holyrics_follow_the_new_backoff_and_the_chip_does_not_repaint()
    {
        var clock = new FakeTimeProvider();
        var (watcher, source, _) = Make(clock);
        var seen = new List<CountdownSnapshot>();
        watcher.Changed += () => seen.Add(watcher.Current);
        source.Fallback = Down();

        var delays = new List<double>();
        for (var i = 0; i < 7; i++)
        {
            delays.Add((await watcher.PollOnceAsync(TestContext.Current.CancellationToken)).TotalSeconds);
        }

        Assert.Equal([2d, 5, 15, 30, 60, 60, 60], delays);
        Assert.Empty(seen); // Idle and "unreachable" look the same on the chip: nothing changed, nothing is repainted
        Assert.Equal(CountdownSnapshot.Idle, watcher.Current);
    }

    [Fact]
    public async Task When_holyrics_answers_again_one_information_line_says_how_long_and_how_many_tries()
    {
        var clock = new FakeTimeProvider();
        var (watcher, source, _) = Make(clock);
        source.Script(Down(), Down(), Down(), Nothing, Nothing);

        for (var i = 0; i < 3; i++)
        {
            await watcher.PollOnceAsync(TestContext.Current.CancellationToken);
            clock.Advance(TimeSpan.FromSeconds(150));
        }

        await watcher.PollOnceAsync(TestContext.Current.CancellationToken); // it answers at the 7.5th minute
        await watcher.PollOnceAsync(TestContext.Current.CancellationToken); // and keeps answering: nothing more to say

        Assert.Equal(2, Told.Count());
        var recovered = Told.Last();
        Assert.Equal(LogLevel.Information, recovered.Level);
        Assert.Equal("Holyrics voltou a responder ap\U000000F3s 7 min, 3 tentativas", recovered.Message);
    }

    [Fact]
    public async Task While_it_stays_down_there_is_one_reminder_per_hour_and_none_in_between()
    {
        var clock = new FakeTimeProvider();
        var (watcher, source, _) = Make(clock);
        source.Fallback = Down();

        for (var i = 0; i < 8; i++)
        {
            await watcher.PollOnceAsync(TestContext.Current.CancellationToken);
            clock.Advance(TimeSpan.FromMinutes(31)); // polls at 0, 31, 62, 93, 124, 155, 186 and 217 minutes
        }

        var lines = Told.ToList();
        Assert.Equal(4, lines.Count);
        Assert.Equal(LogLevel.Warning, lines[0].Level);
        Assert.StartsWith("Holyrics continua sem resposta h\U000000E1 1 h", lines[1].Message, StringComparison.Ordinal);
        Assert.StartsWith("Holyrics continua sem resposta h\U000000E1 2 h", lines[2].Message, StringComparison.Ordinal);
        Assert.StartsWith("Holyrics continua sem resposta h\U000000E1 3 h", lines[3].Message, StringComparison.Ordinal);
        Assert.All(lines.Skip(1), l => Assert.Equal(LogLevel.Information, l.Level));
    }

    [Fact]
    public async Task A_failure_after_a_recovery_is_a_new_outage_with_a_new_warning()
    {
        var clock = new FakeTimeProvider();
        var (watcher, source, _) = Make(clock);
        source.Script(Down(), Down(), Nothing, Down(), Down(), Nothing);

        for (var i = 0; i < 6; i++)
        {
            await watcher.PollOnceAsync(TestContext.Current.CancellationToken);
            clock.Advance(TimeSpan.FromSeconds(30));
        }

        Assert.Equal([LogLevel.Warning, LogLevel.Information, LogLevel.Warning, LogLevel.Information], Told.Select(l => l.Level));
        Assert.Equal("Holyrics voltou a responder ap\U000000F3s 1 min, 2 tentativas", Told.Last().Message);
    }

    [Fact]
    public async Task A_different_reason_in_the_same_outage_is_told_too_but_not_over_and_over()
    {
        var clock = new FakeTimeProvider();
        var (watcher, source, _) = Make(clock);
        var denied = new CountdownReading(HolyricsOutcome.Unauthorized, Message: "token sem permiss\U000000E3o para GetCPInfo");
        source.Script(Down(), Down(), denied, Down(), denied);

        for (var i = 0; i < 5; i++)
        {
            await watcher.PollOnceAsync(TestContext.Current.CancellationToken);
            clock.Advance(TimeSpan.FromMinutes(6));
        }

        // refused (warning), refused again (silent), then each change comes 6 minutes after the last full line, over the 5-minute gap: 4 warnings
        Assert.Equal(4, Told.Count(l => l.Level == LogLevel.Warning));
    }

    [Fact]
    public async Task Two_reasons_flapping_every_twenty_seconds_do_not_flood_the_log()
    {
        var clock = new FakeTimeProvider();
        var (watcher, source, _) = Make(clock);
        var denied = new CountdownReading(HolyricsOutcome.Unauthorized, Message: "token sem permiss\U000000E3o para GetCPInfo");
        for (var i = 0; i < 30; i++)
        {
            source.Script(Down(), denied);
        }

        for (var i = 0; i < 60; i++)
        {
            await watcher.PollOnceAsync(TestContext.Current.CancellationToken);
            clock.Advance(TimeSpan.FromSeconds(20));
        }

        // 20 minutes of flapping: the first line, then at most one change line per 5 minutes
        Assert.InRange(Told.Count(l => l.Level == LogLevel.Warning), 1, 5);
    }

    [Fact]
    public async Task Switching_the_watch_off_ends_the_outage_without_a_word_and_a_later_answer_is_not_a_recovery()
    {
        var clock = new FakeTimeProvider();
        var (watcher, source, _) = Make(clock);
        source.Script(Down(), Down());
        await watcher.PollOnceAsync(TestContext.Current.CancellationToken);
        await watcher.PollOnceAsync(TestContext.Current.CancellationToken);
        _settings.Update(s => s with { Holyrics = s.Holyrics with { CountdownStopEnabled = false } });
        await watcher.PollOnceAsync(TestContext.Current.CancellationToken);
        _settings.Update(s => s with { Holyrics = s.Holyrics with { CountdownStopEnabled = true } });
        clock.Advance(TimeSpan.FromHours(3));

        await watcher.PollOnceAsync(TestContext.Current.CancellationToken); // Holyrics answers

        Assert.Single(Told); // only the first warning: no "voltou ap\U000000F3s 3 h"
    }

    [Fact]
    public async Task A_new_address_is_asked_at_once_at_the_normal_pace_and_its_outage_is_told_anew()
    {
        var clock = new FakeTimeProvider();
        var (watcher, source, _) = Make(clock);
        source.Fallback = Down();
        var last = TimeSpan.Zero;
        for (var i = 0; i < 6; i++)
        {
            last = await watcher.PollOnceAsync(TestContext.Current.CancellationToken);
        }

        Assert.Equal(TimeSpan.FromSeconds(60), last);

        _settings.Update(s => s with { Holyrics = s.Holyrics with { Host = "192.168.1.99" } });
        var next = await watcher.PollOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(TimeSpan.FromSeconds(2), next); // the backoff started over
        Assert.Equal(2, Told.Count(l => l.Level == LogLevel.Warning)); // one for the old address, one for the new
    }

    [Fact]
    public async Task A_new_address_cuts_the_wait_of_the_background_loop_short()
    {
        var clock = new FakeTimeProvider();
        var (watcher, source, _) = Make(clock);
        source.Fallback = Down();
        using (watcher)
        {
            watcher.Start();
            await WaitFor(() => source.Reads == 1);
            await Settle();
            clock.Advance(TimeSpan.FromSeconds(2));
            await WaitFor(() => source.Reads == 2);
            await Settle();
            clock.Advance(TimeSpan.FromSeconds(5));
            await WaitFor(() => source.Reads == 3); // now it waits 15 s
            await Settle();

            _settings.Update(s => s with { Holyrics = s.Holyrics with { Port = s.Holyrics.Port + 1 } });

            await WaitFor(() => source.Reads == 4); // no clock movement: the change woke it
        }
    }

    [Fact]
    public async Task A_presentation_that_fails_while_the_panel_answers_is_told_once_and_the_return_is_told_once()
    {
        _server.Replies["GetCPInfo"] = HolyricsReply.WithData(Cp(false, "0"));
        _server.Replies["GetCurrentPresentation"] = HolyricsReply.Error("boom");
        var clock = new FakeTimeProvider();
        using var client = new HolyricsClient(_settings, _protector, _log, clock, minInterval: TimeSpan.Zero);

        for (var i = 0; i < 30; i++)
        {
            var reading = await client.ReadCountdownAsync(TestContext.Current.CancellationToken);
            Assert.True(reading.Ok); // the panel still works
            clock.Advance(TimeSpan.FromSeconds(1));
        }

        var warning = Assert.Single(Told);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains("cron\U000000F4metro (apresenta\U000000E7\U000000E3o) falhou", warning.Message, StringComparison.Ordinal);

        _server.Replies["GetCurrentPresentation"] = HolyricsReply.WithData("null");
        await client.ReadCountdownAsync(TestContext.Current.CancellationToken);
        await client.ReadCountdownAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, Told.Count());
        Assert.Equal("Holyrics (apresenta\U000000E7\U000000E3o) voltou a ler a apresenta\U000000E7\U000000E3o ap\U000000F3s 30 s, 30 tentativas", Told.Last().Message);
    }

    // Lets the background loop get from "a look has begun" to "asleep on the fake clock" before the test moves the clock.
    private static Task Settle() => Task.Delay(60, TestContext.Current.CancellationToken);

    private static async Task WaitFor(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < until)
        {
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        Assert.True(condition(), "the condition was not met in 5 s");
    }

    // ---- the client against the fake API server ----------------------------------------------------------------------------------------------------

    private static string Cp(bool show, string time, bool stopwatch = false, string stopwatchTime = "0") =>
        "{\"text\":\"\",\"show\":false,\"countdown_show\":" + (show ? "true" : "false") + ",\"countdown_time\":" + time + ",\"stopwatch_show\":" + (stopwatch ? "true" : "false") + ",\"stopwatch_time\":" + stopwatchTime + "}";

    private static string Presentation(string type, string currentTime) =>
        "{\"id\":\"zMgfmzCNWmbG\",\"type\":\"" + type + "\",\"name\":\"0min 10s - Contagem regressiva\",\"time\":\"00:10\",\"current_time\":\"" + currentTime + "\",\"slide_number\":1}";

    [Fact]
    public async Task The_client_reads_the_panel_countdown_in_whole_seconds_as_the_real_holyrics_sends_it()
    {
        _server.Replies["GetCPInfo"] = HolyricsReply.WithData(Cp(true, "7"));
        _server.Replies["GetCurrentPresentation"] = HolyricsReply.WithData("null");

        var reading = await Client.ReadCountdownAsync(TestContext.Current.CancellationToken);

        Assert.True(reading.Shown);
        Assert.Equal(CountdownKind.Panel, reading.Kind);
        Assert.Equal(7d, reading.SecondsLeft);
        Assert.Equal(["GetCPInfo", "GetCurrentPresentation"], _server.Actions);
        Assert.All(_server.Requests, r => Assert.Equal("{}", r.Body));
    }

    [Fact]
    public async Task The_panel_countdown_keeps_going_below_zero_and_a_hidden_one_reads_as_none()
    {
        _server.Replies["GetCPInfo"] = HolyricsReply.WithData(Cp(true, "-2"));
        var below = await Client.ReadCountdownAsync(TestContext.Current.CancellationToken);
        _server.Replies["GetCPInfo"] = HolyricsReply.WithData(Cp(false, "0"));
        var hidden = await Client.ReadCountdownAsync(TestContext.Current.CancellationToken);

        Assert.Equal(-2d, below.SecondsLeft);
        Assert.True(hidden.Ok);
        Assert.False(hidden.Shown);
        Assert.Equal(CountdownKind.None, hidden.Kind);
    }

    [Fact]
    public async Task The_stopwatch_that_counts_up_is_ignored()
    {
        _server.Replies["GetCPInfo"] = HolyricsReply.WithData(Cp(false, "0", stopwatch: true, stopwatchTime: "2"));

        var reading = await Client.ReadCountdownAsync(TestContext.Current.CancellationToken);

        Assert.False(reading.Shown);
    }

    [Theory]
    [InlineData("0:09", 9)]
    [InlineData("0:02", 2)]
    [InlineData("12:30", 750)]
    [InlineData("1:00:05", 3605)]
    public async Task A_countdown_presentation_is_read_from_its_current_time(string currentTime, double expected)
    {
        _server.Replies["GetCPInfo"] = HolyricsReply.WithData(Cp(false, "0"));
        _server.Replies["GetCurrentPresentation"] = HolyricsReply.WithData(Presentation("countdown", currentTime));

        var reading = await Client.ReadCountdownAsync(TestContext.Current.CancellationToken);

        Assert.Equal(CountdownKind.Presentation, reading.Kind);
        Assert.Equal(expected, reading.SecondsLeft);
    }

    [Fact]
    public async Task A_presentation_that_is_not_a_countdown_or_has_no_time_is_ignored()
    {
        _server.Replies["GetCPInfo"] = HolyricsReply.WithData(Cp(false, "0"));
        _server.Replies["GetCurrentPresentation"] = HolyricsReply.WithData(Presentation("song", "0:02"));
        Assert.False((await Client.ReadCountdownAsync(TestContext.Current.CancellationToken)).Shown);

        _server.Replies["GetCurrentPresentation"] = HolyricsReply.WithData(Presentation("countdown", "soon"));
        Assert.False((await Client.ReadCountdownAsync(TestContext.Current.CancellationToken)).Shown);

        _server.Replies["GetCurrentPresentation"] = HolyricsReply.Error("No presentation available");
        Assert.True((await Client.ReadCountdownAsync(TestContext.Current.CancellationToken)).Ok);
    }

    [Fact]
    public async Task With_two_countdowns_on_display_the_smaller_time_wins()
    {
        _server.Replies["GetCPInfo"] = HolyricsReply.WithData(Cp(true, "30"));
        _server.Replies["GetCurrentPresentation"] = HolyricsReply.WithData(Presentation("countdown", "0:03"));

        var reading = await Client.ReadCountdownAsync(TestContext.Current.CancellationToken);

        Assert.Equal(CountdownKind.Presentation, reading.Kind);
        Assert.Equal(3d, reading.SecondsLeft);
    }

    [Fact]
    public async Task A_scripted_ten_second_countdown_through_the_real_client_stops_the_music_once()
    {
        var tick = 10;
        _server.Live["GetCPInfo"] = () => HolyricsReply.WithData(Cp(true, tick.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        var player = new FakePlayer();
        var watcher = new CountdownWatcher(_settings, Client, player, _log);

        for (; tick >= 0; tick--)
        {
            await watcher.PollOnceAsync(TestContext.Current.CancellationToken);
            await watcher.PollOnceAsync(TestContext.Current.CancellationToken);
        }

        Assert.Equal(1, player.Pauses);
    }

    [Fact]
    public async Task A_token_that_may_not_read_the_panel_is_told_with_the_action_name()
    {
        _server.Replies["GetCPInfo"] = HolyricsReply.Unauthorized;

        var reading = await Client.ReadCountdownAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HolyricsOutcome.Unauthorized, reading.Outcome);
        Assert.Contains("GetCPInfo", reading.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_token_that_may_not_read_the_presentation_is_not_asked_again()
    {
        _server.Replies["GetCPInfo"] = HolyricsReply.WithData(Cp(true, "5"));
        _server.Replies["GetCurrentPresentation"] = HolyricsReply.Unauthorized;

        var first = await Client.ReadCountdownAsync(TestContext.Current.CancellationToken);
        await Client.ReadCountdownAsync(TestContext.Current.CancellationToken);
        await Client.ReadCountdownAsync(TestContext.Current.CancellationToken);

        Assert.Equal(5d, first.SecondsLeft); // the panel countdown still works
        Assert.Single(_server.Actions, a => a == "GetCurrentPresentation");
    }

    [Fact]
    public async Task A_holyrics_that_is_off_is_reported_unreachable_and_the_log_does_not_fill_up()
    {
        var dead = _server.Port;
        _server.Dispose();
        _settings.Update(s => s with { Holyrics = s.Holyrics with { Port = dead } });
        var tokenBefore = FakeHolyricsServer.GoodToken;
        using var quiet = new HolyricsClient(_settings, _protector, _log, new FakeTimeProvider(), minInterval: TimeSpan.Zero);

        for (var i = 0; i < 3; i++)
        {
            var reading = await quiet.ReadCountdownAsync(TestContext.Current.CancellationToken);
            Assert.Equal(HolyricsOutcome.Unreachable, reading.Outcome);
            Assert.Contains("127.0.0.1:" + dead, reading.Detail, StringComparison.Ordinal); // the facts travel with the reading, to the watcher that logs them once
            Assert.DoesNotContain(tokenBefore, reading.Detail, StringComparison.Ordinal);
        }

        Assert.DoesNotContain(_log.Entries, e => e.Level >= LogLevel.Warning); // the client leaves the telling of a poll outage to the watcher
        Assert.All(_log.Entries, e => Assert.DoesNotContain(tokenBefore, e.Message, StringComparison.Ordinal));
        Assert.Equal(HolyricsLink.Unknown, quiet.Status.Link); // a poll never repaints the connection chip
    }

    [Fact]
    public async Task The_token_never_reaches_the_log_or_the_reading_even_when_holyrics_echoes_it()
    {
        _server.Replies["GetCPInfo"] = HolyricsReply.Error("bad token " + FakeHolyricsServer.GoodToken);

        var reading = await Client.ReadCountdownAsync(TestContext.Current.CancellationToken);

        Assert.False(reading.Ok);
        Assert.DoesNotContain(FakeHolyricsServer.GoodToken, reading.Message ?? string.Empty, StringComparison.Ordinal);
        Assert.All(_log.Entries, e => Assert.DoesNotContain(FakeHolyricsServer.GoodToken, e.Message, StringComparison.Ordinal));
    }

    [Fact]
    public void The_permission_check_asks_for_the_actions_the_watch_needs()
    {
        Assert.Contains("GetCPInfo", HolyricsClient.RequiredActions.Split(','));
        Assert.Contains("GetCurrentPresentation", HolyricsClient.RequiredActions.Split(','));
    }

    // ---- the settings ------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_switch_is_on_and_the_seconds_are_two_for_a_first_install_and_for_an_old_file()
    {
        Assert.True(new HolyricsSettings().CountdownStopEnabled);
        Assert.Equal(2, new HolyricsSettings().CountdownStopAt);

        var (old, corrupt) = SettingsJson.Load("""{"holyrics":{"host":"127.0.0.1","port":8091,"arrowsEnabled":false}}""");

        Assert.False(corrupt);
        Assert.True(old.Holyrics.CountdownStopEnabled);
        Assert.Equal(2, old.Holyrics.CountdownStopAt);
        Assert.False(old.Holyrics.ArrowsEnabled); // nothing that was there changed
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 5)]
    [InlineData(false, 10)]
    public void The_last_position_survives_a_save_and_a_load(bool enabled, int seconds)
    {
        var original = new AppSettings { Holyrics = new HolyricsSettings { CountdownStopEnabled = enabled, CountdownStopAt = seconds } };

        var (loaded, corrupt) = SettingsJson.Load(SettingsJson.Save(original));

        Assert.False(corrupt);
        Assert.Equal(enabled, loaded.Holyrics.CountdownStopEnabled);
        Assert.Equal(seconds, loaded.Holyrics.CountdownStopAt);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-4, 1)]
    [InlineData(11, 10)]
    [InlineData(99, 10)]
    public void The_seconds_are_held_between_one_and_ten(int typed, int kept)
    {
        var (loaded, _) = SettingsJson.Load("{\"holyrics\":{\"countdownStopAt\":" + typed + "}}");

        Assert.Equal(kept, loaded.Holyrics.CountdownStopAt);
    }
}
