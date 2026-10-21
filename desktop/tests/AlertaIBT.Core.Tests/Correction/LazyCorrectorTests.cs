using AlertaIBT.Core.Alerts;
using AlertaIBT.Core.Correction;

namespace AlertaIBT.Core.Tests.Correction;

public class LazyCorrectorTests
{
    private sealed class Echo : ICorrector
    {
        public Task<ParsedAlert?> CorrectAsync(string raw, CancellationToken ct) => Task.FromResult<ParsedAlert?>(AlertParser.Parse(raw));
    }

    [Fact]
    public async Task The_factory_runs_only_when_needed_and_only_once_even_with_many_callers()
    {
        var builds = 0;
        var lazy = new LazyCorrector(() =>
        {
            Interlocked.Increment(ref builds);
            Thread.Sleep(30);
            return new Echo();
        });
        Assert.Equal(0, builds);

        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => lazy.CorrectAsync("gol branco abc1234", TestContext.Current.CancellationToken)));

        Assert.Equal(1, builds);
        Assert.All(results, r => Assert.Equal("ABC-1234", r!.Plate));
        Assert.Null(lazy.LoadError);
    }

    [Fact]
    public async Task Warm_builds_in_the_background_without_a_caller()
    {
        var built = new TaskCompletionSource();
        var lazy = new LazyCorrector(() =>
        {
            built.SetResult();
            return new Echo();
        });

        lazy.Warm();

        await built.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_factory_that_fails_fails_every_call_the_same_way_and_keeps_the_error()
    {
        var builds = 0;
        var lazy = new LazyCorrector(() =>
        {
            builds++;
            throw new FileNotFoundException("dictionary missing");
        });

        await Assert.ThrowsAsync<FileNotFoundException>(() => lazy.CorrectAsync("x", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<FileNotFoundException>(() => lazy.CorrectAsync("x", TestContext.Current.CancellationToken));

        Assert.Equal(1, builds);
        Assert.IsType<FileNotFoundException>(lazy.LoadError);
        lazy.Warm(); // does not throw either
    }

    [Fact]
    public async Task Through_the_pipeline_a_missing_dictionary_means_the_plain_parser()
    {
        var offline = new LazyCorrector(() => throw new FileNotFoundException("dictionary missing"));
        var gemini = new FakeNullCorrector();
        var pipeline = new CorrectionPipeline(gemini, offline, () => FixedRules.Empty, TimeSpan.FromSeconds(3));

        var result = await pipeline.RunAsync("gol branco abc1234 farol aceso", useGemini: false, TestContext.Current.CancellationToken);

        Assert.Equal("ABC-1234", result.Alert.Plate);
        Assert.Equal(CorrectorKind.Offline, result.By);
    }

    private sealed class FakeNullCorrector : ICorrector
    {
        public Task<ParsedAlert?> CorrectAsync(string raw, CancellationToken ct) => Task.FromResult<ParsedAlert?>(null);
    }
}
