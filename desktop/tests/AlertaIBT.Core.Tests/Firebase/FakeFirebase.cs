using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.Extensions.Time.Testing;

namespace AlertaIBT.Core.Tests.Firebase;

/// <summary>A readable stream fed by the test: what the test pushes is what the client reads; closing it ends the stream.</summary>
internal sealed class FeedStream : Stream
{
    private readonly Channel<byte[]> _chunks = Channel.CreateUnbounded<byte[]>();
    private byte[] _rest = [];
    private int _restAt;

    public void Push(byte[] bytes) => _chunks.Writer.TryWrite(bytes);

    public void Push(string text) => Push(Encoding.UTF8.GetBytes(text));

    public void End() => _chunks.Writer.TryComplete();

    public void Fail() => _chunks.Writer.TryComplete(new IOException("connection reset"));

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (_restAt >= _rest.Length)
        {
            try
            {
                if (!await _chunks.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    return 0;
                }
            }
            catch (ChannelClosedException ex)
            {
                throw ex.InnerException ?? ex;
            }

            if (_chunks.Reader.TryRead(out var next))
            {
                _rest = next;
                _restAt = 0;
            }
        }

        var count = Math.Min(buffer.Length, _rest.Length - _restAt);
        _rest.AsMemory(_restAt, count).CopyTo(buffer);
        _restAt += count;
        return count;
    }

    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

internal sealed record FakeCall(string Method, string Path, string? Query, JsonNode? Body, DateTimeOffset At);

internal sealed class FakeStreamConnection(string path, string token, DateTimeOffset openedAt)
{
    public string Path { get; } = path;

    public string Token { get; } = token;

    public DateTimeOffset OpenedAt { get; } = openedAt;

    public FeedStream Body { get; } = new();

    public void Raw(string text) => Body.Push(text);

    public void Event(string name, string data) => Body.Push($"event: {name}\ndata: {data}\n\n");

    public void KeepAlive() => Event("keep-alive", "null");

    public void Put(string path, JsonNode? data) =>
        Event("put", new JsonObject { ["path"] = path, ["data"] = data?.DeepClone() }.ToJsonString());

    public void Patch(string path, JsonObject data) =>
        Event("patch", new JsonObject { ["path"] = path, ["data"] = data.DeepClone() }.ToJsonString());

    public void Close() => Body.End();
}

/// <summary>
/// The Firebase REST services in memory, as an HTTP handler: Identity Toolkit and Secure Token (sign in, refresh), and the Realtime
/// Database (get, put, patch, delete, and the event stream), with the failures a test asks for. Writes through it reach the open
/// streams the way the real database does.
/// </summary>
internal sealed class FakeFirebase(FakeTimeProvider clock) : HttpMessageHandler
{
    public const string Email = "pc@alertaibt.test";
    public const string Password = "pc-password-9f3k";
    public const string ApiKey = "AIza-test-api-key";
    public const string PcUid = "pc-uid";

    private readonly object _gate = new();
    private readonly JsonObject _db = [];
    private readonly Dictionary<string, string> _tokenUid = new(StringComparer.Ordinal);
    private readonly HashSet<string> _revoked = new(StringComparer.Ordinal);
    private readonly List<FakeCall> _calls = [];
    private readonly List<FakeStreamConnection> _streams = [];
    private int _tokens;
    private int _signIns;
    private int _refreshes;

    /// <summary>When set, every call fails like a dead network.</summary>
    public bool NetworkDown { get; set; }

    /// <summary>When set, the next sign-in answers with this HTTP status and Google error code.</summary>
    public (HttpStatusCode Status, string Code)? SignInFailure { get; set; }

    /// <summary>When set, token refreshes answer with this HTTP status and Google error code.</summary>
    public (HttpStatusCode Status, string Code)? RefreshFailure { get; set; }

    /// <summary>Decides the HTTP status of a database call (null = go on): method, path.</summary>
    public Func<string, string, HttpStatusCode?>? DbRule { get; set; }

    /// <summary>The next stream opened answers with this redirect instead (once).</summary>
    public string? RedirectNextStreamTo { get; set; }

    /// <summary>How many seconds the tokens issued from now on live.</summary>
    public int TokenSeconds { get; set; } = 3600;

    /// <summary>When true, the first event of a stream is not sent (to test a silent connection).</summary>
    public bool SilentStreams { get; set; }

    public int SignIns => Volatile.Read(ref _signIns);

    public int Refreshes => Volatile.Read(ref _refreshes);

    public FakeCall[] Calls
    {
        get
        {
            lock (_gate)
            {
                return _calls.ToArray();
            }
        }
    }

    public FakeStreamConnection[] Streams
    {
        get
        {
            lock (_gate)
            {
                return _streams.ToArray();
            }
        }
    }

    public FakeStreamConnection LastStream => Streams[^1];

    public FakeCall[] Writes(string? pathPrefix = null) =>
        Calls.Where(c => c.Method is "PUT" or "PATCH" or "DELETE" && (pathPrefix is null || c.Path.StartsWith(pathPrefix, StringComparison.Ordinal))).ToArray();

    /// <summary>The value at <paramref name="path"/> now (a copy), or null.</summary>
    public JsonNode? Get(string path)
    {
        lock (_gate)
        {
            return Walk(path)?.DeepClone();
        }
    }

    /// <summary>Writes <paramref name="value"/> at <paramref name="path"/> as the database would for a client with the rights, and tells the streams.</summary>
    public void Set(string path, JsonNode? value)
    {
        lock (_gate)
        {
            Apply(path, value, merge: false);
        }
    }

    /// <summary>Sets one field of a request the way the site does (a patch event reaches the streams).</summary>
    public void SetField(string path, JsonNode? value) => Set(path, value);

    /// <summary>Makes the tokens issued so far stop working (the server will answer 401 and send auth_revoked).</summary>
    public void RevokeTokens()
    {
        lock (_gate)
        {
            foreach (var token in _tokenUid.Keys)
            {
                _revoked.Add(token);
            }
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (NetworkDown)
        {
            throw new HttpRequestException("network down: " + request.RequestUri);
        }

        var uri = request.RequestUri!;
        var path = uri.AbsolutePath;
        if (path.EndsWith("/v1/accounts:signInWithPassword", StringComparison.Ordinal))
        {
            return await SignInAsync(request).ConfigureAwait(false);
        }

        if (path.EndsWith("/v1/token", StringComparison.Ordinal))
        {
            return await RefreshAsync(request).ConfigureAwait(false);
        }

        return await DatabaseAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SignInAsync(HttpRequestMessage request)
    {
        var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync().ConfigureAwait(false))!.AsObject();
        if (!request.RequestUri!.Query.Contains("key=" + ApiKey, StringComparison.Ordinal))
        {
            return Error(HttpStatusCode.BadRequest, "API_KEY_INVALID");
        }

        if (SignInFailure is { } failure)
        {
            return Error(failure.Status, failure.Code);
        }

        if (body["email"]?.GetValue<string>() != Email || body["password"]?.GetValue<string>() != Password)
        {
            return Error(HttpStatusCode.BadRequest, "INVALID_LOGIN_CREDENTIALS");
        }

        Interlocked.Increment(ref _signIns);
        return Json(HttpStatusCode.OK, Issue("idToken", "refreshToken", "localId", "expiresIn"));
    }

    private async Task<HttpResponseMessage> RefreshAsync(HttpRequestMessage request)
    {
        var form = await request.Content!.ReadAsStringAsync().ConfigureAwait(false);
        if (RefreshFailure is { } failure)
        {
            return Error(failure.Status, failure.Code);
        }

        if (!form.Contains("grant_type=refresh_token", StringComparison.Ordinal) || !form.Contains("refresh_token=refresh-", StringComparison.Ordinal))
        {
            return Error(HttpStatusCode.BadRequest, "INVALID_REFRESH_TOKEN");
        }

        Interlocked.Increment(ref _refreshes);
        return Json(HttpStatusCode.OK, Issue("id_token", "refresh_token", "user_id", "expires_in"));
    }

    private JsonObject Issue(string idKey, string refreshKey, string uidKey, string expiresKey)
    {
        var n = Interlocked.Increment(ref _tokens);
        var token = "tok-" + n;
        lock (_gate)
        {
            _tokenUid[token] = PcUid;
        }

        return new JsonObject
        {
            [idKey] = token,
            [refreshKey] = "refresh-" + n,
            [uidKey] = PcUid,
            [expiresKey] = TokenSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
    }

    private async Task<HttpResponseMessage> DatabaseAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var uri = request.RequestUri!;
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        var token = query["auth"] ?? string.Empty;
        var path = string.Join('/', uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString));
        if (path.EndsWith(".json", StringComparison.Ordinal))
        {
            path = path[..^5];
        }

        var method = request.Method.Method;
        JsonNode? body = null;
        if (request.Content is not null)
        {
            var text = await request.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            body = text.Length == 0 ? null : JsonNode.Parse(text);
        }

        var stream = request.Headers.Accept.Any(a => a.MediaType == "text/event-stream");
        lock (_gate)
        {
            _calls.Add(new FakeCall(method, path, uri.Query, body, clock.GetUtcNow()));
        }

        if (stream && RedirectNextStreamTo is { } target)
        {
            RedirectNextStreamTo = null;
            var redirect = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect);
            redirect.Headers.Location = new Uri(target);
            return redirect;
        }

        bool revoked;
        bool known;
        lock (_gate)
        {
            known = _tokenUid.ContainsKey(token);
            revoked = _revoked.Contains(token);
        }

        if (!known || revoked)
        {
            return Json(HttpStatusCode.Unauthorized, new JsonObject { ["error"] = "Permission denied" });
        }

        if (DbRule?.Invoke(method, path) is { } denied)
        {
            return Json(denied, new JsonObject { ["error"] = "rule" });
        }

        if (stream)
        {
            var connection = new FakeStreamConnection(path, token, clock.GetUtcNow());
            lock (_gate)
            {
                _streams.Add(connection);
            }

            if (!SilentStreams)
            {
                connection.Put("/", Get(path));
            }

            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(connection.Body) };
            response.Content.Headers.ContentType = new("text/event-stream");
            return response;
        }

        lock (_gate)
        {
            switch (method)
            {
                case "GET":
                    var node = Walk(path);
                    if (node is JsonObject obj && query["shallow"] == "true")
                    {
                        var shallow = new JsonObject();
                        foreach (var (key, _) in obj)
                        {
                            shallow[key] = true;
                        }

                        node = shallow;
                    }

                    return Json(HttpStatusCode.OK, node?.DeepClone());
                case "PUT":
                    Apply(path, body, merge: false);
                    return new HttpResponseMessage(HttpStatusCode.NoContent);
                case "PATCH":
                    Apply(path, body, merge: true);
                    return new HttpResponseMessage(HttpStatusCode.NoContent);
                case "DELETE":
                    Apply(path, null, merge: false);
                    return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
        }

        return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
    }

    // ---- the data ----

    private JsonNode? Walk(string path)
    {
        JsonNode? node = _db;
        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (node is not JsonObject obj || !obj.TryGetPropertyValue(segment, out node))
            {
                return null;
            }
        }

        return node is JsonObject { Count: 0 } ? null : node;
    }

    private void Apply(string path, JsonNode? value, bool merge)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (merge && value is JsonObject values)
        {
            foreach (var (key, child) in values)
            {
                SetAt(segments.Concat(key.Split('/', StringSplitOptions.RemoveEmptyEntries)).ToArray(), child);
            }

            Notify(segments, values, patch: true);
            return;
        }

        SetAt(segments, value);
        Notify(segments, value, patch: false);
    }

    private void SetAt(string[] segments, JsonNode? value)
    {
        if (segments.Length == 0)
        {
            foreach (var key in _db.Select(p => p.Key).ToList())
            {
                _db.Remove(key);
            }

            if (value is JsonObject root)
            {
                foreach (var (key, child) in root)
                {
                    _db[key] = child?.DeepClone();
                }
            }

            return;
        }

        var trail = new List<JsonObject> { _db };
        var node = _db;
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (node[segments[i]] is not JsonObject next)
            {
                if (value is null)
                {
                    return;
                }

                next = [];
                node[segments[i]] = next;
            }

            node = next;
            trail.Add(node);
        }

        if (value is null)
        {
            node.Remove(segments[^1]);
        }
        else
        {
            node[segments[^1]] = value.DeepClone();
        }

        for (var i = trail.Count - 1; i > 0 && trail[i].Count == 0; i--)
        {
            trail[i - 1].Remove(segments[i - 1]);
        }
    }

    // Tells every open stream whose path contains the write.
    private void Notify(string[] written, JsonNode? value, bool patch)
    {
        foreach (var stream in _streams)
        {
            var watched = stream.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var common = Math.Min(watched.Length, written.Length);
            if (!watched.Take(common).SequenceEqual(written.Take(common)))
            {
                continue;
            }

            if (written.Length <= watched.Length)
            {
                // The write is at or above the watched path: send the watched value as a whole.
                stream.Put("/", Walk(stream.Path));
                continue;
            }

            var relative = "/" + string.Join('/', written.Skip(watched.Length));
            if (patch && value is JsonObject values)
            {
                stream.Patch(relative, values);
            }
            else
            {
                stream.Put(relative, value);
            }
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, JsonNode? body) =>
        new(status) { Content = new StringContent(body?.ToJsonString() ?? "null", Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Error(HttpStatusCode status, string code) =>
        Json(status, new JsonObject { ["error"] = new JsonObject { ["code"] = (int)status, ["message"] = code } });
}
