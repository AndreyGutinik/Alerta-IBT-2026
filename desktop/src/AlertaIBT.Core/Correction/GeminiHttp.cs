using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AlertaIBT.Core.Correction;

/// <summary>
/// The HTTP client of <see cref="GeminiCorrector"/>. The app keeps one for its whole life, and its connections are kept
/// warm on purpose: the first call after a quiet spell used to pay a new TLS handshake and ran into the time limit, and
/// the free quota forbids "warm-up" requests, so the pool does the job instead.
/// </summary>
public static class GeminiHttp
{
    /// <summary>
    /// The timeout of the client itself: longer than <see cref="CorrectionPipeline.DefaultGeminiTimeout"/>, so the pipeline
    /// is the one that gives up first and the offline corrector takes over.
    /// </summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    /// <summary>A pooled connection is replaced after this long, so a changed address is noticed.</summary>
    public static readonly TimeSpan ConnectionLifetime = TimeSpan.FromMinutes(5);

    /// <summary>An idle connection is closed after this long: between messages in a busy hour it stays open.</summary>
    public static readonly TimeSpan ConnectionIdleTimeout = TimeSpan.FromMinutes(2);

    /// <summary>The handler with the lifetimes above.</summary>
    public static SocketsHttpHandler CreateHandler() => new()
    {
        PooledConnectionLifetime = ConnectionLifetime,
        PooledConnectionIdleTimeout = ConnectionIdleTimeout,
    };

    /// <summary>The long-lived client for <see cref="GeminiCorrector"/>; the caller owns it.</summary>
    public static HttpClient CreateClient() => new(CreateHandler(), disposeHandler: true) { Timeout = RequestTimeout };

    // ---- what the corrector and the health probe share ----

    internal const string Endpoint = "https://generativelanguage.googleapis.com/v1beta/models/";

    /// <summary>The model id to ask: the trimmed setting, or <see cref="GeminiPrompt.DefaultModel"/> when it is blank.</summary>
    internal static string ModelId(string? model) =>
        string.IsNullOrWhiteSpace(model) ? GeminiPrompt.DefaultModel : model.Trim();

    /// <summary>A <c>generateContent</c> POST for <paramref name="model"/>; the key goes in the <c>x-goog-api-key</c> header only.</summary>
    internal static HttpRequestMessage CreateRequest(string model, string key, JsonObject body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"{Endpoint}{Uri.EscapeDataString(ModelId(model))}:generateContent")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, MediaTypeHeaderValue.Parse("application/json")),
        };
        request.Headers.Add("x-goog-api-key", key);
        return request;
    }

    // Type names only: the message of an exception can carry addresses.
    internal static string TypeNames(Exception ex) =>
        ex.InnerException is { } inner ? $"{ex.GetType().Name}/{inner.GetType().Name}" : ex.GetType().Name;

    /// <summary>
    /// "HTTP 403 PERMISSION_DENIED: API key not valid.": the code, and Google's own status and message when the body has
    /// them (with the key hidden).
    /// </summary>
    internal static async Task<string> DescribeFailureAsync(HttpResponseMessage response, string key, CancellationToken ct)
    {
        var reason = $"HTTP {(int)response.StatusCode}";
        try
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.Object)
            {
                if (error.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.String
                    && GeminiLog.Clean(status.GetString()!, key, 40) is { Length: > 0 } statusText)
                {
                    reason += " " + statusText;
                }

                if (error.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String
                    && GeminiLog.Clean(message.GetString()!, key) is { Length: > 0 } messageText)
                {
                    reason += ": " + messageText;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // No readable error body: the code alone says enough.
        }

        return reason;
    }
}
