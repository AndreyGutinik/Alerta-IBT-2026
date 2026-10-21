using System.Text.Json.Nodes;

namespace AlertaIBT.Core.Correction;

/// <summary>What Gemini is told and the shape it must answer in.</summary>
public static class GeminiPrompt
{
    /// <summary>
    /// The model used when none is set. Checked against https://ai.google.dev/gemini-api/docs/pricing on 2026-10-20:
    /// the stable Flash-Lite model with a free tier (no cost on the free plan).
    /// </summary>
    public const string DefaultModel = "gemini-3.5-flash-lite";

    /// <summary>
    /// The system instruction, in Portuguese and short (cost and latency). It asks for a fix of the spelling only,
    /// the fields of a vehicle alert, and an empty <c>texto</c> for improper content, which the pipeline reads as a
    /// refusal.
    /// </summary>
    public const string System =
        "Voc\U000000EA corrige avisos curtos de estacionamento de uma igreja, mostrados num painel de LED.\n"
        + "Regras:\n"
        + "- Corrija ortografia, acentos e concord\U000000E2ncia. N\U000000E3o traduza.\n"
        + "- Nunca invente palavras, modelos, cores ou placas. Mantenha a placa exatamente como veio.\n"
        + "- Extraia modelo (do carro), cor, placa e motivo (pode ficar vazio).\n"
        + "- Se o texto n\U000000E3o for sobre um ve\U000000EDculo, use kind \"free\" e coloque o texto corrigido em texto.\n"
        + "- Se o texto for ofensivo, de \U000000F3dio, sexual ou trote, use kind \"free\" com texto vazio.\n"
        + "- Sempre preencha texto com o aviso inteiro corrigido.\n"
        + "- Em aviso de estacionamento, \"video aberto\" quer dizer \"vidro aberto\" (idem fechado, quebrado).\n"
        + "Exemplo 1: corola prata abc1d23 vidro abrto\n"
        + "{\"kind\":\"vehicle\",\"modelo\":\"Corolla\",\"cor\":\"Prata\",\"placa\":\"ABC1D23\",\"motivo\":\"vidro aberto\","
        + "\"texto\":\"Corolla prata ABC1D23 vidro aberto\"}\n"
        + "Exemplo 2: pais do pedro compareser na recepssao\n"
        + "{\"kind\":\"free\",\"modelo\":\"\",\"cor\":\"\",\"placa\":\"\",\"motivo\":\"\","
        + "\"texto\":\"Pais do Pedro comparecer na recep\U000000E7\U000000E3o\"}\n"
        + "Exemplo 3: fox prata abc1d54 video aberto\n"
        + "{\"kind\":\"vehicle\",\"modelo\":\"Fox\",\"cor\":\"Prata\",\"placa\":\"ABC1D54\",\"motivo\":\"vidro aberto\","
        + "\"texto\":\"Fox prata ABC1D54 vidro aberto\"}";

    /// <summary>
    /// The JSON schema of the answer: <c>kind</c> (<c>vehicle</c> or <c>free</c>), <c>modelo</c>, <c>cor</c>,
    /// <c>placa</c>, <c>motivo</c> and <c>texto</c>, all strings and all required.
    /// </summary>
    public static JsonObject CreateSchema()
    {
        JsonObject properties = new()
        {
            ["kind"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("vehicle", "free") },
        };
        foreach (var field in new[] { "modelo", "cor", "placa", "motivo", "texto" })
        {
            properties[field] = new JsonObject { ["type"] = "string" };
        }

        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = new JsonArray("kind", "modelo", "cor", "placa", "motivo", "texto"),
        };
    }

    /// <summary>
    /// The <c>thinkingConfig</c> that makes <paramref name="model"/> think as little as it can, so the answer fits the
    /// timeout: <c>thinkingLevel</c> <c>MINIMAL</c> for Gemini 3 and newer (older models reject it) and
    /// <c>thinkingBudget</c> 0 for Gemini 2.5. <c>null</c> for any other model, which gets no thinking setting (some
    /// models reject it).
    /// </summary>
    public static JsonObject? CreateThinkingConfig(string model)
    {
        if (model.StartsWith("gemini-2.5", StringComparison.OrdinalIgnoreCase))
        {
            return new JsonObject { ["thinkingBudget"] = 0 };
        }

        return model.StartsWith("gemini-3", StringComparison.OrdinalIgnoreCase)
            ? new JsonObject { ["thinkingLevel"] = "MINIMAL" }
            : null;
    }
}
