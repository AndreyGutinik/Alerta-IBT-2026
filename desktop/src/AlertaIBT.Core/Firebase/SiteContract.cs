using System.Text.Json.Nodes;
using AlertaIBT.Core.Queue;

namespace AlertaIBT.Core.Firebase;

/// <summary>
/// The words the PC and the site agree on: the <c>state</c> values of a request and the short reasons the site shows next to them
/// (spec 5.6, site column). The site shows <c>reason</c> as it is, so each one is a short pt-BR phrase.
/// </summary>
public static class SiteContract
{
    /// <summary>The request is in the queue (<c>position</c> says where).</summary>
    public const string Queued = "queued";

    /// <summary>The request is passing on the LED now.</summary>
    public const string Showing = "showing";

    /// <summary>The request passed on the LED.</summary>
    public const string Shown = "shown";

    /// <summary>The request was refused (<c>reason</c> says why).</summary>
    public const string Rejected = "rejected";

    /// <summary>The request was cancelled before it passed.</summary>
    public const string Cancelled = "cancelled";

    /// <summary>The request waited too long, or was lost.</summary>
    public const string Expired = "expired";

    /// <summary>The sender is blocked, or the account has no record any more.</summary>
    public const string AccessRemoved = "acesso removido";

    /// <summary>The sender is over 10 notices in the last hour.</summary>
    public const string RateLimited = "limite de 10 por hora";

    /// <summary>The same plate is already queued, on screen, or was shown a moment ago.</summary>
    public const string Duplicate = HistoryReasons.Duplicate;

    /// <summary>Something went wrong at the PC while handling the request.</summary>
    public const string PcError = "erro no PC, tente de novo";

    /// <summary>
    /// The e-mails of the people who are always administrators and never blocked or limited, whatever <c>/users/{uid}</c> says: the
    /// owner and maintainer of the project. This is the ONLY place the address is written; the database rules protect it too, and
    /// this is the second line (defense in depth). Compared without case and without surrounding spaces, see <see cref="IsProtectedAdmin"/>.
    /// </summary>
    public static readonly IReadOnlyList<string> ProtectedAdmins = ["gutnikandrey01@protonmail.com"];

    /// <summary>Whether <paramref name="email"/> (from the <c>/users</c> record) is one of <see cref="ProtectedAdmins"/>.</summary>
    public static bool IsProtectedAdmin(string? email)
    {
        var wanted = email?.Trim();
        return !string.IsNullOrEmpty(wanted)
            && ProtectedAdmins.Any(admin => string.Equals(admin, wanted, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The state name of a history entry.</summary>
    public static string StateOf(HistoryState state) => state switch
    {
        HistoryState.Shown => Shown,
        HistoryState.Rejected => Rejected,
        HistoryState.Cancelled => Cancelled,
        _ => Expired,
    };

    /// <summary>The reason the site shows for a history entry (none for a notice that the sender cancelled herself).</summary>
    public static string? ReasonOf(HistoryEntry entry) =>
        entry.State == HistoryState.Cancelled && entry.Reason == HistoryReasons.CancelledOnSite ? null : entry.Reason;
}

/// <summary>What the PC writes to <c>/requests/{id}/status</c> (the rules allow exactly these five fields and need <c>state</c> and <c>at</c>).</summary>
/// <param name="State">One of the <see cref="SiteContract"/> states.</param>
/// <param name="Position">The place in the queue (1 = next), when queued.</param>
/// <param name="Reason">A short pt-BR phrase, at most 200 letters.</param>
/// <param name="Text">The final text of the notice, at most 400 letters.</param>
/// <param name="At">When it happened (ms since 1970); the current time when <c>null</c>.</param>
public sealed record StatusValue(string State, int? Position = null, string? Reason = null, string? Text = null, long? At = null)
{
    /// <summary>Identifies what the site would show, so an unchanged status is not written twice.</summary>
    public string Key => $"{State}|{Position}|{Reason}|{Text}";

    /// <summary>The JSON object of the status.</summary>
    /// <param name="nowMs">The time to write when <see cref="At"/> is not set.</param>
    public JsonObject ToJson(long nowMs)
    {
        var json = new JsonObject { ["state"] = State, ["at"] = At ?? nowMs };
        if (Position is { } position && position >= 1)
        {
            json["position"] = position;
        }

        if (!string.IsNullOrEmpty(Reason))
        {
            json["reason"] = Clip(Reason, 200);
        }

        if (!string.IsNullOrEmpty(Text))
        {
            json["text"] = Clip(Text, 400);
        }

        return json;
    }

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..max];
}

/// <summary>A request as the site wrote it, read defensively: the site is not trusted (any Google account can write one).</summary>
/// <param name="Id">The request id.</param>
/// <param name="Uid">Who sent it.</param>
/// <param name="Raw">What the person typed.</param>
/// <param name="Urgent">Whether it asks to go first.</param>
/// <param name="CreatedAt">When it was sent (ms since 1970, server time); 0 when unknown.</param>
/// <param name="Cancel">Whether a cancellation was asked.</param>
/// <param name="State">The state in <c>status</c>, or <c>pending</c> when the PC has not answered yet.</param>
public sealed record SiteRequest(string Id, string Uid, string Raw, bool Urgent, long CreatedAt, bool Cancel, string State)
{
    /// <summary>Reads a request; <c>false</c> when it lacks the sender or the text, or they are not text (the request is then ignored).</summary>
    public static bool TryParse(string id, JsonNode? node, out SiteRequest request)
    {
        request = null!;
        if (node is not JsonObject obj || ReadString(obj, "uid") is not { Length: > 0 } uid || ReadString(obj, "raw") is not { } raw
            || string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var urgent = obj["urgent"] is JsonValue u && u.TryGetValue<bool>(out var urgentValue) && urgentValue;
        var cancel = obj["cancel"] is JsonValue c && c.TryGetValue<bool>(out var cancelValue) && cancelValue;
        long created = 0;
        if (ReadNumber(obj["createdAt"]) is { } ms && double.IsFinite(ms) && ms > 0)
        {
            created = (long)ms;
        }

        var state = obj["status"] is JsonObject status && ReadString(status, "state") is { Length: > 0 } s ? s : "pending";
        request = new SiteRequest(id, uid, raw, urgent, created, cancel, state);
        return true;
    }

    // A number as the database sends it (parsed JSON) or as code builds it (a long, an int).
    private static double? ReadNumber(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue<double>(out var real))
        {
            return real;
        }

        return value.TryGetValue<long>(out var whole) ? whole : null;
    }

    private static string? ReadString(JsonObject obj, string key) =>
        obj[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}

/// <summary>
/// Keeps the "10 notices per hour per person" rule of the PC (the database rules only hold the 20 s spacing). In memory; after a
/// restart it is rebuilt from the requests that still exist (they are kept for 24 hours).
/// </summary>
public sealed class PerPersonLimiter(int limit, TimeSpan window)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Queue<DateTimeOffset>> _hits = new(StringComparer.Ordinal);

    /// <summary>Counts one notice of <paramref name="key"/> if it is under the limit.</summary>
    /// <returns><c>true</c> when it may go on; <c>false</c> when the person already sent <c>limit</c> in the last <c>window</c>.</returns>
    public bool TryAcquire(string key, DateTimeOffset now)
    {
        lock (_gate)
        {
            var queue = Prune(key, now);
            if (queue.Count >= limit)
            {
                return false;
            }

            queue.Enqueue(now);
            return true;
        }
    }

    /// <summary>Counts a notice that was handled before (earlier session) without asking.</summary>
    public void Seed(string key, DateTimeOffset at, DateTimeOffset now)
    {
        if (now - at > window)
        {
            return;
        }

        lock (_gate)
        {
            var queue = Prune(key, now);

            // Kept in time order: a seed older than the newest hit goes first.
            var all = queue.Append(at).OrderBy(x => x).ToList();
            queue.Clear();
            foreach (var hit in all)
            {
                queue.Enqueue(hit);
            }
        }
    }

    private Queue<DateTimeOffset> Prune(string key, DateTimeOffset now)
    {
        if (!_hits.TryGetValue(key, out var queue))
        {
            queue = new Queue<DateTimeOffset>();
            _hits[key] = queue;
        }

        while (queue.Count > 0 && now - queue.Peek() >= window)
        {
            queue.Dequeue();
        }

        return queue;
    }
}
