namespace AlertaIBT.Core.Queue;

/// <summary>How an alert ended.</summary>
public enum HistoryState
{
    /// <summary>It ran on the screen.</summary>
    Shown,

    /// <summary>It was refused and never queued.</summary>
    Rejected,

    /// <summary>It was removed before it ran.</summary>
    Cancelled,

    /// <summary>It waited too long and was dropped.</summary>
    Expired,
}

/// <summary>One line of the history: an alert, how it ended, when and why.</summary>
/// <param name="Item">The alert.</param>
/// <param name="State">How it ended.</param>
/// <param name="At">When it ended.</param>
/// <param name="Reason">The user-visible reason (see <see cref="HistoryReasons"/>), or <c>null</c> when there is none.</param>
public sealed record HistoryEntry(QueueItem Item, HistoryState State, DateTimeOffset At, string? Reason);

/// <summary>The reasons the history shows to the user.</summary>
public static class HistoryReasons
{
    /// <summary>The sermon started, so the queue was cancelled or refuses alerts.</summary>
    public const string Sermon = "prega\U000000E7\U000000E3o";

    /// <summary>The same plate was already queued, on screen or shown a moment ago.</summary>
    public const string Duplicate = "repetido";

    /// <summary>The operator removed it at the PC.</summary>
    public const string RemovedOnPc = "removido no PC";

    /// <summary>The sender cancelled it on the site or the app.</summary>
    public const string CancelledOnSite = "cancelado no site";

    /// <summary>The operator cleared the whole queue.</summary>
    public const string QueueCleared = "fila limpa";

    /// <summary>The correction refused the text as improper.</summary>
    public const string ImproperContent = "conte\U000000FAdo impr\U000000F3prio";

    /// <summary>A member's alert was longer than the limit.</summary>
    public const string TooLong = "texto grande demais";

    /// <summary>The alert lacks required fields: <c>faltou modelo, cor e placa</c>.</summary>
    /// <param name="fields">What it lacks, as <c>"modelo"</c>, <c>"cor"</c> and <c>"placa"</c>.</param>
    public static string MissingFields(IReadOnlyList<string> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);

        var list = fields.Count switch
        {
            0 => "campos",
            1 => fields[0],
            _ => string.Join(", ", fields.Take(fields.Count - 1)) + " e " + fields[^1],
        };
        return "faltou " + list;
    }
}
