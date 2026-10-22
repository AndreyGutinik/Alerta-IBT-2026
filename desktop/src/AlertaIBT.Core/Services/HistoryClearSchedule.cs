namespace AlertaIBT.Core.Services;

/// <summary>
/// The weekly clearing of the history: every Monday at 00:00 in the time zone it is given (the system one in the app). Only date
/// arithmetic, so the tests cover the day and time boundaries and the days the clock jumps.
/// </summary>
/// <remarks>
/// A Monday 00:00 that does not exist (the clock jumps over midnight) is the first moment after the jump; one that happens twice
/// (the clock goes back over midnight) is the first of the two.
/// </remarks>
public static class HistoryClearSchedule
{
    /// <summary>The latest Monday 00:00 (local) that is not after <paramref name="now"/>.</summary>
    /// <param name="now">The current moment.</param>
    /// <param name="zone">The time zone whose midnight counts.</param>
    /// <returns>That moment.</returns>
    public static DateTimeOffset MostRecentMonday(DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);

        var date = LocalMonday(now, zone);
        var candidate = MidnightOf(date, zone);
        while (candidate > now)
        {
            date = date.AddDays(-7);
            candidate = MidnightOf(date, zone);
        }

        return candidate;
    }

    /// <summary>The first Monday 00:00 (local) after <paramref name="now"/>.</summary>
    /// <param name="now">The current moment.</param>
    /// <param name="zone">The time zone whose midnight counts.</param>
    /// <returns>That moment.</returns>
    public static DateTimeOffset NextMonday(DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);

        var date = LocalMonday(now, zone).AddDays(7);
        var candidate = MidnightOf(date, zone);
        while (candidate <= now)
        {
            date = date.AddDays(7);
            candidate = MidnightOf(date, zone);
        }

        return candidate;
    }

    /// <summary>Whether a Monday 00:00 has passed since <paramref name="lastClear"/>, so the history is due.</summary>
    /// <param name="lastClear">When the history was last cleared; <c>null</c> (a first run) is never due: the caller starts the count.</param>
    /// <param name="now">The current moment.</param>
    /// <param name="zone">The time zone whose midnight counts.</param>
    /// <returns><c>true</c> when the last clear is older than the latest Monday 00:00.</returns>
    public static bool IsDue(DateTimeOffset? lastClear, DateTimeOffset now, TimeZoneInfo zone)
    {
        if (lastClear is not { } last)
        {
            return false;
        }

        return last < MostRecentMonday(now, zone);
    }

    // The Monday of the local week of "now", as a calendar date (time 00:00).
    private static DateTime LocalMonday(DateTimeOffset now, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(now, zone).DateTime.Date;
        var sinceMonday = ((int)local.DayOfWeek + 6) % 7;
        return DateTime.SpecifyKind(local.AddDays(-sinceMonday), DateTimeKind.Unspecified);
    }

    // The moment local 00:00 of that date happens.
    private static DateTimeOffset MidnightOf(DateTime date, TimeZoneInfo zone)
    {
        var local = date;
        while (zone.IsInvalidTime(local))
        {
            local = local.AddMinutes(30);
        }

        var offset = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local).Max() : zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }
}
