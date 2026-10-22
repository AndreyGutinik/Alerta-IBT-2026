using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AlertaIBT.Core.Firebase;

/// <summary>Where a <see cref="RtdbListener"/> stands.</summary>
public enum ListenerState
{
    /// <summary>Opening the first connection.</summary>
    Connecting,

    /// <summary>The server sent the data: events are arriving.</summary>
    Connected,

    /// <summary>The connection was lost; trying again.</summary>
    Reconnecting,

    /// <summary>The rules or the account refuse the reading (the PC's account is not in <c>/devices</c>, or was disabled).</summary>
    PermissionDenied,
}

/// <summary>What one event changed under the watched path.</summary>
/// <param name="Children">Each child that changed, with its new value (a copy; <c>null</c> when it was removed).</param>
/// <param name="Reset">Whether the whole watched value was replaced (the first data of a connection).</param>
public sealed record RtdbChange(IReadOnlyDictionary<string, JsonNode?> Children, bool Reset);

/// <summary>
/// Watches one path of the database through the streaming REST API and keeps a local copy of its value up to date: <c>put</c> and
/// <c>patch</c> events are applied at their paths, and <see cref="Changed"/> tells which children of the watched path changed.
/// Reconnects by itself with a wait that grows from 1 to 30 seconds, renews the token before it expires, reconnects at once on
/// <c>auth_revoked</c> (with a renewed token), and drops a connection that sent nothing (not even a keep-alive) for 60 seconds.
/// A reconnection sends the whole value again, so a missed event never leaves the copy wrong.
/// </summary>
public sealed class RtdbListener
{
    /// <summary>A connection that sent nothing for this long is dropped (the server sends a keep-alive about every 30 s).</summary>
    public static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(60);

    /// <summary>The waits between attempts after a failure: 1, 2, 4, 8, 16, then 30 seconds.</summary>
    public static readonly IReadOnlyList<TimeSpan> Backoff =
        [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(16), TimeSpan.FromSeconds(30)];

    private static readonly TimeSpan MinConnectionLife = TimeSpan.FromSeconds(30);

    private readonly RealtimeDbClient _db;
    private readonly string _path;
    private readonly TimeProvider _clock;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private JsonObject? _root;
    private ListenerState? _state;

    /// <param name="db">The database client.</param>
    /// <param name="path">What to watch (<c>requests</c>).</param>
    /// <param name="clock">Drives the waits and the watchdog.</param>
    /// <param name="logger">Receives what happened, without secrets.</param>
    public RtdbListener(RealtimeDbClient db, string path, TimeProvider? clock = null, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        _db = db;
        _path = path;
        _clock = clock ?? TimeProvider.System;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>Raised when the state changes, with a short detail for a problem; on the listener's own thread. A handler must not throw.</summary>
    public event Action<ListenerState, string?>? StateChanged;

    /// <summary>Raised after each event that changed the data, on the listener's own thread. A handler must not block for long and must not throw.</summary>
    public event Action<RtdbChange>? Changed;

    /// <summary>The current state, or <c>null</c> before <see cref="RunAsync"/> started.</summary>
    public ListenerState? State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    /// <summary>A copy of one child of the watched path as it is now, or <c>null</c> when there is none.</summary>
    /// <param name="key">The child key (a request id).</param>
    public JsonNode? GetChild(string key)
    {
        lock (_gate)
        {
            return _root is not null && _root.TryGetPropertyValue(key, out var value) ? value?.DeepClone() : null;
        }
    }

    /// <summary>A copy of the children of the watched path as they are now.</summary>
    public IReadOnlyDictionary<string, JsonNode?> Snapshot()
    {
        lock (_gate)
        {
            return _root is null
                ? new Dictionary<string, JsonNode?>()
                : _root.ToDictionary(p => p.Key, p => p.Value?.DeepClone(), StringComparer.Ordinal);
        }
    }

    /// <summary>Runs until <paramref name="ct"/> is cancelled. Never throws for a failure of the connection: it reports it and tries again.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        var attempt = 0;
        var forceRefresh = false;
        var first = true;
        var revokedStreak = 0;
        while (!ct.IsCancellationRequested)
        {
            Outcome outcome;
            try
            {
                outcome = await ConnectOnceAsync(forceRefresh, first, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }

            if (ct.IsCancellationRequested)
            {
                return;
            }

            first = false;
            forceRefresh = false;
            if (outcome.Healthy)
            {
                attempt = 0;
            }

            TimeSpan wait;
            switch (outcome.Kind)
            {
                case OutcomeKind.Renew:
                    revokedStreak = 0;
                    continue;

                case OutcomeKind.Revoked:
                    forceRefresh = true;
                    revokedStreak = outcome.Healthy ? 1 : revokedStreak + 1;
                    if (revokedStreak <= 1)
                    {
                        continue;
                    }

                    SetState(ListenerState.Reconnecting, outcome.Detail);
                    wait = Backoff[Math.Min(attempt++, Backoff.Count - 1)];
                    break;

                case OutcomeKind.Denied:
                    SetState(ListenerState.PermissionDenied, outcome.Detail);
                    wait = Backoff[^1];
                    break;

                default:
                    revokedStreak = 0;
                    SetState(ListenerState.Reconnecting, outcome.Detail);
                    wait = Backoff[Math.Min(attempt++, Backoff.Count - 1)];
                    break;
            }

            _logger.LogInformation("Banco de dados: nova tentativa em {Seconds:F0} s ({Detail})", wait.TotalSeconds, outcome.Detail ?? "sem detalhe");
            try
            {
                await Task.Delay(wait, _clock, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private enum OutcomeKind
    {
        Lost,
        Renew,
        Revoked,
        Denied,
    }

    private sealed record Outcome(OutcomeKind Kind, string? Detail, bool Healthy);

    private async Task<Outcome> ConnectOnceAsync(bool forceRefresh, bool first, CancellationToken ct)
    {
        if (first)
        {
            SetState(ListenerState.Connecting, null);
        }

        var life = _db.Tokens.TimeUntilRefresh;
        using var lifeCts = new CancellationTokenSource(life < MinConnectionLife ? MinConnectionLife : life, _clock);
        using var dogCts = new CancellationTokenSource(Watchdog, _clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lifeCts.Token, dogCts.Token);
        var healthy = false;
        try
        {
            using var response = await _db.OpenStreamAsync(_path, forceRefresh, linked.Token).ConfigureAwait(false);
            await using var body = await response.Content.ReadAsStreamAsync(linked.Token).ConfigureAwait(false);
            return await ReadEventsAsync(body, dogCts, linked.Token, h => healthy = h).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return dogCts.IsCancellationRequested
                ? new Outcome(OutcomeKind.Lost, "sem sinal do servidor por 60 s", healthy)
                : new Outcome(OutcomeKind.Renew, null, healthy);
        }
        catch (RtdbException ex)
        {
            return new Outcome(
                ex.Failure == RtdbFailure.PermissionDenied ? OutcomeKind.Denied : OutcomeKind.Lost,
                ex.Message,
                healthy);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or ObjectDisposedException)
        {
            return new Outcome(OutcomeKind.Lost, "conex\U000000E3o interrompida", healthy);
        }
    }

    private async Task<Outcome> ReadEventsAsync(Stream body, CancellationTokenSource dog, CancellationToken ct, Action<bool> setHealthy)
    {
        var parser = new SseParser();
        var decoder = Encoding.UTF8.GetDecoder();
        var bytes = new byte[8192];
        var chars = new char[8192];
        var healthy = false;
        while (true)
        {
            var read = await body.ReadAsync(bytes, ct).ConfigureAwait(false);
            if (read == 0)
            {
                return new Outcome(OutcomeKind.Lost, "o servidor encerrou a conex\U000000E3o", healthy);
            }

            dog.CancelAfter(Watchdog);
            var count = decoder.GetChars(bytes, 0, read, chars, 0);
            foreach (var evt in parser.Feed(chars.AsSpan(0, count)))
            {
                switch (evt.Name)
                {
                    case "put":
                    case "patch":
                        if (!TryApply(evt, out var change))
                        {
                            _logger.LogWarning("Banco de dados: evento {Event} malformado; reconectando para sincronizar", evt.Name);
                            return new Outcome(OutcomeKind.Lost, "evento malformado", healthy);
                        }

                        if (!healthy)
                        {
                            healthy = true;
                            setHealthy(true);
                            SetState(ListenerState.Connected, null);
                        }

                        RaiseChanged(change);
                        break;

                    case "auth_revoked":
                        return new Outcome(OutcomeKind.Revoked, "token revogado", healthy);

                    case "cancel":
                        // Before any data it is the rules refusing the reading; after data, most likely an expired token.
                        return new Outcome(healthy ? OutcomeKind.Revoked : OutcomeKind.Denied, "leitura cancelada pelo servidor", healthy);

                    default:
                        // keep-alive, and anything a newer server may add: the read itself already fed the watchdog.
                        break;
                }
            }
        }
    }

    private bool TryApply(SseEvent evt, out RtdbChange change)
    {
        change = new RtdbChange(new Dictionary<string, JsonNode?>(), false);
        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(evt.Data);
        }
        catch (JsonException)
        {
            return false;
        }

        if (parsed is not JsonObject payload || payload["path"] is not JsonValue pathValue || !pathValue.TryGetValue<string>(out var path))
        {
            return false;
        }

        var data = payload["data"];
        lock (_gate)
        {
            if (evt.Name == "put")
            {
                change = ApplyPut(path, data);
                return true;
            }

            if (data is null)
            {
                // A patch with nothing in it changes nothing.
                return true;
            }

            if (data is not JsonObject values)
            {
                return false;
            }

            var touched = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (key, value) in values)
            {
                touched.UnionWith(ApplyPut(path + "/" + key, value).Children.Keys);
            }

            change = Describe(touched, reset: false);
            return true;
        }
    }

    // Caller holds the lock.
    private RtdbChange ApplyPut(string path, JsonNode? data)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            var before = _root?.Select(p => p.Key).ToList() ?? [];
            _root = data is JsonObject root && root.Count > 0 ? (JsonObject)root.DeepClone() : null;
            var touched = new HashSet<string>(before, StringComparer.Ordinal);
            if (_root is not null)
            {
                touched.UnionWith(_root.Select(p => p.Key));
            }

            return Describe(touched, reset: true);
        }

        // Walk down, making the objects that are missing; a value that is not an object on the way is replaced.
        var trail = new List<JsonObject>();
        _root ??= [];
        var node = _root;
        trail.Add(node);
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (node[segments[i]] is not JsonObject next)
            {
                if (data is null)
                {
                    // Removing something under a place that is not there.
                    return Describe([segments[0]], reset: false);
                }

                next = [];
                node[segments[i]] = next;
            }

            node = next;
            trail.Add(node);
        }

        var last = segments[^1];
        if (data is null)
        {
            node.Remove(last);
        }
        else
        {
            node[last] = data.DeepClone();
        }

        // The database keeps no empty objects: a parent left empty goes away.
        for (var i = trail.Count - 1; i > 0 && trail[i].Count == 0; i--)
        {
            trail[i - 1].Remove(segments[i - 1]);
        }

        if (_root.Count == 0)
        {
            _root = null;
        }

        return Describe([segments[0]], reset: false);
    }

    // Caller holds the lock.
    private RtdbChange Describe(IEnumerable<string> keys, bool reset)
    {
        var children = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            children[key] = _root is not null && _root.TryGetPropertyValue(key, out var value) ? value?.DeepClone() : null;
        }

        return new RtdbChange(children, reset);
    }

    private void RaiseChanged(RtdbChange change)
    {
        try
        {
            Changed?.Invoke(change);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro ao tratar uma mudan\U000000E7a do banco de dados");
        }
    }

    private void SetState(ListenerState state, string? detail)
    {
        lock (_gate)
        {
            if (_state == state && detail is null)
            {
                return;
            }

            _state = state;
        }

        try
        {
            StateChanged?.Invoke(state, detail);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro ao tratar o estado do banco de dados");
        }
    }
}
