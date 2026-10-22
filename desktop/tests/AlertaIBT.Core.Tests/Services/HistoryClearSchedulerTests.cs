using AlertaIBT.Core.Services;
using AlertaIBT.Core.Settings;

namespace AlertaIBT.Core.Tests.Services;

// The Monday 00:00 clear: the timer, the catch-up for a PC that was off, the first run, and the switch.
public sealed class HistoryClearSchedulerTests
{
    // UTC-3. Monday 2026-10-19 00:00 local is 03:00 UTC.
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone("t-brt", TimeSpan.FromHours(-3), "BRT", "BRT");

    private static DateTimeOffset Local(int month, int day, int hour, int minute = 0, int second = 0) =>
        new DateTimeOffset(2026, month, day, hour, minute, second, TimeSpan.FromHours(-3)).ToUniversalTime();

    private static HistoryClearRig RigAt(DateTimeOffset now, DateTimeOffset? last, bool weekly = true)
    {
        var rig = new HistoryClearRig(siteConfigured: false);
        rig.Clock.SetUtcNow(now);
        rig.SiteIsOnline();
        rig.Update(s => s with { System = s.System with { LastHistoryClearAt = last, ClearHistoryWeekly = weekly } });
        rig.FillHistory(5);
        return rig;
    }

    private static HistoryClearScheduler SchedulerOf(HistoryClearRig rig) =>
        new(rig.Cleaner, () => rig.Settings, rig.Update, rig.Clock, Zone, rig.Log);

    [Fact]
    public async Task The_first_run_only_starts_the_count_and_clears_nothing()
    {
        var rig = RigAt(Local(10, 21, 12), last: null);
        using var scheduler = SchedulerOf(rig);

        await scheduler.CheckNow();

        Assert.Equal(rig.Clock.GetUtcNow(), rig.Settings.System.LastHistoryClearAt);
        Assert.Equal(5, rig.Queue.History.Count);
        Assert.Equal(0, rig.Telegram.Calls);
    }

    [Fact]
    public async Task A_pc_that_was_off_for_three_weeks_clears_once_at_the_next_look_and_then_not_again()
    {
        var last = Local(10, 5, 10); // a Monday, three weeks before
        var rig = RigAt(Local(10, 26, 15), last);
        using var scheduler = SchedulerOf(rig);

        await scheduler.CheckNow();

        Assert.Empty(rig.Queue.History);
        Assert.Equal(rig.Clock.GetUtcNow(), rig.Settings.System.LastHistoryClearAt);

        rig.FillHistory(2);
        await scheduler.CheckNow();
        Assert.Equal(2, rig.Queue.History.Count);
        Assert.Equal(1, rig.Telegram.Calls);
    }

    [Fact]
    public async Task A_clear_made_this_week_after_monday_is_not_owed()
    {
        var rig = RigAt(Local(10, 23, 18), Local(10, 20, 9)); // Friday; cleared on Tuesday
        using var scheduler = SchedulerOf(rig);

        await scheduler.CheckNow();

        Assert.Equal(5, rig.Queue.History.Count);
        Assert.Equal(Local(10, 20, 9), rig.Settings.System.LastHistoryClearAt);
    }

    [Fact]
    public async Task The_clear_is_owed_from_local_midnight_not_utc_midnight()
    {
        // Cleared Sunday 22:00 local. Monday 00:00 UTC is Sunday 21:00 local, so UTC midnight must not count.
        var rig = RigAt(Local(10, 25, 22, 30), Local(10, 25, 22));
        using var scheduler = SchedulerOf(rig);
        await scheduler.CheckNow();
        Assert.Equal(5, rig.Queue.History.Count);

        rig.Clock.SetUtcNow(Local(10, 26, 0, 0, 1));
        await scheduler.CheckNow();

        Assert.Empty(rig.Queue.History);
    }

    [Fact]
    public async Task With_the_switch_off_nothing_is_cleared_and_turning_it_on_later_does_not_wipe_the_old_week()
    {
        var rig = RigAt(Local(10, 26, 15), Local(10, 5, 10), weekly: false);
        using var scheduler = SchedulerOf(rig);

        await scheduler.CheckNow();

        Assert.Equal(5, rig.Queue.History.Count);
        Assert.Equal(rig.Clock.GetUtcNow(), rig.Settings.System.LastHistoryClearAt);

        rig.Update(s => s with { System = s.System with { ClearHistoryWeekly = true } });
        await scheduler.CheckNow();

        Assert.Equal(5, rig.Queue.History.Count);
        Assert.Equal(0, rig.Telegram.Calls);
    }

    [Fact]
    public async Task A_last_clear_in_the_future_after_the_clock_was_set_back_starts_the_count_again()
    {
        var rig = RigAt(Local(10, 21, 12), Local(11, 30, 12));
        using var scheduler = SchedulerOf(rig);

        await scheduler.CheckNow();

        Assert.Equal(rig.Clock.GetUtcNow(), rig.Settings.System.LastHistoryClearAt);
        Assert.Equal(5, rig.Queue.History.Count);
    }

    [Fact]
    public async Task The_timer_fires_at_monday_midnight_while_the_app_runs()
    {
        var rig = RigAt(Local(10, 25, 23, 59, 30), Local(10, 25, 20)); // Sunday, thirty seconds to midnight
        using var scheduler = SchedulerOf(rig);
        scheduler.Start();
        await Wait.Settle();
        Assert.Equal(5, rig.Queue.History.Count);

        rig.Clock.Advance(TimeSpan.FromSeconds(29));
        await Wait.Settle();
        Assert.Equal(5, rig.Queue.History.Count);

        rig.Clock.Advance(TimeSpan.FromSeconds(1)); // exactly Monday 00:00
        await Wait.Until(() => rig.Queue.History.Count == 0, "history cleared at midnight");
        Assert.Equal(1, rig.Telegram.Calls);
    }

    [Fact]
    public async Task Start_makes_up_for_a_missed_monday_at_once()
    {
        var rig = RigAt(Local(10, 21, 9), Local(10, 15, 20)); // Thursday before; Monday 19th passed while the PC was off
        using var scheduler = SchedulerOf(rig);

        scheduler.Start();

        await Wait.Until(() => rig.Queue.History.Count == 0, "caught up at start");
    }

    [Fact]
    public async Task A_pc_that_wakes_from_sleep_after_monday_is_noticed_within_the_recheck()
    {
        var rig = RigAt(Local(10, 21, 9), Local(10, 20, 9)); // Wednesday, cleared Tuesday
        using var scheduler = SchedulerOf(rig);
        scheduler.Start();
        await Wait.Settle();
        Assert.Equal(5, rig.Queue.History.Count);

        rig.Clock.Advance(TimeSpan.FromDays(5)); // slept until Monday 2026-10-26 09:00
        await Wait.Until(() => rig.Queue.History.Count == 0, "cleared after waking");
    }

    [Fact]
    public async Task A_second_look_while_one_is_running_does_not_clear_twice()
    {
        var rig = RigAt(Local(10, 26, 15), Local(10, 5, 10));
        rig.Telegram.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var scheduler = SchedulerOf(rig);

        var first = scheduler.CheckNow();
        await Wait.Until(() => rig.Telegram.Calls == 1, "first look reached Telegram");
        await scheduler.CheckNow();
        rig.Telegram.Hold.SetResult();
        await first;

        Assert.Equal(1, rig.Telegram.Calls);
    }

    [Fact]
    public async Task After_dispose_the_timer_does_nothing()
    {
        var rig = RigAt(Local(10, 25, 23, 59, 30), Local(10, 25, 20));
        var scheduler = SchedulerOf(rig);
        scheduler.Start();
        await Wait.Settle();
        scheduler.Dispose();

        rig.Clock.Advance(TimeSpan.FromMinutes(5));
        await Wait.Settle();

        Assert.Equal(5, rig.Queue.History.Count);
    }

    [Fact]
    public async Task Check_now_never_throws_even_when_the_clearing_does()
    {
        var rig = RigAt(Local(10, 26, 15), Local(10, 5, 10));
        rig.Telegram.Throw = new InvalidOperationException("boom");
        using var scheduler = SchedulerOf(rig);

        await scheduler.CheckNow();

        Assert.Empty(rig.Queue.History);
    }
}
