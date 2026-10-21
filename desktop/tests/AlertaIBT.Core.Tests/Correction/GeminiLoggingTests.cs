using System.Net;
using System.Text;
using AlertaIBT.Core.Alerts;
using AlertaIBT.Core.Correction;
using AlertaIBT.Core.Tests.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace AlertaIBT.Core.Tests.Correction;

// What Gemini writes in the log (Diagnostico): one line per failure, never the key, throttled to one per reason per minute.
public class GeminiLoggingTests
{
    private const string Key = "AIzaSyTESTKEY-0123456789abcdefghijk";
    private const string Model = "gemini-test";

    private readonly CapturingLogger _log = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 21, 9, 0, 0, TimeSpan.Zero));

    private sealed class Handler(Func<CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => respond(ct);
    }

    private static HttpResponseMessage Reply(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private GeminiCorrector Make(
        Func<CancellationToken, Task<HttpResponseMessage>> respond,
        string? key = Key,
        TimeSpan? httpTimeout = null)
    {
        var http = new HttpClient(new Handler(respond));
        if (httpTimeout is { } timeout)
        {
            http.Timeout = timeout;
        }

        return new GeminiCorrector(http, () => key, () => Model, _log, _clock);
    }

    private GeminiCorrector Answering(HttpStatusCode status, string body) => Make(_ => Task.FromResult(Reply(status, body)));

    private static Task<ParsedAlert?> Run(GeminiCorrector corrector) =>
        corrector.CorrectAsync("gol branco abc1234 farol aceso", CancellationToken.None);

    private string[] Lines => _log.Entries.Select(entry => entry.Message).ToArray();

    [Fact]
    public async Task Http403_LogsTheStatusGoogleSaysAndTheModel()
    {
        var corrector = Answering(
            HttpStatusCode.Forbidden,
            "{\"error\":{\"code\":403,\"message\":\"API key not valid. Please pass a valid API key.\",\"status\":\"PERMISSION_DENIED\"}}");

        Assert.Null(await Run(corrector));

        var entry = Assert.Single(_log.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal(
            "Gemini falhou: HTTP 403 PERMISSION_DENIED: API key not valid. Please pass a valid API key. (modelo gemini-test)",
            entry.Message);
    }

    [Fact]
    public async Task Http429WithoutBody_LogsJustTheCode()
    {
        Assert.Null(await Run(Answering(HttpStatusCode.TooManyRequests, string.Empty)));

        Assert.Equal(["Gemini falhou: HTTP 429 (modelo gemini-test)"], Lines);
    }

    [Fact]
    public async Task Http500WithAnHtmlBody_LogsJustTheCode()
    {
        Assert.Null(await Run(Answering(HttpStatusCode.InternalServerError, "<html>oops</html>")));

        Assert.Equal(["Gemini falhou: HTTP 500 (modelo gemini-test)"], Lines);
    }

    [Fact]
    public async Task InvalidJsonBody_IsLogged()
    {
        Assert.Null(await Run(Answering(HttpStatusCode.OK, "this is not json")));

        Assert.Equal(["Gemini falhou: resposta com JSON invalido (modelo gemini-test)"], Lines);
    }

    [Fact]
    public async Task JsonWithoutUsableAnswer_IsLogged()
    {
        Assert.Null(await Run(Answering(HttpStatusCode.OK, "{\"candidates\":[]}")));

        Assert.Equal(["Gemini falhou: resposta em formato inesperado (modelo gemini-test)"], Lines);
    }

    [Fact]
    public async Task NoKey_IsLogged()
    {
        Assert.Null(await Run(Make(_ => throw new InvalidOperationException("must not be called"), key: " ")));

        Assert.Equal(["Gemini falhou: sem chave de API (modelo gemini-test)"], Lines);
    }

    [Fact]
    public async Task NetworkError_LogsTheExceptionTypeOnly()
    {
        var corrector = Make(_ => throw new HttpRequestException("Name or service not known (generativelanguage.googleapis.com:443)"));

        Assert.Null(await Run(corrector));

        Assert.Equal(["Gemini falhou: erro de rede (HttpRequestException) (modelo gemini-test)"], Lines);
    }

    [Fact]
    public async Task HttpClientTimeout_IsLoggedAsTimeout()
    {
        var corrector = Make(
            async ct =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return Reply(HttpStatusCode.OK, "{}");
            },
            httpTimeout: TimeSpan.FromMilliseconds(50));

        Assert.Null(await Run(corrector));

        Assert.Equal(["Gemini falhou: tempo esgotado (modelo gemini-test)"], Lines);
    }

    [Fact]
    public async Task CallerCancellation_LogsNothing()
    {
        using var cts = new CancellationTokenSource();
        var corrector = Make(async ct =>
        {
            await cts.CancelAsync();
            ct.ThrowIfCancellationRequested();
            return Reply(HttpStatusCode.OK, "{}");
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => corrector.CorrectAsync("gol", cts.Token));

        Assert.Empty(Lines);
    }

    [Theory]
    [InlineData("{\"promptFeedback\":{\"blockReason\":\"SAFETY\"}}", "Gemini: recusou o texto (blockReason SAFETY) (modelo gemini-test)")]
    [InlineData("{\"candidates\":[{\"finishReason\":\"PROHIBITED_CONTENT\"}]}", "Gemini: recusou o texto (finishReason PROHIBITED_CONTENT) (modelo gemini-test)")]
    public async Task SafetyRefusal_IsLoggedAsInformation(string body, string expected)
    {
        var result = await Run(Answering(HttpStatusCode.OK, body));

        Assert.NotNull(result);
        var entry = Assert.Single(_log.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Equal(expected, entry.Message);
    }

    [Fact]
    public async Task TheKeyNeverAppearsInTheLog_EvenWhenGoogleEchoesIt()
    {
        var echoes = Answering(
            HttpStatusCode.BadRequest,
            "{\"error\":{\"message\":\"Bad key " + Key + " sent\",\"status\":\"INVALID_ARGUMENT\"}}");
        var network = Make(_ => throw new HttpRequestException("https://x/?key=" + Key));
        var broken = Answering(HttpStatusCode.OK, Key + " is not json");

        await Run(echoes);
        await Run(network);
        await Run(broken);

        Assert.NotEmpty(Lines);
        Assert.All(Lines, line => Assert.DoesNotContain(Key, line));
        Assert.All(Lines, line => Assert.DoesNotContain("abc1234", line));
        Assert.Contains("Gemini falhou: HTTP 400 INVALID_ARGUMENT: Bad key *** sent (modelo gemini-test)", Lines);
    }

    [Fact]
    public async Task LongGoogleMessage_IsCutAndKeptOnOneLine()
    {
        var message = string.Join("\n", Enumerable.Repeat("campo invalido no corpo", 40));
        var corrector = Answering(
            HttpStatusCode.BadRequest,
            "{\"error\":{\"message\":" + System.Text.Json.JsonSerializer.Serialize(message) + ",\"status\":\"INVALID_ARGUMENT\"}}");

        await Run(corrector);

        var line = Assert.Single(Lines);
        Assert.DoesNotContain('\n', line);
        Assert.True(line.Length < 260, line);
    }

    // ---- throttle ----

    [Fact]
    public async Task SameReason_IsLoggedOncePerMinute()
    {
        var corrector = Answering(HttpStatusCode.TooManyRequests, string.Empty);

        for (var i = 0; i < 5; i++)
        {
            await Run(corrector);
        }

        Assert.Single(Lines);

        _clock.Advance(TimeSpan.FromSeconds(59));
        await Run(corrector);
        Assert.Single(Lines);

        _clock.Advance(TimeSpan.FromSeconds(2));
        await Run(corrector);
        Assert.Equal(2, Lines.Length);
    }

    [Fact]
    public async Task DifferentReasons_AreThrottledSeparately()
    {
        var status = HttpStatusCode.TooManyRequests;
        var corrector = Make(_ => Task.FromResult(Reply(status, string.Empty)));

        await Run(corrector);
        status = HttpStatusCode.Forbidden;
        await Run(corrector);
        await Run(corrector);

        Assert.Equal(["Gemini falhou: HTTP 429 (modelo gemini-test)", "Gemini falhou: HTTP 403 (modelo gemini-test)"], Lines);
    }

    [Fact]
    public async Task ASuccessfulAnswer_LogsNothing()
    {
        const string body = "{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"{\\\"kind\\\":\\\"free\\\",\\\"modelo\\\":\\\"\\\",\\\"cor\\\":\\\"\\\",\\\"placa\\\":\\\"\\\",\\\"motivo\\\":\\\"\\\",\\\"texto\\\":\\\"ok\\\"}\"}]},\"finishReason\":\"STOP\"}]}";

        Assert.NotNull(await Run(Answering(HttpStatusCode.OK, body)));

        Assert.Empty(Lines);
    }
}
