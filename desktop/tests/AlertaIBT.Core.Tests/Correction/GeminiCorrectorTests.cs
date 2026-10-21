using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AlertaIBT.Core.Alerts;
using AlertaIBT.Core.Correction;

namespace AlertaIBT.Core.Tests.Correction;

public class GeminiCorrectorTests
{
    private const string Key = "test-key-123";

    private sealed record Call(HttpMethod Method, Uri Uri, string? KeyHeader, string Body);

    // Answers every request with `respond`, remembering what was asked.
    private sealed class FakeHandler(Func<CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public List<Call> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct);
            request.Headers.TryGetValues("x-goog-api-key", out var keys);
            Calls.Add(new Call(request.Method, request.RequestUri!, keys?.SingleOrDefault(), body));
            return await respond(ct);
        }
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    // The envelope Gemini answers with, around the JSON text it generated.
    private static string Envelope(string generatedText, string finishReason = "STOP") =>
        new JsonObject
        {
            ["candidates"] = new JsonArray(new JsonObject
            {
                ["content"] = new JsonObject
                {
                    ["role"] = "model",
                    ["parts"] = new JsonArray(new JsonObject { ["text"] = generatedText }),
                },
                ["finishReason"] = finishReason,
            }),
        }.ToJsonString();

    private static string Reply(string kind, string modelo = "", string cor = "", string placa = "", string motivo = "", string texto = "") =>
        new JsonObject
        {
            ["kind"] = kind,
            ["modelo"] = modelo,
            ["cor"] = cor,
            ["placa"] = placa,
            ["motivo"] = motivo,
            ["texto"] = texto,
        }.ToJsonString();

    private static (GeminiCorrector Corrector, FakeHandler Handler) Make(
        Func<CancellationToken, Task<HttpResponseMessage>> respond,
        string? key = Key,
        string model = "gemini-test")
    {
        var handler = new FakeHandler(respond);
        return (new GeminiCorrector(new HttpClient(handler), () => key, () => model), handler);
    }

    private static (GeminiCorrector Corrector, FakeHandler Handler) Answering(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        Make(_ => Task.FromResult(Json(json, status)));

    private static (GeminiCorrector Corrector, FakeHandler Handler) Generating(string generatedText) =>
        Answering(Envelope(generatedText));

    private static Task<ParsedAlert?> Run(GeminiCorrector corrector, string raw = "corola prata abc1d23 vidro abrto") =>
        corrector.CorrectAsync(raw, CancellationToken.None);

    // ---- request ----

    [Fact]
    public async Task Request_IsAPostToGenerateContentWithTheKeyInTheHeaderOnly()
    {
        var (corrector, handler) = Generating(Reply("free", texto: "ok"));

        await Run(corrector);

        var call = Assert.Single(handler.Calls);
        Assert.Equal(HttpMethod.Post, call.Method);
        Assert.Equal("https://generativelanguage.googleapis.com/v1beta/models/gemini-test:generateContent", call.Uri.ToString());
        Assert.Equal(Key, call.KeyHeader);
        Assert.DoesNotContain(Key, call.Uri.ToString());
        Assert.DoesNotContain(Key, call.Body);
        Assert.Empty(call.Uri.Query);
    }

    [Fact]
    public async Task Request_BodyHasTheSystemPromptTheTextAndTheJsonConfig()
    {
        var (corrector, handler) = Generating(Reply("free", texto: "ok"));

        await Run(corrector, "corola prata \"abc1d23\" vidro abrto\nlinha 2");

        var body = JsonNode.Parse(handler.Calls.Single().Body)!;
        Assert.Equal(GeminiPrompt.System, (string?)body["systemInstruction"]!["parts"]![0]!["text"]);

        var contents = body["contents"]!.AsArray();
        Assert.Single(contents);
        Assert.Equal("user", (string?)contents[0]!["role"]);
        Assert.Equal("corola prata \"abc1d23\" vidro abrto\nlinha 2", (string?)contents[0]!["parts"]![0]!["text"]);

        var config = body["generationConfig"]!;
        Assert.Equal("application/json", (string?)config["responseMimeType"]);
        Assert.Equal(0, (double?)config["temperature"]);
    }

    [Fact]
    public async Task Request_SchemaHasTheSixRequiredStringFieldsAndKindIsAnEnum()
    {
        var (corrector, handler) = Generating(Reply("free", texto: "ok"));

        await Run(corrector);

        var schema = JsonNode.Parse(handler.Calls.Single().Body)!["generationConfig"]!["responseJsonSchema"]!;
        Assert.Equal("object", (string?)schema["type"]);
        var required = schema["required"]!.AsArray().Select(node => (string?)node).Order().ToArray();
        Assert.Equal(["cor", "kind", "modelo", "motivo", "placa", "texto"], required);

        var properties = schema["properties"]!.AsObject();
        Assert.Equal(6, properties.Count);
        foreach (var (_, property) in properties)
        {
            Assert.Equal("string", (string?)property!["type"]);
        }

        Assert.Equal(["vehicle", "free"], schema["properties"]!["kind"]!["enum"]!.AsArray().Select(node => (string?)node).ToArray());
    }

    [Theory]
    [InlineData("gemini-3.5-flash-lite", "thinkingLevel", "MINIMAL")]
    [InlineData("gemini-3.1-flash-lite", "thinkingLevel", "MINIMAL")]
    [InlineData("gemini-2.5-flash-lite", "thinkingBudget", "0")]
    public async Task Request_ThinkingIsAtItsMinimumForTheModelFamily(string model, string field, string expected)
    {
        var (corrector, handler) = Make(_ => Task.FromResult(Json(Envelope(Reply("free", texto: "ok")))), model: model);

        await Run(corrector);

        var thinking = JsonNode.Parse(handler.Calls.Single().Body)!["generationConfig"]!["thinkingConfig"]!.AsObject();
        Assert.Single(thinking);
        Assert.Equal(expected, thinking[field]!.ToJsonString().Trim('"'));
    }

    [Fact]
    public async Task Request_NoThinkingConfigForAModelWeDoNotKnow()
    {
        var (corrector, handler) = Make(_ => Task.FromResult(Json(Envelope(Reply("free", texto: "ok")))), model: "some-other-model");

        await Run(corrector);

        Assert.Null(JsonNode.Parse(handler.Calls.Single().Body)!["generationConfig"]!["thinkingConfig"]);
    }

    [Fact]
    public async Task Request_ReadsTheKeyAndTheModelOnEveryCall()
    {
        var key = "first";
        var model = "model-a";
        var handler = new FakeHandler(_ => Task.FromResult(Json(Envelope(Reply("free", texto: "ok")))));
        var corrector = new GeminiCorrector(new HttpClient(handler), () => key, () => model);

        await Run(corrector);
        key = "second";
        model = "model-b";
        await Run(corrector);

        Assert.Equal(["first", "second"], handler.Calls.Select(call => call.KeyHeader).ToArray());
        Assert.EndsWith("/models/model-a:generateContent", handler.Calls[0].Uri.AbsolutePath);
        Assert.EndsWith("/models/model-b:generateContent", handler.Calls[1].Uri.AbsolutePath);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Request_ABlankModelFallsBackToTheDefault(string model)
    {
        var (corrector, handler) = Make(_ => Task.FromResult(Json(Envelope(Reply("free", texto: "ok")))), model: model);

        await Run(corrector);

        Assert.EndsWith($"/models/{GeminiPrompt.DefaultModel}:generateContent", handler.Calls.Single().Uri.AbsolutePath);
    }

    [Fact]
    public async Task Request_TheModelIsEscapedInTheUrl()
    {
        var (corrector, handler) = Make(_ => Task.FromResult(Json(Envelope(Reply("free", texto: "ok")))), model: "a/b?c");

        await Run(corrector);

        Assert.StartsWith("https://generativelanguage.googleapis.com/v1beta/models/a%2Fb%3Fc:generateContent", handler.Calls.Single().Uri.AbsoluteUri);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task NoKey_ReturnsNullWithoutAnyHttpCall(string? key)
    {
        var (corrector, handler) = Make(_ => Task.FromResult(Json(Envelope(Reply("free", texto: "ok")))), key);

        Assert.Null(await Run(corrector));
        Assert.Empty(handler.Calls);
    }

    // ---- prompt ----

    [Fact]
    public void Prompt_IsShortAndSaysWhatItMust()
    {
        Assert.InRange(GeminiPrompt.System.Length, 300, 1300);
        Assert.Contains("kind", GeminiPrompt.System);
        Assert.Contains("free", GeminiPrompt.System);
        Assert.Contains("vehicle", GeminiPrompt.System);
        Assert.Contains("placa", GeminiPrompt.System);
        Assert.Contains("modelo", GeminiPrompt.System);
        Assert.Contains("motivo", GeminiPrompt.System);
        Assert.Contains("texto", GeminiPrompt.System);
        Assert.Contains("ofensivo", GeminiPrompt.System);
        Assert.Contains("abc1d23", GeminiPrompt.System, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Prompt_SaysThatVideoAbertoMeansVidroAberto()
    {
        Assert.Contains("video aberto", GeminiPrompt.System, StringComparison.Ordinal);
        Assert.Contains("vidro aberto", GeminiPrompt.System, StringComparison.Ordinal);
    }

    [Fact]
    public void DefaultModel_IsAFlashLiteModel()
    {
        Assert.Contains("flash-lite", GeminiPrompt.DefaultModel);
    }

    // ---- response ----

    [Fact]
    public async Task Response_VehicleBecomesAParsedAlert()
    {
        var (corrector, _) = Generating(Reply("vehicle", "Corolla", "Prata", "abc1d23", "vidro aberto", "Corolla prata ABC1D23 vidro aberto"));

        var alert = await Run(corrector);

        Assert.Equal(new ParsedAlert("Corolla", "Prata", "ABC-1D23", "vidro aberto", "Corolla prata ABC1D23 vidro aberto"), alert);
    }

    [Fact]
    public async Task Response_ValuesAreTrimmedAndCaseIsKept()
    {
        var (corrector, _) = Generating(Reply("vehicle", "  Gol \n", " BRANCO", " ABC-1234 ", "  Farol aceso ", "  Gol branco  "));

        var alert = await Run(corrector);

        Assert.Equal(new ParsedAlert("Gol", "BRANCO", "ABC-1234", "Farol aceso", "Gol branco"), alert);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("XYZ")]
    [InlineData("ABC-12")]
    public async Task Response_AnEmptyOrInvalidPlateIsNull(string placa)
    {
        var (corrector, _) = Generating(Reply("vehicle", "Gol", "Branco", placa, "farol aceso", "Gol branco farol aceso"));

        var alert = await Run(corrector);

        Assert.NotNull(alert);
        Assert.Null(alert.Plate);
    }

    [Fact]
    public async Task Response_WithoutTextoTheFreeTextJoinsTheFields()
    {
        var (corrector, _) = Generating(Reply("vehicle", "Gol", "Branco", "abc1234", "farol aceso"));

        var alert = await Run(corrector);

        Assert.Equal("Gol Branco abc1234 farol aceso", alert!.FreeText);
        Assert.Equal("ABC-1234", alert.Plate);
    }

    [Fact]
    public async Task Response_JoinSkipsEmptyFieldsWithSingleSpaces()
    {
        var (corrector, _) = Generating(Reply("vehicle", "Gol", placa: "abc1234"));

        var alert = await Run(corrector);

        Assert.Equal("Gol abc1234", alert!.FreeText);
    }

    [Fact]
    public async Task Response_AVehicleWithNothingInItIsNull()
    {
        var (corrector, _) = Generating(Reply("vehicle"));

        Assert.Null(await Run(corrector));
    }

    [Fact]
    public async Task Response_FreeBecomesAnAnnouncement()
    {
        var (corrector, _) = Generating(Reply("free", "ignored", "ignored", "abc1234", "ignored", "  Pais do Pedro compare\U000000E7am na recep\U000000E7\U000000E3o "));

        var alert = await Run(corrector);

        Assert.Equal(new ParsedAlert("", "", null, "", "Pais do Pedro compare\U000000E7am na recep\U000000E7\U000000E3o"), alert);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Response_FreeWithEmptyTextoIsTheRefusalAlert(string texto)
    {
        var (corrector, _) = Generating(Reply("free", texto: texto));

        var alert = await Run(corrector);

        Assert.Equal(new ParsedAlert("", "", null, "", ""), alert);
    }

    [Fact]
    public async Task Response_TextPartsAreConcatenated()
    {
        var json = Reply("free", texto: "Aviso importante");
        var envelope = new JsonObject
        {
            ["candidates"] = new JsonArray(new JsonObject
            {
                ["content"] = new JsonObject
                {
                    ["parts"] = new JsonArray(
                        new JsonObject { ["text"] = json[..10] },
                        new JsonObject { ["text"] = json[10..] }),
                },
                ["finishReason"] = "STOP",
            }),
        }.ToJsonString();
        var (corrector, _) = Answering(envelope);

        var alert = await Run(corrector);

        Assert.Equal("Aviso importante", alert!.FreeText);
    }

    [Fact]
    public async Task Response_ThoughtPartsAreIgnored()
    {
        var envelope = new JsonObject
        {
            ["candidates"] = new JsonArray(new JsonObject
            {
                ["content"] = new JsonObject
                {
                    ["parts"] = new JsonArray(
                        new JsonObject { ["text"] = "thinking out loud, not JSON", ["thought"] = true },
                        new JsonObject { ["text"] = Reply("free", texto: "Aviso") }),
                },
                ["finishReason"] = "STOP",
            }),
        }.ToJsonString();
        var (corrector, _) = Answering(envelope);

        Assert.Equal("Aviso", (await Run(corrector))!.FreeText);
    }

    [Fact]
    public async Task Response_UnknownFieldsAreIgnored()
    {
        var generated = new JsonObject
        {
            ["kind"] = "free",
            ["modelo"] = "",
            ["cor"] = "",
            ["placa"] = "",
            ["motivo"] = "",
            ["texto"] = "Aviso",
            ["extra"] = 7,
        }.ToJsonString();
        var (corrector, _) = Generating(generated);

        Assert.Equal("Aviso", (await Run(corrector))!.FreeText);
    }

    // ---- refusals ----

    [Fact]
    public async Task Refusal_ABlockedPromptIsTheRefusalAlert()
    {
        var (corrector, _) = Answering("""{"promptFeedback":{"blockReason":"PROHIBITED_CONTENT"}}""");

        Assert.Equal(new ParsedAlert("", "", null, "", ""), await Run(corrector));
    }

    [Theory]
    [InlineData("SAFETY")]
    [InlineData("PROHIBITED_CONTENT")]
    [InlineData("BLOCKLIST")]
    [InlineData("SPII")]
    public async Task Refusal_ASafetyFinishReasonIsTheRefusalAlert(string finishReason)
    {
        var (corrector, _) = Answering(Envelope(Reply("free", texto: "text that should not be used"), finishReason));

        Assert.Equal(new ParsedAlert("", "", null, "", ""), await Run(corrector));
    }

    [Fact]
    public async Task Refusal_AFinishReasonWithNoTextAtAllIsStillTheRefusalAlert()
    {
        var (corrector, _) = Answering("""{"candidates":[{"finishReason":"SAFETY"}]}""");

        Assert.Equal(new ParsedAlert("", "", null, "", ""), await Run(corrector));
    }

    [Fact]
    public async Task Refusal_AnUnspecifiedBlockReasonIsNotABlock()
    {
        var envelope = new JsonObject
        {
            ["promptFeedback"] = new JsonObject { ["blockReason"] = "BLOCK_REASON_UNSPECIFIED" },
            ["candidates"] = new JsonArray(new JsonObject
            {
                ["content"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = Reply("free", texto: "Aviso") }) },
                ["finishReason"] = "STOP",
            }),
        }.ToJsonString();
        var (corrector, _) = Answering(envelope);

        Assert.Equal("Aviso", (await Run(corrector))!.FreeText);
    }

    // ---- failures ----

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task Failure_ANon2xxStatusIsNullEvenWithAGoodBody(HttpStatusCode status)
    {
        var (corrector, _) = Answering(Envelope(Reply("free", texto: "Aviso")), status);

        Assert.Null(await Run(corrector));
    }

    [Theory]
    [InlineData("this is not json")]
    [InlineData("{\"kind\":\"free\"")]
    [InlineData("[]")]
    [InlineData("\"free\"")]
    [InlineData("null")]
    [InlineData("")]
    public async Task Failure_InvalidGeneratedJsonIsNull(string generated)
    {
        var (corrector, _) = Generating(generated);

        Assert.Null(await Run(corrector));
    }

    [Theory]
    [InlineData("""{"modelo":"","cor":"","placa":"","motivo":"","texto":"x"}""")] // no kind
    [InlineData("""{"kind":"other","modelo":"","cor":"","placa":"","motivo":"","texto":"x"}""")] // unknown kind
    [InlineData("""{"kind":3,"modelo":"","cor":"","placa":"","motivo":"","texto":"x"}""")] // kind not a string
    [InlineData("""{"kind":"free","modelo":"","placa":"","motivo":"","texto":"x"}""")] // no cor
    [InlineData("""{"kind":"free","modelo":"","cor":"","placa":"","motivo":""}""")] // no texto
    [InlineData("""{"kind":"vehicle","modelo":"Gol","cor":null,"placa":"","motivo":"","texto":"x"}""")] // null field
    [InlineData("""{"kind":"vehicle","modelo":"Gol","cor":"","placa":5,"motivo":"","texto":"x"}""")] // number field
    public async Task Failure_IncompleteJsonIsNull(string generated)
    {
        var (corrector, _) = Generating(generated);

        Assert.Null(await Run(corrector));
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{}")]
    [InlineData("""{"candidates":[]}""")]
    [InlineData("""{"candidates":[{}]}""")]
    [InlineData("""{"candidates":[{"content":{"parts":[]},"finishReason":"STOP"}]}""")]
    [InlineData("""{"candidates":[{"content":{"parts":[{"inlineData":{}}]},"finishReason":"STOP"}]}""")]
    [InlineData("""{"candidates":"nope"}""")]
    public async Task Failure_AnEnvelopeWithoutUsableTextIsNull(string envelope)
    {
        var (corrector, _) = Answering(envelope);

        Assert.Null(await Run(corrector));
    }

    [Fact]
    public async Task Failure_ANetworkErrorIsNull()
    {
        var (corrector, _) = Make(_ => throw new HttpRequestException("no internet"));

        Assert.Null(await Run(corrector));
    }

    [Fact]
    public async Task Failure_AnHttpClientTimeoutIsNull()
    {
        // The HttpClient's own timeout throws TaskCanceledException while the caller's token is still alive.
        var (corrector, _) = Make(_ => throw new TaskCanceledException("timeout"));

        Assert.Null(await Run(corrector));
    }

    [Fact]
    public async Task Failure_ACallerThatCancelsGetsTheCancellation()
    {
        using var cts = new CancellationTokenSource();
        var (corrector, _) = Make(async ct =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return Json(Envelope(Reply("free", texto: "late")));
        });

        var run = corrector.CorrectAsync("texto", cts.Token);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task Failure_AnAlreadyCancelledTokenThrowsWithoutAnyCall()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var (corrector, handler) = Generating(Reply("free", texto: "Aviso"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => corrector.CorrectAsync("texto", cts.Token));
        Assert.Empty(handler.Calls);
    }

    [Fact]
    public async Task Failure_TheKeyNeverAppearsInTheResultOrBody()
    {
        var (corrector, handler) = Generating(Reply("vehicle", "Gol", "Branco", "abc1234", "farol aceso"));

        var alert = await Run(corrector);

        Assert.DoesNotContain(Key, JsonSerializer.Serialize(alert));
        Assert.DoesNotContain(Key, handler.Calls.Single().Body);
    }

    [Fact]
    public void Constructor_RejectsNulls()
    {
        Assert.Throws<ArgumentNullException>(() => new GeminiCorrector(null!, () => Key, () => "m"));
        Assert.Throws<ArgumentNullException>(() => new GeminiCorrector(new HttpClient(), null!, () => "m"));
        Assert.Throws<ArgumentNullException>(() => new GeminiCorrector(new HttpClient(), () => Key, null!));
    }

    // ---- with the pipeline ----

    private static CorrectionPipeline PipelineOver(GeminiCorrector gemini) =>
        new(gemini, new FakeOffline(), () => FixedRules.Empty, TimeSpan.FromSeconds(3));

    private sealed class FakeOffline : ICorrector
    {
        public Task<ParsedAlert?> CorrectAsync(string raw, CancellationToken ct) =>
            Task.FromResult<ParsedAlert?>(AlertParser.Parse(raw));
    }

    [Fact]
    public async Task Pipeline_TheAnswerOfThePromptExampleGetsThroughTheLock()
    {
        var (corrector, _) = Generating(Reply("vehicle", "Corolla", "Prata", "ABC1D23", "vidro aberto", "Corolla prata ABC1D23 vidro aberto"));

        var result = await PipelineOver(corrector).RunAsync("corola prata abc1d23 vidro abrto", true, CancellationToken.None);

        Assert.Equal(CorrectorKind.Gemini, result.By);
        Assert.True(result.Changed);
        Assert.Equal("COROLLA PRATA | PLACA: ABC-1D23 | VIDRO ABERTO", AlertFormatter.Format(result.Alert));
    }

    [Fact]
    public async Task Pipeline_ARefusalFromGeminiIsRefused()
    {
        var (corrector, _) = Generating(Reply("free", texto: ""));

        var result = await PipelineOver(corrector).RunAsync("some prank", true, CancellationToken.None);

        Assert.True(result.Refused);
    }

    [Fact]
    public async Task Pipeline_ARateLimitFallsBackToOffline()
    {
        var (corrector, _) = Answering("{}", HttpStatusCode.TooManyRequests);

        var result = await PipelineOver(corrector).RunAsync("gol branco abc1234", true, CancellationToken.None);

        Assert.Equal(CorrectorKind.Offline, result.By);
    }
}
