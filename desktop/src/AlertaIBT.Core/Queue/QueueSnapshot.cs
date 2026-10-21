namespace AlertaIBT.Core.Queue;

/// <summary>What survives a restart: the alerts still to show and the history.</summary>
/// <param name="Pending">The alert on screen (if any) first, then the pending ones, in order.</param>
/// <param name="History">The history, newest first.</param>
/// <param name="RecentShown">
/// The plates shown in the last 10 minutes and when (see <see cref="AlertQueue"/>); <c>null</c> in a file written before
/// this existed, and then the shown entries of <paramref name="History"/> stand in for it.
/// </param>
public sealed record QueueSnapshot(
    IReadOnlyList<QueueItem> Pending,
    IReadOnlyList<HistoryEntry> History,
    IReadOnlyDictionary<string, DateTimeOffset>? RecentShown = null);
