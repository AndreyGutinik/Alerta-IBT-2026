using AlertaIBT.Core.Alerts;
using AlertaIBT.Core.Correction;
using AlertaIBT.Core.Services;
using AlertaIBT.Core.Tests.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace AlertaIBT.Core.Tests.Correction;

// The lines the pipeline and the service leave about Gemini: a timeout, the safety lock saying no, online/offline.
public class PipelineLoggingTests
{
    private const string Raw = "gol branco abc1234 farol aceso";

    private readonly CapturingLogger _log = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 21, 9, 0, 0, TimeSpan.Zero));

    private CorrectionPipeline Pipeline(Func<string, CancellationToken, Task<ParsedAlert?>> gemini, TimeSpan? timeout = null) =>
        new(
            new FakeCorrector(gemini),
            new FakeCorrector((raw, _) => Task.FromResult<ParsedAlert?>(AlertParser.Parse(raw))),
            () => FixedRules.Empty,
            timeout ?? TimeSpan.FromSeconds(5),
            _clock,
            _log,
            () => "gemini-test");

    private static ParsedAlert Ai(string model, string color, string? plate, string reason) =>
        new(model, color, plate, reason, string.Join(' ', new[] { model, color, plate, reason }.Where(s => !string.IsNullOrEmpty(s))));

    private string[] Lines => _log.Entries.Select(entry => entry.Message).ToArray();

    [Fact]
    public async Task TimeoutOfTheCall_IsLogged()
    {
        var pipeline = Pipeline(async (_, ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return null;
        }, TimeSpan.FromMilliseconds(50));

        var result = await pipeline.RunAsync(Raw, true, CancellationToken.None);

        Assert.Equal(CorrectorKind.Offline, result.By);
        Assert.Equal(["Gemini falhou: tempo esgotado (modelo gemini-test)"], Lines);
    }

    [Fact]
    public async Task ACorrectorThatThrows_IsLoggedWithTheTypeOnly()
    {
        var pipeline = Pipeline((_, _) => throw new InvalidOperationException("secret text " + Raw));

        await pipeline.RunAsync(Raw, true, CancellationToken.None);

        Assert.Equal(["Gemini falhou: erro inesperado (InvalidOperationException) (modelo gemini-test)"], Lines);
    }

    [Fact]
    public async Task LockRejection_DifferentPlate_IsLoggedWithoutTheText()
    {
        var pipeline = Pipeline((_, _) => Task.FromResult<ParsedAlert?>(Ai("Gol", "Branco", "XYZ-9876", "farol aceso")));

        var result = await pipeline.RunAsync(Raw, true, CancellationToken.None);

        Assert.Equal(CorrectorKind.Offline, result.By);
        Assert.Equal(["Gemini falhou: trava de seguranca recusou a resposta (placa diferente) (modelo gemini-test)"], Lines);
        Assert.All(Lines, line => Assert.DoesNotContain("XYZ", line));
    }

    [Fact]
    public async Task LockRejection_InventedWord_IsLogged()
    {
        var pipeline = Pipeline((_, _) => Task.FromResult<ParsedAlert?>(Ai("Gol", "Branco", "ABC-1234", "farol aceso urgente")));

        await pipeline.RunAsync(Raw, true, CancellationToken.None);

        Assert.Equal(["Gemini falhou: trava de seguranca recusou a resposta (palavra nova) (modelo gemini-test)"], Lines);
    }

    [Fact]
    public async Task AGoodAnswer_LogsNothing()
    {
        var pipeline = Pipeline((_, _) => Task.FromResult<ParsedAlert?>(Ai("Gol", "Branco", "ABC-1234", "farol aceso")));

        var result = await pipeline.RunAsync(Raw, true, CancellationToken.None);

        Assert.Equal(CorrectorKind.Gemini, result.By);
        Assert.Empty(Lines);
    }

    [Fact]
    public async Task RepeatedLockRejections_AreThrottledToOnePerMinute()
    {
        var pipeline = Pipeline((_, _) => Task.FromResult<ParsedAlert?>(Ai("Gol", "Branco", "XYZ-9876", "farol aceso")));

        for (var i = 0; i < 4; i++)
        {
            await pipeline.RunAsync(Raw + new string('x', i), true, CancellationToken.None);
        }

        Assert.Single(Lines);
        _clock.Advance(TimeSpan.FromSeconds(61));
        await pipeline.RunAsync(Raw + "yy", true, CancellationToken.None);
        Assert.Equal(2, Lines.Length);
    }

    [Fact]
    public async Task Service_LogsTheOnlineOfflineTransitionsOnly()
    {
        var online = false;
        using var h = new ServiceHarness(
            (_, _) => Task.FromResult<ParsedAlert?>(online ? Ai("Gol", "Branco", "ABC-1234", "farol aceso") : null),
            start: false);
        h.Display.SetLed(false);
        h.Service.Start();

        await h.Pc(Raw);
        await h.Pc(Raw + " x");
        online = true;
        await h.Pc(Raw + " y");

        var transitions = h.Logger.Entries.Where(entry => entry.Message.StartsWith("Gemini ", StringComparison.Ordinal)).ToArray();
        Assert.Equal(
            [
                (LogLevel.Warning, "Gemini offline: os avisos usam so o corretor offline"),
                (LogLevel.Information, "Gemini online"),
            ],
            transitions);
    }
}
