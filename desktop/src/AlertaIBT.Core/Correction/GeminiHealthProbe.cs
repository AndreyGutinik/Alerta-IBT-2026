using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace AlertaIBT.Core.Correction;

/// <summary>How a probe of Gemini ended.</summary>
public enum GeminiProbeStatus
{
    /// <summary>A real request got a usable answer.</summary>
    Online,

    /// <summary>There is no API key: nothing was sent.</summary>
    NoKey,

    /// <summary>The request was sent and did not work (see <see cref="GeminiProbeResult.Failure"/>).</summary>
    Failed,
}

/// <summary>The class of a failed probe.</summary>
public enum GeminiFailureKind
{
    /// <summary>Not a failure.</summary>
    None,

    /// <summary>Gemini answered with an error status (401, 403, 429, 500...).</summary>
    Http,

    /// <summary>No answer in time.</summary>
    Timeout,

    /// <summary>The connection failed (no internet, DNS, TLS).</summary>
    Network,

    /// <summary>A 200 answer with no usable shape (broken JSON, no candidate).</summary>
    InvalidResponse,

    /// <summary>Gemini blocked the answer (safety).</summary>
    Safety,

    /// <summary>The caller cancelled the probe (the app is closing): not a result about Gemini.</summary>
    Cancelled,
}

/// <summary>What <see cref="GeminiHealthProbe.ProbeAsync"/> found.</summary>
/// <param name="Status">How it ended.</param>
/// <param name="Failure">The class of the failure; <see cref="GeminiFailureKind.None"/> unless failed.</param>
/// <param name="Reason">Short text for the tooltip and the log (never the key); empty when online.</param>
/// <param name="HttpStatus">The HTTP status code for <see cref="GeminiFailureKind.Http"/>.</param>
/// <param name="Latency">How long the request took.</param>
public sealed record GeminiProbeResult(
    GeminiProbeStatus Status,
    GeminiFailureKind Failure,
    string Reason,
    int? HttpStatus,
    TimeSpan Latency);

/// <summary>Probes whether Gemini works right now.</summary>
public interface IGeminiHealthProbe
{
    /// <summary>Sends one minimal request. Never throws.</summary>
    Task<GeminiProbeResult> ProbeAsync(CancellationToken ct);
}

/// <summary>
/// Asks Gemini a tiny real question ("ok", at most 8 tokens out) through the same client, address, model and key as the
/// corrections, so a green result proves the real path works, quota included (a <c>models.get</c> would not). The key goes
/// in the <c>x-goog-api-key</c> header only and is never logged. Failures use the same throttled log lines as the corrector;
/// a success writes one Information line.
/// </summary>
public sealed class GeminiHealthProbe : IGeminiHealthProbe
{
    /// <summary>How long the probe waits. Longer than a correction may take: right after the app opens the first connection is slow, and nobody waits for the probe (a false "Offline" for 30 s is worse than a slow check).</summary>
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(20);

    private const int MaxOutputTokens = 8;

    private readonly HttpClient _http;
    private readonly Func<string?> _apiKey;
    private readonly Func<string> _model;
    private readonly GeminiLog _log;
    private readonly TimeProvider _clock;

    /// <param name="http">The client of <see cref="GeminiCorrector"/> (warm connections included).</param>
    /// <param name="apiKey">Gives the API key, read on every probe; <c>null</c> or blank means there is none.</param>
    /// <param name="model">Gives the model id, read on every probe; blank means <see cref="GeminiPrompt.DefaultModel"/>.</param>
    /// <param name="logger">Receives the log lines; nothing is logged when omitted.</param>
    /// <param name="clock">Time source of the timeout, the latency and the log throttle; the system clock when omitted.</param>
    public GeminiHealthProbe(HttpClient http, Func<string?> apiKey, Func<string> model, ILogger? logger = null, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(apiKey);
        ArgumentNullException.ThrowIfNull(model);

        _http = http;
        _apiKey = apiKey;
        _model = model;
        _clock = clock ?? TimeProvider.System;
        _log = new GeminiLog(logger, _clock);
    }

    /// <inheritdoc />
    public async Task<GeminiProbeResult> ProbeAsync(CancellationToken ct)
    {
        string modelId;
        string? key;
        try
        {
            key = _apiKey();
            modelId = GeminiHttp.ModelId(_model());
        }
        catch (Exception)
        {
            return Failed(GeminiFailureKind.InvalidResponse, "configura\U000000E7\U000000E3o inv\U000000E1lida", null, TimeSpan.Zero);
        }

        if (string.IsNullOrWhiteSpace(key))
        {
            return new GeminiProbeResult(GeminiProbeStatus.NoKey, GeminiFailureKind.None, "sem chave de API", null, TimeSpan.Zero);
        }

        var started = _clock.GetTimestamp();
        try
        {
            using var timeout = new CancellationTokenSource(ProbeTimeout, _clock);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            using var request = GeminiHttp.CreateRequest(modelId, key, BuildBody(modelId));
            using var response = await _http.SendAsync(request, linked.Token).ConfigureAwait(false);
            var latency = _clock.GetElapsedTime(started);
            if (!response.IsSuccessStatusCode)
            {
                var reason = await GeminiHttp.DescribeFailureAsync(response, key, linked.Token).ConfigureAwait(false);
                _log.Failure("http-" + (int)response.StatusCode, reason, modelId);
                return Failed(GeminiFailureKind.Http, reason, (int)response.StatusCode, latency);
            }

            var body = await response.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false);
            latency = _clock.GetElapsedTime(started);
            var result = Judge(body, modelId, latency);
            if (result.Status == GeminiProbeStatus.Online)
            {
                _log.Connected(modelId, (long)latency.TotalMilliseconds);
            }

            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return Failed(GeminiFailureKind.Cancelled, "cancelado", null, _clock.GetElapsedTime(started));
        }
        catch (OperationCanceledException)
        {
            return Log("timeout", GeminiFailureKind.Timeout, "tempo esgotado", modelId, started);
        }
        catch (JsonException)
        {
            return Log("json", GeminiFailureKind.InvalidResponse, "resposta com JSON inv\U000000E1lido", modelId, started);
        }
        catch (HttpRequestException ex)
        {
            return Log("network", GeminiFailureKind.Network, $"erro de rede ({GeminiHttp.TypeNames(ex)})", modelId, started);
        }
        catch (Exception ex)
        {
            return Log("error-" + ex.GetType().Name, GeminiFailureKind.Network, $"erro inesperado ({GeminiHttp.TypeNames(ex)})", modelId, started);
        }
    }

    // A tiny question with the smallest answer: no system prompt, no schema, the corrector's own minimal-thinking rule.
    private static JsonObject BuildBody(string model)
    {
        JsonObject generationConfig = new()
        {
            ["maxOutputTokens"] = MaxOutputTokens,
            ["temperature"] = 0,
        };
        if (GeminiPrompt.CreateThinkingConfig(model) is { } thinking)
        {
            generationConfig["thinkingConfig"] = thinking;
        }

        return new JsonObject
        {
            ["contents"] = new JsonArray(new JsonObject
            {
                ["role"] = "user",
                ["parts"] = new JsonArray(new JsonObject { ["text"] = "ok" }),
            }),
            ["generationConfig"] = generationConfig,
        };
    }

    // Online when a candidate came back that was not blocked: its text may be empty (the token limit is tiny).
    private GeminiProbeResult Judge(string body, string modelId, TimeSpan latency)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return Log("shape", GeminiFailureKind.InvalidResponse, "resposta em formato inesperado", modelId, latency);
        }

        if (root.TryGetProperty("promptFeedback", out var feedback)
            && feedback.ValueKind == JsonValueKind.Object
            && feedback.TryGetProperty("blockReason", out var blockReason)
            && blockReason.ValueKind == JsonValueKind.String
            && blockReason.GetString() is { Length: > 0 } reason
            && reason != "BLOCK_REASON_UNSPECIFIED")
        {
            return Log("safety", GeminiFailureKind.Safety, "resposta bloqueada (blockReason " + GeminiLog.Clean(reason, null, 40) + ")", modelId, latency);
        }

        if (!root.TryGetProperty("candidates", out var candidates)
            || candidates.ValueKind != JsonValueKind.Array
            || candidates.GetArrayLength() == 0
            || candidates[0] is not { ValueKind: JsonValueKind.Object } candidate)
        {
            return Log("shape", GeminiFailureKind.InvalidResponse, "resposta em formato inesperado", modelId, latency);
        }

        if (candidate.TryGetProperty("finishReason", out var finish)
            && finish.ValueKind == JsonValueKind.String
            && GeminiCorrector.BlockingFinishReasons.Contains(finish.GetString()!))
        {
            return Log("safety", GeminiFailureKind.Safety, "resposta bloqueada (finishReason " + finish.GetString() + ")", modelId, latency);
        }

        return new GeminiProbeResult(GeminiProbeStatus.Online, GeminiFailureKind.None, string.Empty, null, latency);
    }

    private GeminiProbeResult Log(string kind, GeminiFailureKind failure, string reason, string modelId, long started) =>
        Log(kind, failure, reason, modelId, _clock.GetElapsedTime(started));

    private GeminiProbeResult Log(string kind, GeminiFailureKind failure, string reason, string modelId, TimeSpan latency)
    {
        _log.Failure(kind, reason, modelId);
        return Failed(failure, reason, null, latency);
    }

    private static GeminiProbeResult Failed(GeminiFailureKind failure, string reason, int? httpStatus, TimeSpan latency) =>
        new(GeminiProbeStatus.Failed, failure, reason, httpStatus, latency);
}
