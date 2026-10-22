using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using AlertaIBT.Core.Correction;
using AlertaIBT.Core.Tests.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace AlertaIBT.Core.Tests.Correction;

// The startup/idle probe: one tiny real generateContent through the corrector's own client, never a throw, never a secret.
public class GeminiHealthProbeTests
{
    private const string Key = "AIzaSyPROBEKEY-0123456789abcdefghijk";
    private const string Model = "gemini-3.5-flash-lite";

    private readonly CapturingLogger _log = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 22, 9, 0, 0, TimeSpan.Zero));

    private sealed record Call(HttpMethod Method, Uri Uri, string? KeyHeader, string Body);

    private sealed class Handler(Func<CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
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

    private static HttpResponseMessage Reply(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static string Ok(string finish = "STOP", bool withText = true)
    {
        JsonObject candidate = new() { ["finishReason"] = finish };
        if (withText)
        {
            candidate["content"] = new JsonObject
            {
                ["role"] = "model",
                ["parts"] = new JsonArray(new JsonObject { ["text"] = "ok" }),
            };
        }

        return new JsonObject { ["candidates"] = new JsonArray(candidate) }.ToJsonString();
    }

    private (GeminiHealthProbe Probe, Handler Handler) Make(
        Func<CancellationToken, Task<HttpResponseMessage>> respond,
        string? key = Key,
        string model = Model)
    {
        var handler = new Handler(respond);
        return (new GeminiHealthProbe(new HttpClient(handler), () => key, () => model, _log, _clock), handler);
    }

    private (GeminiHealthProbe Probe, Handler Handler) Answering(HttpStatusCode status, string body, string? key = Key) =>
        Make(_ => Task.FromResult(Reply(status, body)), key);

    // ---- success ----

    [Fact]
    public async Task Success_IsOnlineAndLogsOneInformationLineWithoutSecrets()
    {
        var (probe, handler) = Answering(HttpStatusCode.OK, Ok());

        var result = await probe.ProbeAsync(CancellationToken.None);

        Assert.Equal(GeminiProbeStatus.Online, result.Status);
        Assert.Equal(GeminiFailureKind.None, result.Failure);
        Assert.Single(handler.Calls);
        var entry = Assert.Single(_log.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Equal("Gemini: conectado (modelo gemini-3.5-flash-lite, 0 ms)", entry.Message);
        Assert.DoesNotContain(Key, entry.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("MAX_TOKENS", false)]
    [InlineData("STOP", true)]
    public async Task ASmallAnswerCutByTheTokenLimitStillProvesTheConnection(string finish, bool withText)
    {
        var (probe, _) = Answering(HttpStatusCode.OK, Ok(finish, withText));

        Assert.Equal(GeminiProbeStatus.Online, (await probe.ProbeAsync(CancellationToken.None)).Status);
    }

    // ---- the request ----

    [Fact]
    public async Task Request_IsMinimalAndCarriesTheKeyOnlyInTheHeader()
    {
        var (probe, handler) = Answering(HttpStatusCode.OK, Ok());

        await probe.ProbeAsync(CancellationToken.None);

        var call = Assert.Single(handler.Calls);
        Assert.Equal(HttpMethod.Post, call.Method);
        Assert.Equal(
            "https://generativelanguage.googleapis.com/v1beta/models/gemini-3.5-flash-lite:generateContent",
            call.Uri.ToString());
        Assert.Equal(Key, call.KeyHeader);
        Assert.DoesNotContain(Key, call.Uri.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Key, call.Body, StringComparison.Ordinal);

        var body = JsonNode.Parse(call.Body)!.AsObject();
        Assert.Equal("ok", body["contents"]![0]!["parts"]![0]!["text"]!.GetValue<string>());
        Assert.Null(body["systemInstruction"]);
        var config = body["generationConfig"]!.AsObject();
        Assert.InRange(config["maxOutputTokens"]!.GetValue<int>(), 1, 8);
        Assert.Equal(0, config["temperature"]!.GetValue<double>());
        Assert.Equal("MINIMAL", config["thinkingConfig"]!["thinkingLevel"]!.GetValue<string>());
        Assert.Null(config["responseJsonSchema"]);
    }

    [Fact]
    public async Task Request_UsesTheModelOfTheSettingsAndTheCorrectorsThinkingRule()
    {
        var (probe, handler) = Make(_ => Task.FromResult(Reply(HttpStatusCode.OK, Ok())), model: "gemini-2.5-flash");

        await probe.ProbeAsync(CancellationToken.None);

        var call = Assert.Single(handler.Calls);
        Assert.EndsWith("/gemini-2.5-flash:generateContent", call.Uri.AbsolutePath, StringComparison.Ordinal);
        Assert.Equal(0, JsonNode.Parse(call.Body)!["generationConfig"]!["thinkingConfig"]!["thinkingBudget"]!.GetValue<int>());
    }

    // ---- no key ----

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task NoKey_IsNoKeyAndNoRequestIsSent(string? key)
    {
        var (probe, handler) = Answering(HttpStatusCode.OK, Ok(), key);

        var result = await probe.ProbeAsync(CancellationToken.None);

        Assert.Equal(GeminiProbeStatus.NoKey, result.Status);
        Assert.Empty(handler.Calls);
    }

    // ---- failures ----

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, 401)]
    [InlineData(HttpStatusCode.Forbidden, 403)]
    [InlineData(HttpStatusCode.TooManyRequests, 429)]
    [InlineData(HttpStatusCode.InternalServerError, 500)]
    public async Task HttpErrors_AreFailedWithTheStatusInTheReason(HttpStatusCode status, int code)
    {
        var (probe, _) = Answering(status, string.Empty);

        var result = await probe.ProbeAsync(CancellationToken.None);

        Assert.Equal(GeminiProbeStatus.Failed, result.Status);
        Assert.Equal(GeminiFailureKind.Http, result.Failure);
        Assert.Equal(code, result.HttpStatus);
        Assert.Equal($"HTTP {code}", result.Reason);
    }

    [Fact]
    public async Task Http403_ReasonCarriesGooglesStatusAndMessageButNeverTheKey()
    {
        var (probe, _) = Answering(
            HttpStatusCode.Forbidden,
            $"{{\"error\":{{\"code\":403,\"message\":\"API key {Key} not valid.\",\"status\":\"PERMISSION_DENIED\"}}}}");

        var result = await probe.ProbeAsync(CancellationToken.None);

        Assert.Equal("HTTP 403 PERMISSION_DENIED: API key *** not valid.", result.Reason);
        var entry = Assert.Single(_log.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal("Gemini falhou: HTTP 403 PERMISSION_DENIED: API key *** not valid. (modelo gemini-3.5-flash-lite)", entry.Message);
        Assert.DoesNotContain(Key, entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidJson_IsFailedInvalidResponse()
    {
        var (probe, _) = Answering(HttpStatusCode.OK, "not json {");

        var result = await probe.ProbeAsync(CancellationToken.None);

        Assert.Equal(GeminiProbeStatus.Failed, result.Status);
        Assert.Equal(GeminiFailureKind.InvalidResponse, result.Failure);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"candidates\":[]}")]
    public async Task AnswerWithoutACandidate_IsFailedInvalidResponse(string body)
    {
        var (probe, _) = Answering(HttpStatusCode.OK, body);

        var result = await probe.ProbeAsync(CancellationToken.None);

        Assert.Equal(GeminiFailureKind.InvalidResponse, result.Failure);
    }

    [Theory]
    [InlineData("{\"promptFeedback\":{\"blockReason\":\"SAFETY\"}}")]
    [InlineData("{\"candidates\":[{\"finishReason\":\"SAFETY\"}]}")]
    public async Task BlockedAnswer_IsFailedSafety(string body)
    {
        var (probe, _) = Answering(HttpStatusCode.OK, body);

        var result = await probe.ProbeAsync(CancellationToken.None);

        Assert.Equal(GeminiProbeStatus.Failed, result.Status);
        Assert.Equal(GeminiFailureKind.Safety, result.Failure);
    }

    [Fact]
    public async Task NetworkError_IsFailedNetworkWithTypeNamesOnly()
    {
        var (probe, _) = Make(_ => throw new HttpRequestException("secret host name.example", new IOException("x")));

        var result = await probe.ProbeAsync(CancellationToken.None);

        Assert.Equal(GeminiFailureKind.Network, result.Failure);
        Assert.Equal("erro de rede (HttpRequestException/IOException)", result.Reason);
        Assert.DoesNotContain("secret", _log.Entries[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SlowAnswer_TimesOutAfterTwentySecondsAsFailedTimeout()
    {
        var (probe, _) = Make(async ct =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return Reply(HttpStatusCode.OK, Ok());
        });

        var task = probe.ProbeAsync(CancellationToken.None);
        _clock.Advance(TimeSpan.FromSeconds(19));
        Assert.False(task.IsCompleted);
        _clock.Advance(TimeSpan.FromSeconds(2));
        var result = await task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(GeminiFailureKind.Timeout, result.Failure);
        Assert.Equal("tempo esgotado", result.Reason);
    }

    [Fact]
    public async Task TheClientsOwnTimeout_IsAlsoATimeout()
    {
        var (probe, _) = Make(_ => throw new TaskCanceledException("client timeout"));

        var result = await probe.ProbeAsync(CancellationToken.None);

        Assert.Equal(GeminiFailureKind.Timeout, result.Failure);
    }

    [Fact]
    public async Task CallerCancellation_DoesNotThrowAndIsMarkedCancelled()
    {
        using var cts = new CancellationTokenSource();
        var (probe, _) = Make(async ct =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return Reply(HttpStatusCode.OK, Ok());
        });

        var task = probe.ProbeAsync(cts.Token);
        await cts.CancelAsync();
        var result = await task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(GeminiFailureKind.Cancelled, result.Failure);
    }

    [Fact]
    public async Task RepeatedFailuresOfTheSameReason_AreLoggedOncePerMinute()
    {
        var (probe, _) = Answering(HttpStatusCode.TooManyRequests, string.Empty);

        await probe.ProbeAsync(CancellationToken.None);
        _clock.Advance(TimeSpan.FromSeconds(30));
        await probe.ProbeAsync(CancellationToken.None);
        _clock.Advance(TimeSpan.FromSeconds(31));
        await probe.ProbeAsync(CancellationToken.None);

        Assert.Equal(2, _log.Entries.Length);
    }
}
