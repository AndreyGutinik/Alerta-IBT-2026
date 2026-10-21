using AlertaIBT.Core.Alerts;
using AlertaIBT.Core.Correction;

namespace AlertaIBT.Core.Tests.Correction;

// Gemini may take up to 5 s in the app (the first call after a quiet spell is slow). The tests scale the clock down:
// a limit of 400 ms, an answer at 150 ms (like 4 s of 5) and one at 1,200 ms (like more than 5 s).
public class GeminiTimeoutTests
{
    private const string Raw = "gol branco abc1234 farol aceso";

    private static readonly TimeSpan Limit = TimeSpan.FromMilliseconds(400);

    private sealed class SlowGemini(TimeSpan delay) : ICorrector
    {
        public async Task<ParsedAlert?> CorrectAsync(string raw, CancellationToken ct)
        {
            await Task.Delay(delay, ct);
            return new ParsedAlert("Gol", "Branco", "ABC-1234", "farol aceso", "Gol Branco ABC-1234 farol aceso");
        }
    }

    private sealed class Offline : ICorrector
    {
        public Task<ParsedAlert?> CorrectAsync(string raw, CancellationToken ct) =>
            Task.FromResult<ParsedAlert?>(AlertParser.Parse(raw));
    }

    private static CorrectionPipeline Pipeline(TimeSpan geminiTakes) =>
        new(new SlowGemini(geminiTakes), new Offline(), () => FixedRules.Empty, Limit);

    [Fact]
    public void The_limit_used_by_the_app_is_five_seconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(5), CorrectionPipeline.DefaultGeminiTimeout);
    }

    [Fact]
    public void The_http_client_waits_longer_than_the_pipeline_does()
    {
        Assert.True(GeminiHttp.RequestTimeout > CorrectionPipeline.DefaultGeminiTimeout);
        using var client = GeminiHttp.CreateClient();
        Assert.Equal(GeminiHttp.RequestTimeout, client.Timeout);
    }

    [Fact]
    public async Task An_answer_inside_the_limit_counts_as_Gemini()
    {
        var result = await Pipeline(TimeSpan.FromMilliseconds(150)).RunAsync(Raw, useGemini: true, CancellationToken.None);

        Assert.Equal(CorrectorKind.Gemini, result.By);
    }

    [Fact]
    public async Task An_answer_past_the_limit_falls_back_to_the_offline_corrector()
    {
        var result = await Pipeline(TimeSpan.FromMilliseconds(1200)).RunAsync(Raw, useGemini: true, CancellationToken.None);

        Assert.Equal(CorrectorKind.Offline, result.By);
    }
}
