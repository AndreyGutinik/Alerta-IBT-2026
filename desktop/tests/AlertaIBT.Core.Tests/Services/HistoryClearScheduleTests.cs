using AlertaIBT.Core.Services;

namespace AlertaIBT.Core.Tests.Services;

// "Every Monday at 00:00, local time": the date arithmetic, with the clock jumps of daylight saving time.
public sealed class HistoryClearScheduleTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    private static readonly TimeZoneInfo Brasilia = TimeZoneInfo.CreateCustomTimeZone("t-brt", TimeSpan.FromHours(-3), "BRT", "BRT");

    // Monday 00:00 local, 2026-10-19 (the 19th of October 2026 is a Monday).
    private static readonly DateTimeOffset Monday = new(2026, 10, 19, 0, 0, 0, TimeSpan.Zero);

    // A zone that loses the hour 00:00-01:00 on Monday 2026-03-16 and repeats it on Monday 2026-10-19.
    private static readonly TimeZoneInfo MondayJumps = MakeMondayJumpsZone();

    private static TimeZoneInfo MakeMondayJumpsZone()
    {
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
            new DateTime(2026, 1, 1),
            new DateTime(2026, 12, 31),
            TimeSpan.FromHours(1),
            TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 0, 0, 0), 3, 16),
            TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 1, 0, 0), 10, 19));
        return TimeZoneInfo.CreateCustomTimeZone("t-jumps", TimeSpan.FromHours(-3), "jumps", "jumps-std", "jumps-dst", [rule]);
    }

    [Theory]
    [InlineData(2026, 10, 19, 0, 0, 0, 2026, 10, 19)] // Monday, exactly 00:00
    [InlineData(2026, 10, 19, 0, 0, 1, 2026, 10, 19)] // one second after
    [InlineData(2026, 10, 19, 23, 59, 59, 2026, 10, 19)] // Monday night
    [InlineData(2026, 10, 20, 12, 0, 0, 2026, 10, 19)] // Tuesday
    [InlineData(2026, 10, 21, 12, 0, 0, 2026, 10, 19)] // Wednesday
    [InlineData(2026, 10, 22, 12, 0, 0, 2026, 10, 19)] // Thursday
    [InlineData(2026, 10, 23, 12, 0, 0, 2026, 10, 19)] // Friday
    [InlineData(2026, 10, 24, 12, 0, 0, 2026, 10, 19)] // Saturday
    [InlineData(2026, 10, 25, 12, 0, 0, 2026, 10, 19)] // Sunday
    [InlineData(2026, 10, 25, 23, 59, 59, 2026, 10, 19)] // Sunday, one second before Monday
    [InlineData(2026, 10, 26, 0, 0, 0, 2026, 10, 26)] // the next Monday
    [InlineData(2026, 12, 31, 12, 0, 0, 2026, 12, 28)] // across the month
    [InlineData(2027, 1, 1, 12, 0, 0, 2026, 12, 28)] // across the year
    public void The_most_recent_monday_is_found_for_every_weekday_and_boundary(int y, int mo, int d, int h, int mi, int s, int ey, int emo, int ed)
    {
        var now = new DateTimeOffset(y, mo, d, h, mi, s, TimeSpan.Zero);

        Assert.Equal(new DateTimeOffset(ey, emo, ed, 0, 0, 0, TimeSpan.Zero), HistoryClearSchedule.MostRecentMonday(now, Utc));
    }

    [Fact]
    public void One_tick_before_monday_still_belongs_to_the_week_before()
    {
        Assert.Equal(Monday.AddDays(-7), HistoryClearSchedule.MostRecentMonday(Monday.AddTicks(-1), Utc));
        Assert.Equal(Monday, HistoryClearSchedule.MostRecentMonday(Monday, Utc));
    }

    [Theory]
    [InlineData(2026, 10, 19, 0, 0, 0, 2026, 10, 26)] // exactly Monday 00:00: the next one is a week away
    [InlineData(2026, 10, 18, 23, 59, 59, 2026, 10, 19)] // one second before
    [InlineData(2026, 10, 19, 0, 0, 1, 2026, 10, 26)]
    [InlineData(2026, 10, 22, 9, 30, 0, 2026, 10, 26)]
    [InlineData(2026, 12, 29, 9, 30, 0, 2027, 1, 4)] // across the year
    public void The_next_monday_is_the_first_one_strictly_after_now(int y, int mo, int d, int h, int mi, int s, int ey, int emo, int ed)
    {
        var now = new DateTimeOffset(y, mo, d, h, mi, s, TimeSpan.Zero);

        Assert.Equal(new DateTimeOffset(ey, emo, ed, 0, 0, 0, TimeSpan.Zero), HistoryClearSchedule.NextMonday(now, Utc));
    }

    [Fact]
    public void Midnight_is_the_local_one_not_the_utc_one()
    {
        // Sunday 2026-10-25 22:00 in Brasilia is Monday 01:00 UTC. Local Monday 00:00 is 03:00 UTC.
        var sundayNight = new DateTimeOffset(2026, 10, 26, 1, 0, 0, TimeSpan.Zero);

        Assert.Equal(new DateTimeOffset(2026, 10, 19, 3, 0, 0, TimeSpan.Zero), HistoryClearSchedule.MostRecentMonday(sundayNight, Brasilia));
        Assert.Equal(new DateTimeOffset(2026, 10, 26, 3, 0, 0, TimeSpan.Zero), HistoryClearSchedule.NextMonday(sundayNight, Brasilia));

        var afterMidnight = new DateTimeOffset(2026, 10, 26, 3, 0, 0, TimeSpan.Zero);
        Assert.Equal(afterMidnight, HistoryClearSchedule.MostRecentMonday(afterMidnight, Brasilia));
    }

    [Fact]
    public void The_week_that_crosses_daylight_saving_time_still_uses_local_midnight()
    {
        var zone = FindNewYork();

        // Clocks go back on Sunday 2026-11-01. Monday 2026-10-26 00:00 is EDT (04:00 UTC); Monday 2026-11-02 00:00 is EST (05:00 UTC).
        var saturday = new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);
        var tuesdayAfter = new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);

        Assert.Equal(new DateTimeOffset(2026, 10, 26, 4, 0, 0, TimeSpan.Zero), HistoryClearSchedule.MostRecentMonday(saturday, zone));
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 5, 0, 0, TimeSpan.Zero), HistoryClearSchedule.NextMonday(saturday, zone));
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 5, 0, 0, TimeSpan.Zero), HistoryClearSchedule.MostRecentMonday(tuesdayAfter, zone));

        // The same on the day clocks go forward (Sunday 2026-03-08): Monday 2026-03-09 00:00 is already EDT.
        var sundayEvening = new DateTimeOffset(2026, 3, 9, 3, 0, 0, TimeSpan.Zero);
        Assert.Equal(new DateTimeOffset(2026, 3, 9, 4, 0, 0, TimeSpan.Zero), HistoryClearSchedule.NextMonday(sundayEvening, zone));
    }

    [Fact]
    public void A_monday_midnight_that_does_not_exist_is_the_first_moment_after_the_jump()
    {
        // 2026-03-16: local clocks jump from 00:00 straight to 01:00 (UTC-3 becomes UTC-2). The first moment of that Monday is 03:00 UTC.
        var before = new DateTimeOffset(2026, 3, 15, 12, 0, 0, TimeSpan.Zero);

        var next = HistoryClearSchedule.NextMonday(before, MondayJumps);

        Assert.Equal(new DateTimeOffset(2026, 3, 16, 3, 0, 0, TimeSpan.Zero), next);
        Assert.Equal(next, HistoryClearSchedule.MostRecentMonday(next, MondayJumps));
        Assert.Equal(new DateTimeOffset(2026, 3, 9, 3, 0, 0, TimeSpan.Zero), HistoryClearSchedule.MostRecentMonday(next.AddTicks(-1), MondayJumps));
    }

    [Fact]
    public void A_monday_midnight_that_happens_twice_counts_the_first_time()
    {
        // 2026-10-19: at 01:00 (UTC-2) clocks go back to 00:00 (UTC-3). Local 00:00 happens first at 02:00 UTC.
        var sunday = new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

        var next = HistoryClearSchedule.NextMonday(sunday, MondayJumps);

        Assert.Equal(new DateTimeOffset(2026, 10, 19, 2, 0, 0, TimeSpan.Zero), next);

        // An hour later the clock reads 00:00 again; that is not another Monday.
        Assert.Equal(next, HistoryClearSchedule.MostRecentMonday(next.AddHours(1), MondayJumps));
        Assert.Equal(new DateTimeOffset(2026, 10, 26, 3, 0, 0, TimeSpan.Zero), HistoryClearSchedule.NextMonday(next.AddHours(1), MondayJumps));
    }

    [Fact]
    public void A_first_run_with_no_last_clear_is_never_due()
    {
        Assert.False(HistoryClearSchedule.IsDue(null, Monday.AddDays(40), Utc));
    }

    [Fact]
    public void The_history_is_due_once_a_monday_midnight_has_passed_since_the_last_clear()
    {
        // Cleared on Sunday evening: due as soon as Monday 00:00 arrives, not before.
        var sunday = Monday.AddHours(-3);
        Assert.False(HistoryClearSchedule.IsDue(sunday, Monday.AddTicks(-1), Utc));
        Assert.True(HistoryClearSchedule.IsDue(sunday, Monday, Utc));
        Assert.True(HistoryClearSchedule.IsDue(sunday, Monday.AddHours(9), Utc));
    }

    [Fact]
    public void A_clear_made_at_or_after_monday_midnight_is_not_due_until_the_next_monday()
    {
        Assert.False(HistoryClearSchedule.IsDue(Monday, Monday.AddHours(1), Utc));
        Assert.False(HistoryClearSchedule.IsDue(Monday.AddHours(10), Monday.AddDays(6), Utc));
        Assert.False(HistoryClearSchedule.IsDue(Monday.AddHours(10), Monday.AddDays(7).AddTicks(-1), Utc));
        Assert.True(HistoryClearSchedule.IsDue(Monday.AddHours(10), Monday.AddDays(7), Utc));
    }

    [Fact]
    public void A_pc_that_was_off_for_three_weeks_is_due_once_and_then_not_again()
    {
        var last = Monday.AddHours(10);
        var back = Monday.AddDays(21).AddHours(15); // Monday three weeks later, in the afternoon

        Assert.True(HistoryClearSchedule.IsDue(last, back, Utc));

        // After the clear (last = back) there is one owed week only: nothing more is due until the next Monday.
        Assert.False(HistoryClearSchedule.IsDue(back, back.AddMinutes(1), Utc));
        Assert.False(HistoryClearSchedule.IsDue(back, back.AddDays(5), Utc));
        Assert.True(HistoryClearSchedule.IsDue(back, back.AddDays(7), Utc));
    }

    [Fact]
    public void A_last_clear_in_the_future_is_not_due()
    {
        Assert.False(HistoryClearSchedule.IsDue(Monday.AddDays(30), Monday.AddDays(1), Utc));
    }

    [Fact]
    public void Dst_does_not_make_a_clear_due_twice_or_never()
    {
        var zone = FindNewYork();

        // Cleared Monday 2026-10-26 00:00 EDT; the next one is Monday 2026-11-02 00:00 EST = 05:00 UTC (a week plus one hour).
        var last = new DateTimeOffset(2026, 10, 26, 4, 0, 0, TimeSpan.Zero);
        Assert.False(HistoryClearSchedule.IsDue(last, new DateTimeOffset(2026, 11, 2, 4, 59, 0, TimeSpan.Zero), zone));
        Assert.True(HistoryClearSchedule.IsDue(last, new DateTimeOffset(2026, 11, 2, 5, 0, 0, TimeSpan.Zero), zone));
    }

    private static TimeZoneInfo FindNewYork()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        }
    }
}
