using AlertaIBT.Core.Alerts;
using AlertaIBT.Core.Correction;
using AlertaIBT.Core.Tests.Services;
using Microsoft.Extensions.Logging;

namespace AlertaIBT.Core.Tests.Correction;

// A dictionary that cannot be loaded (antivirus, damaged install) must be loud in the log and in the status, and harmless to alerts.
public class LazyCorrectorLoadFailureTests
{
    private readonly string _missing = Path.Combine(Path.GetTempPath(), "alerta-ibt-no-dictionary-" + Guid.NewGuid().ToString("N"));
    private readonly CapturingLogger _log = new();
    private readonly List<Exception> _reported = [];

    private LazyCorrector Make() =>
        new(() => OfflineCorrector.Load(_missing), _log, _missing, _reported.Add);

    private static async Task WaitUntil(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < until)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task AMissingDirectory_LogsOneClearErrorLineWithThePathAndTheException()
    {
        var corrector = Make();

        corrector.Warm();
        await WaitUntil(() => _log.Entries.Length > 0);

        var entry = Assert.Single(_log.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Contains("dicionario offline", entry.Message, StringComparison.Ordinal);
        Assert.Contains(Path.Combine(_missing, "pt_BR.aff"), entry.Message, StringComparison.Ordinal);
        Assert.Contains("FileNotFoundException", entry.Message, StringComparison.Ordinal);
        Assert.Contains("Dictionary file not found", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheFailureIsLoggedAndReportedOnlyOnce_NoMatterHowManyCorrections()
    {
        var corrector = Make();

        for (var i = 0; i < 5; i++)
        {
            await Assert.ThrowsAsync<FileNotFoundException>(() => corrector.CorrectAsync("gol branco abc1234", CancellationToken.None));
        }

        corrector.Warm();
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Single(_log.Entries);
        Assert.Single(_reported);
        Assert.IsType<FileNotFoundException>(_reported[0]);
        Assert.Same(_reported[0], corrector.LoadError);
    }

    [Fact]
    public async Task AlertsKeepWorking_WithTheParseOnlyFallback()
    {
        var pipeline = new CorrectionPipeline(
            new FakeCorrector((_, _) => Task.FromResult<ParsedAlert?>(null)),
            Make(),
            () => FixedRules.Empty,
            TimeSpan.FromSeconds(5));

        var result = await pipeline.RunAsync("gol branco abc1234 farol acesso", useGemini: false, CancellationToken.None);

        Assert.Equal(CorrectorKind.Offline, result.By);
        Assert.False(result.Refused);
        Assert.Equal("ABC-1234", result.Alert.Plate);
        Assert.Equal("FAROL ACESSO", result.Alert.Reason); // parsed, not spell-checked
    }

    [Fact]
    public async Task ACallbackThatThrows_NeverBreaksTheFallback()
    {
        var corrector = new LazyCorrector(() => OfflineCorrector.Load(_missing), _log, _missing, _ => throw new InvalidOperationException("listener bug"));

        await Assert.ThrowsAsync<FileNotFoundException>(() => corrector.CorrectAsync("gol", CancellationToken.None));

        Assert.Single(_log.Entries);
    }

    [Fact]
    public async Task ASuccessfulLoad_LogsNothing()
    {
        var corrector = new LazyCorrector(
            () => OfflineCorrector.Load(Path.Combine(AppContext.BaseDirectory, "Dictionaries")),
            _log,
            "ok",
            _reported.Add);

        var alert = await corrector.CorrectAsync("corolla prata abc1d23 vidro abrto", CancellationToken.None);

        Assert.Equal("VIDRO ABERTO", alert!.Reason);
        Assert.Empty(_log.Entries);
        Assert.Empty(_reported);
        Assert.Null(corrector.LoadError);
    }
}
