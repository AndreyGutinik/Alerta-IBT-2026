using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AlertaIBT.Core.Firebase;

/// <summary>Why a call to the Realtime Database failed.</summary>
public enum RtdbFailure
{
    /// <summary>The rules (or the token) refused the call, even with a freshly renewed token.</summary>
    PermissionDenied,

    /// <summary>The server could not be reached, or the call took too long.</summary>
    Network,

    /// <summary>The server answered with an error of its own (5xx, 429).</summary>
    Server,

    /// <summary>The answer was not what the protocol says.</summary>
    Protocol,

    /// <summary>Anything else (a 4xx that is not a permission problem).</summary>
    Other,
}

/// <summary>A call to the Realtime Database failed. The message never holds a token or the address.</summary>
public sealed class RtdbException : Exception
{
    /// <param name="failure">Why it failed.</param>
    /// <param name="message">What happened.</param>
    /// <param name="status">The HTTP status, when there was an answer.</param>
    /// <param name="inner">The cause, when there is one.</param>
    public RtdbException(RtdbFailure failure, string message, HttpStatusCode? status = null, Exception? inner = null)
        : base(message, inner)
    {
        Failure = failure;
        Status = status;
    }

    /// <summary>Why it failed.</summary>
    public RtdbFailure Failure { get; }

    /// <summary>The HTTP status, when there was an answer.</summary>
    public HttpStatusCode? Status { get; }
}

/// <summary>
/// The Realtime Database over its REST API (<c>{databaseUrl}/{path}.json?auth={idToken}</c>): get, put, patch and delete, and the
/// streaming request that <see cref="RtdbListener"/> reads. No SDK. A 401 (which is also how the database says "permission denied")
/// is tried once more with a renewed token before it counts as a refusal. The token and the address are never logged.
/// </summary>
public sealed class RealtimeDbClient
{
    /// <summary>The longest a plain request may take.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

    private const int MaxRedirects = 3;

    private readonly HttpClient _http;
    private readonly Uri _baseUrl;
    private readonly ITokenSource _tokens;
    private readonly ILogger _logger;

    /// <param name="http">
    /// The HTTP client. It must not time out by itself (streams last for hours; each plain call has its own limit) and it may or
    /// may not follow redirects: both work.
    /// </param>
    /// <param name="databaseUrl">The database address (<c>https://x-default-rtdb.firebaseio.com</c>, or <c>http://127.0.0.1:9000?ns=demo-alertaibt</c> for the emulator).</param>
    /// <param name="tokens">Gives the ID token.</param>
    /// <param name="logger">Receives what happened, without secrets.</param>
    public RealtimeDbClient(HttpClient http, string databaseUrl, ITokenSource tokens, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseUrl);
        ArgumentNullException.ThrowIfNull(tokens);

        if (!Uri.TryCreate(databaseUrl.Trim(), UriKind.Absolute, out var url) || url.Scheme is not ("https" or "http"))
        {
            throw new ArgumentException("The database URL is not an http(s) address.", nameof(databaseUrl));
        }

        _http = http;
        _baseUrl = url;
        _tokens = tokens;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>The token source (the listener asks it how long a connection may live).</summary>
    public ITokenSource Tokens => _tokens;

    /// <summary>Reads the value at <paramref name="path"/>; <c>null</c> when there is nothing.</summary>
    /// <param name="path">For example <c>users/abc</c>.</param>
    /// <param name="ct">Cancels the call.</param>
    /// <param name="shallow">Only the keys of the children (their values come as <c>true</c>), for a cheap look at a big node.</param>
    /// <exception cref="RtdbException">The call failed.</exception>
    public async Task<JsonNode?> GetAsync(string path, CancellationToken ct, bool shallow = false)
    {
        var text = await SendAsync(HttpMethod.Get, path, shallow ? "shallow=true" : null, null, ct).ConfigureAwait(false);
        return ParseOrThrow(text);
    }

    /// <summary>Replaces the value at <paramref name="path"/> (a <c>null</c> value deletes it).</summary>
    /// <exception cref="RtdbException">The call failed.</exception>
    public Task PutAsync(string path, JsonNode? value, CancellationToken ct) =>
        SendAsync(HttpMethod.Put, path, "print=silent", value is null ? "null" : value.ToJsonString(), ct);

    /// <summary>Changes only the children named in <paramref name="values"/> (a <c>null</c> child deletes it).</summary>
    /// <exception cref="RtdbException">The call failed.</exception>
    public Task PatchAsync(string path, JsonObject values, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(values);

        return SendAsync(HttpMethod.Patch, path, "print=silent", values.ToJsonString(), ct);
    }

    /// <summary>Deletes the value at <paramref name="path"/>.</summary>
    /// <exception cref="RtdbException">The call failed.</exception>
    public Task DeleteAsync(string path, CancellationToken ct) =>
        SendAsync(HttpMethod.Delete, path, "print=silent", null, ct);

    /// <summary>
    /// Opens the streaming request for <paramref name="path"/>: the response is returned as soon as its headers arrive, and its body
    /// is the event stream. The caller disposes it. Redirects (307, how the database points to the right server) are followed.
    /// </summary>
    /// <param name="path">What to watch.</param>
    /// <param name="forceRefresh">Whether to renew the token first (after the server said it was revoked).</param>
    /// <param name="ct">Cancels the connection (also while the stream is read).</param>
    /// <exception cref="RtdbException">The server answered with an error; a refused token is <see cref="RtdbFailure.PermissionDenied"/>.</exception>
    public async Task<HttpResponseMessage> OpenStreamAsync(string path, bool forceRefresh, CancellationToken ct)
    {
        var token = await GetTokenAsync(forceRefresh, ct).ConfigureAwait(false);
        var response = await SendOnceAsync(HttpMethod.Get, path, null, null, token, stream: true, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            response.Dispose();
            token = await GetTokenAsync(true, ct).ConfigureAwait(false);
            response = await SendOnceAsync(HttpMethod.Get, path, null, null, token, stream: true, ct).ConfigureAwait(false);
        }

        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        var status = response.StatusCode;
        response.Dispose();
        throw Failure(status);
    }

    private async Task<string> SendAsync(HttpMethod method, string path, string? query, string? body, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RequestTimeout);
        try
        {
            var token = await GetTokenAsync(false, timeout.Token).ConfigureAwait(false);
            var response = await SendOnceAsync(method, path, query, body, token, stream: false, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                response.Dispose();
                token = await GetTokenAsync(true, timeout.Token).ConfigureAwait(false);
                response = await SendOnceAsync(method, path, query, body, token, stream: false, timeout.Token).ConfigureAwait(false);
            }

            using (response)
            {
                if (!response.IsSuccessStatusCode)
                {
                    throw Failure(response.StatusCode);
                }

                return await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new RtdbException(RtdbFailure.Network, "O banco de dados demorou demais para responder.");
        }
    }

    private async Task<string> GetTokenAsync(bool forceRefresh, CancellationToken ct)
    {
        try
        {
            return await _tokens.GetTokenAsync(forceRefresh, ct).ConfigureAwait(false);
        }
        catch (FirebaseAuthException ex)
        {
            throw new RtdbException(
                ex.IsTransient ? RtdbFailure.Network : RtdbFailure.PermissionDenied,
                "Sem token do Firebase: " + ex.Message,
                inner: ex);
        }
    }

    private async Task<HttpResponseMessage> SendOnceAsync(
        HttpMethod method,
        string path,
        string? query,
        string? body,
        string token,
        bool stream,
        CancellationToken ct)
    {
        var uri = BuildUri(path, query, token);
        for (var hop = 0; ; hop++)
        {
            using var request = new HttpRequestMessage(method, uri);
            if (stream)
            {
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
                request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
            }

            if (body is not null)
            {
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            }

            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                // Not the exception text: it may carry the address, which holds the token.
                throw new RtdbException(RtdbFailure.Network, "Sem conex\U000000E3o com o banco de dados.", inner: ex);
            }

            if (response.StatusCode is HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect
                or HttpStatusCode.Found or HttpStatusCode.MovedPermanently)
            {
                var location = response.Headers.Location;
                response.Dispose();
                if (location is null || hop >= MaxRedirects)
                {
                    throw new RtdbException(RtdbFailure.Protocol, "Redirecionamento inv\U000000E1lido do banco de dados.");
                }

                uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                continue;
            }

            return response;
        }
    }

    private Uri BuildUri(string path, string? query, string token)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString);
        var builder = new UriBuilder(_baseUrl);
        var prefix = builder.Path.TrimEnd('/');
        builder.Path = prefix + "/" + string.Join('/', segments) + ".json";

        // The emulator is addressed as http://127.0.0.1:9000?ns=name: that query is kept next to the call's own.
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(_baseUrl.Query))
        {
            parts.Add(_baseUrl.Query.TrimStart('?'));
        }

        if (!string.IsNullOrEmpty(query))
        {
            parts.Add(query);
        }

        parts.Add("auth=" + Uri.EscapeDataString(token));
        builder.Query = string.Join('&', parts);
        return builder.Uri;
    }

    private RtdbException Failure(HttpStatusCode status)
    {
        _logger.LogWarning("Banco de dados respondeu HTTP {Status}", (int)status);
        return status switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                new(RtdbFailure.PermissionDenied, "O banco de dados recusou o acesso (regras ou conta do PC).", status),
            HttpStatusCode.TooManyRequests or >= HttpStatusCode.InternalServerError =>
                new(RtdbFailure.Server, "O banco de dados est\U000000E1 com problema (HTTP " + (int)status + ").", status),
            _ => new(RtdbFailure.Other, "O banco de dados respondeu HTTP " + (int)status + ".", status),
        };
    }

    private static JsonNode? ParseOrThrow(string text)
    {
        try
        {
            return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
        }
        catch (JsonException ex)
        {
            throw new RtdbException(RtdbFailure.Protocol, "Resposta inesperada do banco de dados.", inner: ex);
        }
    }
}
