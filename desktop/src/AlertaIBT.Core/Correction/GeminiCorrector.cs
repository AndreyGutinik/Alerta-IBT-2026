using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AlertaIBT.Core.Alerts;
using Microsoft.Extensions.Logging;

namespace AlertaIBT.Core.Correction;

/// <summary>
/// Corrects an alert with Gemini over REST (<c>generateContent</c>, JSON output). It uses the free tier only: the key
/// goes in the <c>x-goog-api-key</c> header (never in the URL, never logged) and nothing is retried. It returns
/// <c>null</c> for anything that is not a usable answer (no key, no internet, quota, a bad status, a broken JSON), and
/// the refusal alert for improper content.
/// </summary>
/// <remarks>
/// The refusal alert is <c>new ParsedAlert("", "", null, "", "")</c>: it is what the pipeline turns into "conteudo
/// improprio". Gemini's answer is not trusted here beyond its shape; <see cref="CorrectionPipeline"/> holds the lock
/// against invented words and plates.
/// </remarks>
public sealed class GeminiCorrector : ICorrector
{
    private static readonly ParsedAlert Refusal = new("", "", null, "", "");

    // Finish reasons that mean Gemini did not want to answer, as opposed to STOP, MAX_TOKENS and the like.
    internal static readonly HashSet<string> BlockingFinishReasons = new(StringComparer.Ordinal)
    {
        "SAFETY", "PROHIBITED_CONTENT", "BLOCKLIST", "SPII",
    };

    private readonly HttpClient _http;
    private readonly Func<string?> _apiKey;
    private readonly Func<string> _model;
    private readonly GeminiLog _log;

    /// <param name="http">Sends the requests; its own timeout, if any, makes a call fail with <c>null</c>.</param>
    /// <param name="apiKey">Gives the API key, read on every call; <c>null</c> or blank means there is none.</param>
    /// <param name="model">
    /// Gives the model id, read on every call; blank means <see cref="GeminiPrompt.DefaultModel"/>.
    /// </param>
    /// <param name="logger">
    /// Receives one line per failure (no key, no address, no text): the reason and the model; the same reason at most once
    /// a minute. Nothing is logged when omitted.
    /// </param>
    /// <param name="clock">Time source of that throttle; the system clock when omitted.</param>
    public GeminiCorrector(HttpClient http, Func<string?> apiKey, Func<string> model, ILogger? logger = null, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(apiKey);
        ArgumentNullException.ThrowIfNull(model);

        _http = http;
        _apiKey = apiKey;
        _model = model;
        _log = new GeminiLog(logger, clock);
    }

    /// <inheritdoc />
    /// <exception cref="OperationCanceledException">The caller cancelled <paramref name="ct"/>.</exception>
    public async Task<ParsedAlert?> CorrectAsync(string raw, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(raw);
        ct.ThrowIfCancellationRequested();

        var key = _apiKey();
        var modelId = ModelId(_model());
        if (string.IsNullOrWhiteSpace(key))
        {
            _log.Failure("no-key", "sem chave de API", modelId);
            return null;
        }

        try
        {
            using var request = BuildRequest(raw, key, modelId);
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                await LogHttpFailureAsync(response, key, modelId, ct).ConfigureAwait(false);
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var alert = ReadResponse(body, out var problem);
            if (problem is { } found)
            {
                LogProblem(found, modelId);
            }

            return alert;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            // The HttpClient's own timeout (the caller did not cancel).
            _log.Failure("timeout", "tempo esgotado", modelId);
            return null;
        }
        catch (JsonException)
        {
            _log.Failure("json", "resposta com JSON invalido", modelId);
            return null;
        }
        catch (HttpRequestException ex)
        {
            _log.Failure("network", $"erro de rede ({TypeNames(ex)})", modelId);
            return null;
        }
        catch (Exception ex)
        {
            _log.Failure("error-" + ex.GetType().Name, $"erro inesperado ({TypeNames(ex)})", modelId);
            return null;
        }
    }

    private static string ModelId(string? model) => GeminiHttp.ModelId(model);

    private static string TypeNames(Exception ex) => GeminiHttp.TypeNames(ex);

    private async Task LogHttpFailureAsync(HttpResponseMessage response, string key, string modelId, CancellationToken ct)
    {
        var reason = await GeminiHttp.DescribeFailureAsync(response, key, ct).ConfigureAwait(false);
        _log.Failure("http-" + (int)response.StatusCode, reason, modelId);
    }

    private void LogProblem(Problem problem, string modelId)
    {
        if (problem.Refusal is not null)
        {
            _log.Note("refusal", $"recusou o texto ({problem.Refusal})", modelId);
        }
        else
        {
            _log.Failure("shape", "resposta em formato inesperado", modelId);
        }
    }

    // Why a 200 answer gave nothing to use: Gemini declined (Refusal says how) or the answer had no usable shape.
    private readonly record struct Problem(string? Refusal);

    private static HttpRequestMessage BuildRequest(string raw, string key, string model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            model = GeminiPrompt.DefaultModel;
        }

        JsonObject generationConfig = new()
        {
            ["responseMimeType"] = "application/json",
            ["responseJsonSchema"] = GeminiPrompt.CreateSchema(),
            ["temperature"] = 0,
        };
        if (GeminiPrompt.CreateThinkingConfig(model.Trim()) is { } thinking)
        {
            generationConfig["thinkingConfig"] = thinking;
        }

        JsonObject body = new()
        {
            ["systemInstruction"] = new JsonObject
            {
                ["parts"] = new JsonArray(new JsonObject { ["text"] = GeminiPrompt.System }),
            },
            ["contents"] = new JsonArray(new JsonObject
            {
                ["role"] = "user",
                ["parts"] = new JsonArray(new JsonObject { ["text"] = raw }),
            }),
            ["generationConfig"] = generationConfig,
        };

        return GeminiHttp.CreateRequest(model, key, body);
    }

    // The alert in Gemini's answer, the refusal alert when it declined, or null when there is nothing usable.
    private static ParsedAlert? ReadResponse(string body, out Problem? problem)
    {
        problem = null;
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            problem = new Problem(null);
            return null;
        }

        if (root.TryGetProperty("promptFeedback", out var feedback)
            && feedback.ValueKind == JsonValueKind.Object
            && feedback.TryGetProperty("blockReason", out var blockReason)
            && blockReason.ValueKind == JsonValueKind.String
            && blockReason.GetString() is { Length: > 0 } reason
            && reason != "BLOCK_REASON_UNSPECIFIED")
        {
            problem = new Problem("blockReason " + GeminiLog.Clean(reason, null, 40));
            return Refusal;
        }

        if (!root.TryGetProperty("candidates", out var candidates)
            || candidates.ValueKind != JsonValueKind.Array
            || candidates.GetArrayLength() == 0
            || candidates[0] is not { ValueKind: JsonValueKind.Object } candidate)
        {
            problem = new Problem(null);
            return null;
        }

        if (candidate.TryGetProperty("finishReason", out var finish)
            && finish.ValueKind == JsonValueKind.String
            && BlockingFinishReasons.Contains(finish.GetString()!))
        {
            problem = new Problem("finishReason " + finish.GetString());
            return Refusal;
        }

        var alert = ReadAlert(GeneratedText(candidate));
        if (alert is null)
        {
            problem = new Problem(null);
        }
        else if (ReferenceEquals(alert, Refusal))
        {
            problem = new Problem("texto vazio");
        }

        return alert;
    }

    // The text of the candidate: its text parts, in order, without the "thought" parts.
    private static string GeneratedText(JsonElement candidate)
    {
        if (!candidate.TryGetProperty("content", out var content)
            || content.ValueKind != JsonValueKind.Object
            || !content.TryGetProperty("parts", out var parts)
            || parts.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var text = new StringBuilder();
        foreach (var part in parts.EnumerateArray())
        {
            if (part.ValueKind != JsonValueKind.Object
                || (part.TryGetProperty("thought", out var thought) && thought.ValueKind == JsonValueKind.True)
                || !part.TryGetProperty("text", out var piece)
                || piece.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            text.Append(piece.GetString());
        }

        return text.ToString();
    }

    private static ParsedAlert? ReadAlert(string generated)
    {
        if (string.IsNullOrWhiteSpace(generated))
        {
            return null;
        }

        using var document = JsonDocument.Parse(generated);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || Field(root, "kind") is not { } kind
            || Field(root, "modelo") is not { } modelo
            || Field(root, "cor") is not { } cor
            || Field(root, "placa") is not { } placa
            || Field(root, "motivo") is not { } motivo
            || Field(root, "texto") is not { } texto)
        {
            return null;
        }

        switch (kind)
        {
            case "free":
                return texto.Length == 0 ? Refusal : new ParsedAlert("", "", null, "", texto);

            case "vehicle":
                var plate = placa.Length == 0 ? null : PlateParser.Normalize(placa);
                var freeText = texto.Length > 0
                    ? texto
                    : string.Join(' ', new[] { modelo, cor, placa, motivo }.Where(part => part.Length > 0));

                // Nothing was read from the text: not an answer to trust, and not a refusal either.
                return freeText.Length == 0 ? null : new ParsedAlert(modelo, cor, plate, motivo, freeText);

            default:
                return null;
        }
    }

    // A required string field of the answer, trimmed; null when it is missing or not a string.
    private static string? Field(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!.Trim()
            : null;
}
