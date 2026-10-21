namespace AlertaIBT.Core.Queue;

/// <summary>What <see cref="AlertQueue.Enqueue"/> did with an alert.</summary>
public enum EnqueueKind
{
    /// <summary>The alert is in the queue.</summary>
    Queued,

    /// <summary>The alert was refused because the same plate is already queued, on screen or was just shown.</summary>
    DuplicateBlocked,

    /// <summary>The alert was refused because the queue is paused (the sermon).</summary>
    PausedRefused,
}

/// <summary>The outcome of <see cref="AlertQueue.Enqueue"/>.</summary>
/// <param name="Kind">What happened.</param>
/// <param name="Position">1-based place in the pending list after insertion (1 = next to show); 0 when not queued.</param>
/// <param name="DuplicateMinutesAgo">
/// Whole minutes since the same plate was shown, when that made it a duplicate; <c>null</c> when the duplicate is
/// pending or on screen, or when there is no duplicate.
/// </param>
/// <param name="WasDuplicate">Whether the plate was a duplicate (also true for a duplicate that was queued anyway).</param>
public sealed record EnqueueResult(EnqueueKind Kind, int Position, int? DuplicateMinutesAgo, bool WasDuplicate);

/// <summary>The answer of <see cref="AlertQueue.CheckDuplicate"/>: whether a plate would count as a duplicate right now.</summary>
/// <param name="IsDuplicate">Whether the same plate is on screen, pending, or was shown in the last 10 minutes.</param>
/// <param name="MinutesAgo">Whole minutes since it was shown, when that is what makes it a duplicate; otherwise <c>null</c>.</param>
public sealed record DuplicateCheck(bool IsDuplicate, int? MinutesAgo);

/// <summary>What to do with an alert whose plate is a duplicate.</summary>
public enum DuplicatePolicy
{
    /// <summary>Refuse it.</summary>
    Block,

    /// <summary>Queue it anyway and report that it was a duplicate.</summary>
    Warn,
}
