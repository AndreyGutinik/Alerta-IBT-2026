namespace AlertaIBT.Core.Firebase;

/// <summary>What clearing the history on the site came to.</summary>
/// <param name="Deleted">Finished requests deleted.</param>
/// <param name="Failed">Finished requests the database refused to delete, or that could not be reached; they stay on the site.</param>
public readonly record struct SiteClearResult(int Deleted, int Failed);

/// <summary>The part of a running site connection that "Limpar histórico" uses.</summary>
public interface ISiteHistory
{
    /// <summary>Where the connection stands; the history can only be cleared while it is <see cref="SiteState.Online"/>.</summary>
    SiteState State { get; }

    /// <summary>
    /// Deletes every finished request (shown, rejected, cancelled, expired) and empties the list of recent notices in <c>/live</c>.
    /// Requests that are pending, queued or showing are never touched.
    /// </summary>
    /// <param name="ct">Cancels the work.</param>
    /// <returns>How many requests were deleted and how many stayed.</returns>
    /// <exception cref="InvalidOperationException">The site is not online.</exception>
    /// <exception cref="RtdbException">The list of requests could not be read.</exception>
    Task<SiteClearResult> ClearHistoryAsync(CancellationToken ct);
}
