using AlertaIBT.Core.Alerts;
using AlertaIBT.Core.Correction;
using Microsoft.Extensions.Time.Testing;

namespace AlertaIBT.Core.Tests.Correction;

public class CorrectionPipelineTests
{
    private const string VehicleRaw = "gol branco abc1234 farol acesso";

    private static readonly TimeSpan LongTimeout = TimeSpan.FromSeconds(5);

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 20, 9, 0, 0, TimeSpan.Zero));
    private readonly List<bool> _health = [];

    // ---- helpers ----

    private sealed class FakeCorrector(Func<string, CancellationToken, Task<ParsedAlert?>> run) : ICorrector
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public Task<ParsedAlert?> CorrectAsync(string raw, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            return run(raw, ct);
        }
    }

    private static FakeCorrector Returns(ParsedAlert? alert) => new((_, _) => Task.FromResult(alert));

    private static FakeCorrector Throws(Exception error) => new((_, _) => throw error);

    // Offline stand-in: the plain parser, after fixing the typo the tests like to use.
    private static FakeCorrector OfflineFake() =>
        new((raw, _) => Task.FromResult<ParsedAlert?>(AlertParser.Parse(raw.Replace("acesso", "aceso"))));

    private static ParsedAlert Ai(string model, string color, string? plate, string reason, string? free = null) =>
        new(model, color, plate, reason, free ?? string.Join(' ', new[] { model, color, plate, reason }.Where(s => !string.IsNullOrEmpty(s))));

    private static ParsedAlert GoodAi() => Ai("Gol", "Branco", "ABC-1234", "farol aceso");

    private CorrectionPipeline Pipeline(
        ICorrector gemini,
        ICorrector? offline = null,
        FixedRules? rules = null,
        TimeSpan? timeout = null)
    {
        var pipeline = new CorrectionPipeline(
            gemini,
            offline ?? OfflineFake(),
            () => rules ?? FixedRules.Empty,
            timeout ?? LongTimeout,
            _clock);
        pipeline.GeminiHealthChanged += _health.Add;
        return pipeline;
    }

    // ---- R1: order ----

    [Fact]
    public async Task Run_GeminiAnswers_ByGemini()
    {
        var gemini = Returns(GoodAi());
        var offline = OfflineFake();

        var result = await Pipeline(gemini, offline).RunAsync(VehicleRaw, true, CancellationToken.None);

        Assert.Equal(CorrectorKind.Gemini, result.By);
        Assert.False(result.Refused);
        Assert.Equal("Gol", result.Alert.Model);
        Assert.Equal("ABC-1234", result.Alert.Plate);
        Assert.Equal(1, gemini.Calls);
        Assert.Equal(0, offline.Calls);
        Assert.Equal([true], _health);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n \t")]
    public async Task Run_EmptyRaw_SkipsEveryCorrectorAndRaisesNoEvent(string raw)
    {
        var gemini = Returns(GoodAi());
        var offline = OfflineFake();

        var result = await Pipeline(gemini, offline).RunAsync(raw, true, CancellationToken.None);

        Assert.Equal(AlertParser.Parse(raw), result.Alert);
        Assert.Equal(CorrectorKind.Offline, result.By);
        Assert.False(result.Refused);
        Assert.False(result.Changed);
        Assert.Equal(0, gemini.Calls);
        Assert.Equal(0, offline.Calls);
        Assert.Empty(_health);
    }

    [Fact]
    public async Task Run_UseGeminiFalse_GoesStraightToOfflineAndLeavesHealthAlone()
    {
        var gemini = Returns(GoodAi());
        var offline = OfflineFake();

        var result = await Pipeline(gemini, offline).RunAsync(VehicleRaw, false, CancellationToken.None);

        Assert.Equal(CorrectorKind.Offline, result.By);
        Assert.Equal("FAROL ACESO", result.Alert.Reason);
        Assert.Equal(0, gemini.Calls);
        Assert.Equal(1, offline.Calls);
        Assert.Empty(_health);
    }

    [Fact]
    public async Task Run_GeminiReturnsNull_FallsBackToOfflineAndReportsOffline()
    {
        var result = await Pipeline(Returns(null)).RunAsync(VehicleRaw, true, CancellationToken.None);

        Assert.Equal(CorrectorKind.Offline, result.By);
        Assert.Equal("FAROL ACESO", result.Alert.Reason);
        Assert.Equal([false], _health);
    }

    [Fact]
    public async Task Run_GeminiThrows_FallsBackToOfflineAndReportsOffline()
    {
        var gemini = Throws(new HttpRequestException("no internet"));

        var result = await Pipeline(gemini).RunAsync(VehicleRaw, true, CancellationToken.None);

        Assert.Equal(CorrectorKind.Offline, result.By);
        Assert.Equal([false], _health);
    }

    [Fact]
    public async Task Run_GeminiFaultsAfterAwait_FallsBackToOffline()
    {
        var gemini = new FakeCorrector(async (_, _) =>
        {
            await Task.Yield();
            throw new InvalidOperationException("boom");
        });

        var result = await Pipeline(gemini).RunAsync(VehicleRaw, true, CancellationToken.None);

        Assert.Equal(CorrectorKind.Offline, result.By);
        Assert.Equal([false], _health);
    }

    [Fact]
    public async Task Run_GeminiTooSlowAndHonoringTheToken_FallsBackToOffline()
    {
        var gemini = new FakeCorrector(async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return GoodAi();
        });

        var result = await Pipeline(gemini, timeout: TimeSpan.FromMilliseconds(50))
            .RunAsync(VehicleRaw, true, CancellationToken.None);

        Assert.Equal(CorrectorKind.Offline, result.By);
        Assert.Equal([false], _health);
    }

    [Fact]
    public async Task Run_GeminiTooSlowAndIgnoringTheToken_StillFallsBackWithinTheTimeout()
    {
        var never = new TaskCompletionSource<ParsedAlert?>();
        var gemini = new FakeCorrector((_, _) => never.Task);

        var started = System.Diagnostics.Stopwatch.StartNew();
        var result = await Pipeline(gemini, timeout: TimeSpan.FromMilliseconds(50))
            .RunAsync(VehicleRaw, true, CancellationToken.None);

        Assert.Equal(CorrectorKind.Offline, result.By);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(3));
        Assert.Equal([false], _health);
    }

    [Fact]
    public async Task Run_OfflineReturnsNull_UsesThePlainParser()
    {
        var result = await Pipeline(Returns(null), Returns(null)).RunAsync("gol branco abc1234 farol aceso", false, CancellationToken.None);

        Assert.Equal(AlertParser.Parse("gol branco abc1234 farol aceso"), result.Alert);
        Assert.Equal(CorrectorKind.Offline, result.By);
    }

    [Fact]
    public async Task Run_OfflineThrows_UsesThePlainParser()
    {
        var result = await Pipeline(Returns(null), Throws(new InvalidOperationException("dictionary"))).RunAsync("gol branco abc1234 farol aceso", false, CancellationToken.None);

        Assert.Equal(AlertParser.Parse("gol branco abc1234 farol aceso"), result.Alert);
        Assert.Equal(CorrectorKind.Offline, result.By);
    }

    // ---- R1.4: refusal ----

    [Fact]
    public async Task Run_GeminiRefuses_IsRefusedAndNeverReachesOffline()
    {
        var gemini = Returns(new ParsedAlert("", "", null, "", ""));
        var offline = OfflineFake();

        var result = await Pipeline(gemini, offline).RunAsync("some offensive prank", true, CancellationToken.None);

        Assert.True(result.Refused);
        Assert.Equal(CorrectorKind.Gemini, result.By);
        Assert.False(result.Changed);
        Assert.Equal(0, offline.Calls);
        Assert.Equal([true], _health);
    }

    [Fact]
    public async Task Run_RefusalIsCheckedBeforeTheSafetyLock()
    {
        // The raw has a plate and the refusal has none: the lock would fail, but the refusal wins.
        var gemini = Returns(new ParsedAlert("", "", null, "", ""));
        var offline = OfflineFake();

        var result = await Pipeline(gemini, offline).RunAsync("abc1234 rude words", true, CancellationToken.None);

        Assert.True(result.Refused);
        Assert.Equal(0, offline.Calls);
    }

    [Fact]
    public async Task Run_RefusalSkipsFixedRules()
    {
        var rules = FixedRules.Parse("x => y");
        var gemini = Returns(new ParsedAlert("", "", null, "", ""));

        var result = await Pipeline(gemini, rules: rules).RunAsync("prank", true, CancellationToken.None);

        Assert.Equal(new ParsedAlert("", "", null, "", ""), result.Alert);
    }

    [Fact]
    public async Task Run_AnswerWithOnlyAFreeTextOrAPlate_IsNotARefusal()
    {
        var onlyPlate = Returns(new ParsedAlert("", "", "ABC-1234", "", "ABC-1234"));
        var onlyText = Returns(new ParsedAlert("", "", null, "", "Culto hoje as 19h"));

        var plate = await Pipeline(onlyPlate).RunAsync("abc1234", true, CancellationToken.None);
        var text = await Pipeline(onlyText).RunAsync("culto hoje as 19h", true, CancellationToken.None);

        Assert.False(plate.Refused);
        Assert.False(text.Refused);
    }

    // ---- R3: safety lock ----

    [Fact]
    public async Task Run_GeminiChangesThePlate_UsesOffline()
    {
        var gemini = Returns(Ai("Gol", "Branco", "ABC-9999", "farol aceso"));

        var result = await Pipeline(gemini).RunAsync("gol branco abc1234 farol acesso", true, CancellationToken.None);

        Assert.Equal(CorrectorKind.Offline, result.By);
        Assert.Equal("ABC-1234", result.Alert.Plate);
    }

    [Fact]
    public async Task Run_RawHasAPlateAndGeminiHasNone_UsesOffline()
    {
        var gemini = Returns(Ai("Gol", "Branco", null, "farol aceso"));

        var result = await Pipeline(gemini).RunAsync(VehicleRaw, true, CancellationToken.None);

        Assert.Equal(CorrectorKind.Offline, result.By);
        Assert.Equal("ABC-1234", result.Alert.Plate);
    }

    [Fact]
    public async Task Run_RawHasNoPlateAndGeminiInventsOne_UsesOffline()
    {
        var gemini = Returns(Ai("Gol", "Branco", "ABC-1234", "farol aceso"));

        var result = await Pipeline(gemini).RunAsync("gol branco farol acesso", true, CancellationToken.None);

        Assert.Equal(CorrectorKind.Offline, result.By);
        Assert.Null(result.Alert.Plate);
    }

    [Fact]
    public async Task Run_BothWithoutAPlate_Passes()
    {
        var gemini = Returns(Ai("Gol", "Branco", null, "farol aceso"));

        var result = await Pipeline(gemini).RunAsync("gol branco farol acesso", true, CancellationToken.None);

        Assert.Equal(CorrectorKind.Gemini, result.By);
        Assert.Null(result.Alert.Plate);
    }

    [Fact]
    public async Task Run_PlateTypedLooselyIsComparedInDisplayForm()
    {
        var gemini = Returns(Ai("Gol", "Branco", "abc1d23", "farol aceso"));

        var result = await Pipeline(gemini).RunAsync("gol branco abc1d23 farol acesso", true, CancellationToken.None);

        Assert.Equal(CorrectorKind.Gemini, result.By);
        Assert.Equal("ABC-1D23", result.Alert.Plate);
    }

    [Fact]
    public async Task Run_GeminiPlateInWrongFormButSamePlate_CarriesTheDisplayForm()
    {
        var gemini = Returns(Ai("Gol", "Branco", " abc 1234 ", "farol aceso", "Gol Branco ABC-1234 farol aceso"));

        var result = await Pipeline(gemini).RunAsync(VehicleRaw, true, CancellationToken.None);

        Assert.Equal(CorrectorKind.Gemini, result.By);
        Assert.Equal("ABC-1234", result.Alert.Plate);
    }

    [Fact]
    public async Task Run_GeminiInventsAModel_UsesOffline()
    {
        var gemini = Returns(Ai("Corolla", "Branco", "ABC-1234", "farol aceso"));

        var result = await Pipeline(gemini).RunAsync("gol branco abc1234", true, CancellationToken.None);

        Assert.Equal(CorrectorKind.Offline, result.By);
        Assert.Equal("GOL", result.Alert.Model);
    }

    [Fact]
    public async Task Run_GeminiInventsAReasonOrAColor_UsesOffline()
    {
        var reason = Returns(Ai("Gol", "Branco", "ABC-1234", "porta aberta"));
        var color = Returns(Ai("Gol", "Preto", "ABC-1234", "farol aceso"));

        Assert.Equal(CorrectorKind.Offline, (await Pipeline(reason).RunAsync(VehicleRaw, true, CancellationToken.None)).By);
        Assert.Equal(CorrectorKind.Offline, (await Pipeline(color).RunAsync(VehicleRaw, true, CancellationToken.None)).By);
    }

    [Fact]
    public async Task Run_GeminiInventsWordsInTheFreeText_UsesOffline()
    {
        var gemini = Returns(Ai("Gol", "Branco", "ABC-1234", "farol aceso", "Gol branco farol aceso, volte logo"));

        var result = await Pipeline(gemini).RunAsync(VehicleRaw, true, CancellationToken.None);

        Assert.Equal(CorrectorKind.Offline, result.By);
    }

    [Fact]
    public async Task Run_FunctionWordsAreAllowed()
    {
        var raw = "gol branco abc1234 parado local proibido";
        var gemini = Returns(Ai("Gol", "Branco", "ABC-1234", "parado em local proibido"));

        var result = await Pipeline(gemini).RunAsync(raw, true, CancellationToken.None);

        Assert.Equal(CorrectorKind.Gemini, result.By);
        Assert.Equal("parado em local proibido", result.Alert.Reason);
    }

    [Fact]
    public async Task Run_CloseSpellingFixIsAllowed()
    {
        var gemini = Returns(GoodAi());

        var result = await Pipeline(gemini).RunAsync(VehicleRaw, true, CancellationToken.None);

        Assert.Equal(CorrectorKind.Gemini, result.By);
        Assert.Equal("farol aceso", result.Alert.Reason);
    }

    [Fact]
    public async Task Run_AccentAndCaseOnlyDifferencesAreAllowed()
    {
        var gemini = Returns(new ParsedAlert("", "", null, "", "Pais do Pedro compare\U000000E7am na Recep\U000000E7\U000000E3o"));

        var result = await Pipeline(gemini).RunAsync("pais do pedro comparecam na recepcao", true, CancellationToken.None);

        Assert.Equal(CorrectorKind.Gemini, result.By);
    }

    [Fact]
    public async Task Run_NegationMustBeInTheRaw()
    {
        // "nao" is within distance 2 of "na", but adding a negation flips the meaning.
        var gemini = Returns(Ai("Gol", "Branco", "ABC-1234", "parado n\U000000E3o garagem"));

        var result = await Pipeline(gemini).RunAsync("gol branco abc1234 parado na garagem", true, CancellationToken.None);

        Assert.Equal(CorrectorKind.Offline, result.By);
    }

    [Fact]
    public async Task Run_NegationAlreadyInTheRawIsKept()
    {
        var gemini = Returns(new ParsedAlert("", "", null, "", "Veiculo n\U000000E3o identificado"));

        var result = await Pipeline(gemini).RunAsync("veiculo nao identificado", true, CancellationToken.None);

        Assert.Equal(CorrectorKind.Gemini, result.By);
    }

    [Fact]
    public async Task Run_WordsWithDigitsMustMatchExactly()
    {
        var gemini = Returns(Ai("HB21", "Branco", "ABC-1234", "farol aceso"));

        var result = await Pipeline(gemini).RunAsync("hb20 branco abc1234 farol acesso", true, CancellationToken.None);

        Assert.Equal(CorrectorKind.Offline, result.By);
    }

    [Fact]
    public async Task Run_ShortWordsAllowOnlyOneEdit()
    {
        // "fox" is two edits from "gol": a different car, not a typo.
        var gemini = Returns(Ai("Fox", "Branco", "ABC-1234", "farol aceso"));

        var result = await Pipeline(gemini).RunAsync(VehicleRaw, true, CancellationToken.None);

        Assert.Equal(CorrectorKind.Offline, result.By);
    }

    [Fact]
    public async Task Run_APlateInsideAnotherFieldMustBeTheRawPlate()
    {
        var gemini = Returns(Ai("Gol", "Branco", "ABC-1234", "farol aceso", "Gol branco ABC-9999 farol aceso"));

        var result = await Pipeline(gemini).RunAsync(VehicleRaw, true, CancellationToken.None);

        Assert.Equal(CorrectorKind.Offline, result.By);
    }

    [Fact]
    public async Task Run_TheRawPlateInTheFreeTextIsFine()
    {
        var gemini = Returns(Ai("Gol", "Branco", "ABC-1234", "farol aceso", "Gol branco PLACA: ABC-1234 farol aceso"));

        var result = await Pipeline(gemini).RunAsync(VehicleRaw, true, CancellationToken.None);

        Assert.Equal(CorrectorKind.Gemini, result.By);
    }

    [Fact]
    public async Task Run_EmojiAndHashInTheRawAreIgnoredByTheLock()
    {
        var gemini = Returns(GoodAi());

        var result = await Pipeline(gemini).RunAsync("#\U0001F697 gol branco abc1234 farol acesso", true, CancellationToken.None);

        Assert.Equal(CorrectorKind.Gemini, result.By);
    }

    [Fact]
    public async Task Run_LockRejectionIsNotAGeminiFailure()
    {
        var gemini = Returns(Ai("Corolla", "Branco", "ABC-1234", "farol aceso"));
        var pipeline = Pipeline(gemini);

        await pipeline.RunAsync("gol branco abc1234", true, CancellationToken.None);

        Assert.Equal([true], _health);
    }

    // ---- R4: health ----

    [Fact]
    public async Task Health_IsRaisedOnlyOnChanges()
    {
        var answers = new Queue<ParsedAlert?>([GoodAi(), GoodAi(), null, null, GoodAi()]);
        var gemini = new FakeCorrector((_, _) => Task.FromResult(answers.Dequeue()));
        var pipeline = Pipeline(gemini);

        for (var i = 0; i < 5; i++)
        {
            await pipeline.RunAsync(VehicleRaw + " " + i, true, CancellationToken.None); // distinct raws: no cache
        }

        Assert.Equal([true, false, true], _health);
    }

    [Fact]
    public async Task Health_FirstCallFailureRaisesFalse()
    {
        await Pipeline(Returns(null)).RunAsync(VehicleRaw, true, CancellationToken.None);

        Assert.Equal([false], _health);
    }

    [Fact]
    public async Task Health_UseGeminiFalseLeavesTheStateAlone()
    {
        var pipeline = Pipeline(Returns(GoodAi()));

        await pipeline.RunAsync(VehicleRaw, false, CancellationToken.None);
        Assert.Empty(_health);

        await pipeline.RunAsync(VehicleRaw, true, CancellationToken.None);
        await pipeline.RunAsync(VehicleRaw, false, CancellationToken.None);
        Assert.Equal([true], _health);
    }

    [Fact]
    public async Task Health_IsThreadSafe()
    {
        var gemini = new FakeCorrector(async (_, _) =>
        {
            await Task.Yield();
            return GoodAi();
        });
        var pipeline = Pipeline(gemini);

        await Task.WhenAll(Enumerable.Range(0, 40).Select(i => pipeline.RunAsync(VehicleRaw + " " + i, true, CancellationToken.None)));

        // Every call succeeded: the state flips from unknown to online once, whatever the interleaving.
        Assert.Equal([true], _health);
    }

    // ---- R5: cancellation ----

    [Fact]
    public async Task Run_CallerCancelsDuringGemini_Throws()
    {
        using var cts = new CancellationTokenSource();
        var offline = OfflineFake();
        var started = new TaskCompletionSource();
        var gemini = new FakeCorrector(async (_, ct) =>
        {
            started.SetResult();
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return GoodAi();
        });

        var run = Pipeline(gemini, offline).RunAsync(VehicleRaw, true, cts.Token);
        await started.Task;
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Equal(0, offline.Calls);
        Assert.Empty(_health);
    }

    [Fact]
    public async Task Run_CallerCancelsWhileGeminiIgnoresTheToken_Throws()
    {
        using var cts = new CancellationTokenSource();
        var never = new TaskCompletionSource<ParsedAlert?>();
        var started = new TaskCompletionSource();
        var gemini = new FakeCorrector((_, _) =>
        {
            started.SetResult();
            return never.Task;
        });
        var offline = OfflineFake();

        var run = Pipeline(gemini, offline).RunAsync(VehicleRaw, true, cts.Token);
        await started.Task;
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Equal(0, offline.Calls);
    }

    [Fact]
    public async Task Run_AlreadyCancelled_Throws()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var gemini = Returns(GoodAi());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Pipeline(gemini).RunAsync(VehicleRaw, true, cts.Token));
        Assert.Equal(0, gemini.Calls);
    }

    [Fact]
    public async Task Run_CallerCancelsDuringOffline_Throws()
    {
        using var cts = new CancellationTokenSource();
        var offline = new FakeCorrector(async (_, ct) =>
        {
            await cts.CancelAsync();
            ct.ThrowIfCancellationRequested();
            return null;
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Pipeline(Returns(null), offline).RunAsync(VehicleRaw, true, cts.Token));
    }

    [Fact]
    public async Task Run_GeminiThrowsCancellationWithoutTheCallerCancelling_IsAGeminiFailure()
    {
        var gemini = Throws(new OperationCanceledException());

        var result = await Pipeline(gemini).RunAsync(VehicleRaw, true, CancellationToken.None);

        Assert.Equal(CorrectorKind.Offline, result.By);
        Assert.Equal([false], _health);
    }

    // ---- R6: fixed rules ----

    [Fact]
    public async Task Rules_AreAppliedAfterTheCorrectorOnEveryTextField()
    {
        var rules = FixedRules.Parse("aceso => ligado\nbranco => alvo\ngol => carro");
        var gemini = Returns(GoodAi());

        var result = await Pipeline(gemini, rules: rules).RunAsync(VehicleRaw, true, CancellationToken.None);

        Assert.Equal("farol ligado", result.Alert.Reason);
        Assert.Equal("alvo", result.Alert.Color);
        Assert.Equal("carro", result.Alert.Model);
        Assert.Contains("ligado", result.Alert.FreeText);
        Assert.Equal("ABC-1234", result.Alert.Plate);
    }

    [Fact]
    public async Task Rules_AreAppliedOnTheOfflinePathToo()
    {
        var rules = FixedRules.Parse("aceso => ligado");

        var result = await Pipeline(Returns(null), rules: rules).RunAsync(VehicleRaw, true, CancellationToken.None);

        Assert.Equal(CorrectorKind.Offline, result.By);
        Assert.Equal("FAROL ligado", result.Alert.Reason);
    }

    [Fact]
    public async Task Rules_NeverTouchThePlate()
    {
        var rules = FixedRules.Parse("abc-1234 => ZZZ");

        var result = await Pipeline(Returns(GoodAi()), rules: rules).RunAsync("gol branco abc-1234 farol acesso", true, CancellationToken.None);

        Assert.Equal("ABC-1234", result.Alert.Plate);
    }

    [Fact]
    public async Task Rules_AreReadOncePerRun()
    {
        var reads = 0;
        var pipeline = new CorrectionPipeline(Returns(GoodAi()), OfflineFake(), () =>
        {
            reads++;
            return FixedRules.Empty;
        }, LongTimeout, _clock);

        await pipeline.RunAsync(VehicleRaw, true, CancellationToken.None);

        Assert.Equal(1, reads);
    }

    [Fact]
    public async Task Rules_AreAppliedWithTheNewestRulesEvenOnACacheHit()
    {
        var current = FixedRules.Empty;
        var gemini = Returns(GoodAi());
        var pipeline = new CorrectionPipeline(gemini, OfflineFake(), () => current, LongTimeout, _clock);

        var before = await pipeline.RunAsync(VehicleRaw, true, CancellationToken.None);
        current = FixedRules.Parse("aceso => ligado");
        var after = await pipeline.RunAsync(VehicleRaw, true, CancellationToken.None);

        Assert.Equal("farol aceso", before.Alert.Reason);
        Assert.Equal("farol ligado", after.Alert.Reason);
        Assert.Equal(1, gemini.Calls);
    }

    // ---- R7: Changed ----

    [Fact]
    public async Task Changed_TrueWhenASpellingWasFixed()
    {
        var result = await Pipeline(Returns(GoodAi())).RunAsync(VehicleRaw, true, CancellationToken.None);

        Assert.True(result.Changed);
    }

    [Fact]
    public async Task Changed_TrueForTheOfflineFixToo()
    {
        var result = await Pipeline(Returns(null)).RunAsync("farol acesso", true, CancellationToken.None);

        Assert.True(result.Changed);
    }

    [Theory]
    [InlineData("gol branco abc1234 farol aceso")]
    [InlineData("GOL BRANCO ABC1234 FAROL ACESO")]
    [InlineData("#gol branco abc1234 farol aceso")]
    [InlineData("\U0001F697 gol branco placa: abc1234 farol aceso")]
    [InlineData("gol branco abc-1234, farol aceso!")]
    public async Task Changed_FalseWhenOnlyFormatOrCaseChanged(string raw)
    {
        var offline = new FakeCorrector((r, _) => Task.FromResult<ParsedAlert?>(AlertParser.Parse(r)));

        var result = await Pipeline(Returns(null), offline).RunAsync(raw, true, CancellationToken.None);

        Assert.False(result.Changed);
    }

    [Fact]
    public async Task Changed_FalseWhenGeminiOnlyReformats()
    {
        var gemini = Returns(Ai("Gol", "Branco", "ABC-1234", "Farol aceso", "Gol Branco | PLACA: ABC-1234 | Farol aceso"));

        var result = await Pipeline(gemini).RunAsync("farol aceso, branco gol abc1234", true, CancellationToken.None);

        Assert.Equal(CorrectorKind.Gemini, result.By);
        Assert.False(result.Changed);
    }

    [Fact]
    public async Task Changed_TrueWhenAnAccentIsAdded()
    {
        var gemini = Returns(new ParsedAlert("", "", null, "", "Pais na recep\U000000E7\U000000E3o"));

        var result = await Pipeline(gemini).RunAsync("pais na recepcao", true, CancellationToken.None);

        Assert.True(result.Changed);
    }

    [Fact]
    public async Task Changed_TrueWhenAFixedRuleRewrites()
    {
        var rules = FixedRules.Parse("branco => alvo");
        var offline = new FakeCorrector((r, _) => Task.FromResult<ParsedAlert?>(AlertParser.Parse(r)));

        var result = await Pipeline(Returns(null), offline, rules).RunAsync("gol branco abc1234", false, CancellationToken.None);

        Assert.True(result.Changed);
    }

    [Fact]
    public async Task Changed_CountsRepeatedWords()
    {
        var gemini = Returns(new ParsedAlert("", "", null, "", "Venha venha rapido"));

        var result = await Pipeline(gemini).RunAsync("venha rapido", true, CancellationToken.None);

        Assert.True(result.Changed);
    }

    // ---- R2: cache ----

    [Fact]
    public async Task Cache_SameRawWithinTwoMinutesSkipsGeminiAndRaisesNoEvent()
    {
        var gemini = Returns(GoodAi());
        var pipeline = Pipeline(gemini);

        var first = await pipeline.RunAsync(VehicleRaw, true, CancellationToken.None);
        _clock.Advance(TimeSpan.FromSeconds(119));
        var second = await pipeline.RunAsync(VehicleRaw, true, CancellationToken.None);

        Assert.Equal(1, gemini.Calls);
        Assert.Equal(first, second);
        Assert.Equal(CorrectorKind.Gemini, second.By);
        Assert.Equal([true], _health);
    }

    [Fact]
    public async Task Cache_ExpiresAfterTwoMinutes()
    {
        var gemini = Returns(GoodAi());
        var pipeline = Pipeline(gemini);

        await pipeline.RunAsync(VehicleRaw, true, CancellationToken.None);
        _clock.Advance(TimeSpan.FromMinutes(2));
        await pipeline.RunAsync(VehicleRaw, true, CancellationToken.None);

        Assert.Equal(2, gemini.Calls);
    }

    [Fact]
    public async Task Cache_IsKeyedByTheExactRaw()
    {
        var gemini = Returns(GoodAi());
        var pipeline = Pipeline(gemini);

        await pipeline.RunAsync(VehicleRaw, true, CancellationToken.None);
        await pipeline.RunAsync(VehicleRaw + " ", true, CancellationToken.None);

        Assert.Equal(2, gemini.Calls);
    }

    [Fact]
    public async Task Cache_KeepsAtMostFiftyEntriesAndDropsTheOldest()
    {
        var gemini = new FakeCorrector((raw, _) => Task.FromResult<ParsedAlert?>(new ParsedAlert("", "", null, "", raw)));
        var pipeline = Pipeline(gemini);

        for (var i = 0; i < 51; i++)
        {
            await pipeline.RunAsync("aviso numero " + i, true, CancellationToken.None);
        }

        Assert.Equal(51, gemini.Calls);

        await pipeline.RunAsync("aviso numero 50", true, CancellationToken.None); // newest: still cached
        await pipeline.RunAsync("aviso numero 1", true, CancellationToken.None); // second oldest: still cached
        Assert.Equal(51, gemini.Calls);

        await pipeline.RunAsync("aviso numero 0", true, CancellationToken.None); // oldest: dropped
        Assert.Equal(52, gemini.Calls);
    }

    [Fact]
    public async Task Cache_DoesNotKeepFailuresOrLockRejections()
    {
        var failing = Returns(null);
        var rejected = Returns(Ai("Corolla", "Branco", "ABC-1234", "farol aceso"));
        var failingPipeline = Pipeline(failing);
        var rejectedPipeline = Pipeline(rejected);

        await failingPipeline.RunAsync(VehicleRaw, true, CancellationToken.None);
        await failingPipeline.RunAsync(VehicleRaw, true, CancellationToken.None);
        await rejectedPipeline.RunAsync("gol branco abc1234", true, CancellationToken.None);
        await rejectedPipeline.RunAsync("gol branco abc1234", true, CancellationToken.None);

        Assert.Equal(2, failing.Calls);
        Assert.Equal(2, rejected.Calls);
    }

    [Fact]
    public async Task Cache_KeepsARefusal()
    {
        var gemini = Returns(new ParsedAlert("", "", null, "", ""));
        var pipeline = Pipeline(gemini);

        await pipeline.RunAsync("prank", true, CancellationToken.None);
        var again = await pipeline.RunAsync("prank", true, CancellationToken.None);

        Assert.True(again.Refused);
        Assert.Equal(1, gemini.Calls);
    }

    [Fact]
    public async Task Cache_IsNotUsedWhenGeminiIsOff()
    {
        var gemini = Returns(GoodAi());
        var pipeline = Pipeline(gemini);

        await pipeline.RunAsync(VehicleRaw, true, CancellationToken.None);
        var off = await pipeline.RunAsync(VehicleRaw, false, CancellationToken.None);

        Assert.Equal(CorrectorKind.Offline, off.By);
    }
}
