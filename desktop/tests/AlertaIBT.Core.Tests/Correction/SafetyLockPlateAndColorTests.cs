using AlertaIBT.Core.Alerts;
using AlertaIBT.Core.Correction;

namespace AlertaIBT.Core.Tests.Correction;

// The safety lock, two more rules: a plate typed with a space (M2) and the color (Gemini may not change it).
public class SafetyLockPlateAndColorTests
{
    private sealed class FakeCorrector(ParsedAlert? answer) : ICorrector
    {
        public Task<ParsedAlert?> CorrectAsync(string raw, CancellationToken ct) => Task.FromResult(answer);
    }

    private static ParsedAlert Ai(string model, string color, string? plate, string reason, string? free = null) =>
        new(model, color, plate, reason, free ?? string.Join(' ', new[] { model, color, plate, reason }.Where(s => !string.IsNullOrEmpty(s))));

    private static Task<CorrectionResult> Run(string raw, ParsedAlert gemini)
    {
        var pipeline = new CorrectionPipeline(
            new FakeCorrector(gemini),
            new FakeCorrector(null),
            () => FixedRules.Empty,
            TimeSpan.FromSeconds(5));
        return pipeline.RunAsync(raw, true, CancellationToken.None);
    }

    // ---- M2: a plate with a space ----

    [Fact]
    public async Task SpacedOldPlate_WithoutTheWordPlaca_IsAcceptedWhenGeminiReadsItContiguously()
    {
        var result = await Run("gol branco abc 1234 farol aceso", Ai("Gol", "Branco", "ABC-1234", "farol aceso"));

        Assert.Equal(CorrectorKind.Gemini, result.By);
        Assert.Equal("ABC-1234", result.Alert.Plate);
        Assert.Equal("Gol", result.Alert.Model);
    }

    [Fact]
    public async Task SpacedPlate_WhenGeminiPutsThePlateInTheFreeTextToo_IsStillAccepted()
    {
        var result = await Run(
            "gol branco abc 1234 farol aceso",
            Ai("Gol", "Branco", "ABC1234", "farol aceso", "Gol Branco ABC-1234 farol aceso"));

        Assert.Equal(CorrectorKind.Gemini, result.By);
        Assert.Equal("ABC-1234", result.Alert.Plate);
    }

    [Theory]
    [InlineData("gol branco ab c1234 farol aceso")]
    [InlineData("gol branco abc - 1234 farol aceso")]
    [InlineData("gol branco ABC  1234 farol aceso")]
    [InlineData("gol branco a b c 1 2 3 4 farol aceso")]
    public async Task PlateTypedWithSpacesOrHyphensInAnyPlace_IsAcceptedWhenTheSevenCharactersAreContiguous(string raw)
    {
        var result = await Run(raw, Ai("Gol", "Branco", "ABC-1234", "farol aceso"));

        Assert.Equal(CorrectorKind.Gemini, result.By);
    }

    [Theory]
    [InlineData("abc 1234", "ABC-1234")]
    [InlineData("ab c1 234", "ABC-1234")]
    [InlineData("abc 12 34", "ABC-1234")]
    public async Task SpacedPlateStartingAtTheBeginningOfAWord_IsAccepted(string raw, string plate)
    {
        var result = await Run(raw, Ai(string.Empty, string.Empty, plate, string.Empty));

        Assert.Equal(CorrectorKind.Gemini, result.By);
    }

    [Theory]
    [InlineData("gol branco farol aceso 1234", "ESO-1234")]
    [InlineData("gol branco farol 1234 no estacionamento", "ROL-1234")]
    public async Task APlateInventedFromTheTailOfAWord_FailsTheLock(string raw, string plate)
    {
        var result = await Run(raw, Ai("Gol", "Branco", plate, "farol aceso"));

        Assert.Equal(CorrectorKind.Offline, result.By);
    }

    [Fact]
    public async Task APlateStraddlingWords_StillFailsTheLock()
    {
        var result = await Run("abc gol 1234", Ai("Gol", "", "ABC-1234", string.Empty));

        Assert.Equal(CorrectorKind.Offline, result.By);
    }

    [Fact]
    public async Task SlotToleranceApplies_AsInPlateParserNormalize()
    {
        // "A8C 1234" read as ABC-1234 (8 in a letter slot is B).
        var result = await Run("gol branco a8c 1234 farol aceso", Ai("Gol", "Branco", "ABC-1234", "farol aceso"));

        Assert.Equal(CorrectorKind.Gemini, result.By);
    }

    [Fact]
    public async Task APlateGeminiInventsThatIsNotInTheRawText_FailsTheLock()
    {
        var result = await Run("gol branco abc 1234 farol aceso", Ai("Gol", "Branco", "XYZ-9876", "farol aceso"));

        Assert.Equal(CorrectorKind.Offline, result.By);
    }

    [Fact]
    public async Task APlateSplitByTwoWords_IsNotAccepted()
    {
        var result = await Run("abc gol 1234 branco farol aceso", Ai("Gol", "Branco", "ABC-1234", "farol aceso"));

        Assert.Equal(CorrectorKind.Offline, result.By);
    }

    [Fact]
    public async Task PlateInTheRawTextWithNoPlateFromGemini_StillFailsTheLock()
    {
        var result = await Run("gol branco abc1234 farol aceso", Ai("Gol", "Branco", null, "farol aceso"));

        Assert.Equal(CorrectorKind.Offline, result.By);
    }

    [Fact]
    public async Task GeminiPlateWhenTheRawHasNoPlateAtAll_FailsTheLock()
    {
        var result = await Run("gol branco farol aceso", Ai("Gol", "Branco", "ABC-1234", "farol aceso"));

        Assert.Equal(CorrectorKind.Offline, result.By);
    }

    // ---- color lock ----

    [Fact]
    public async Task ChangedColor_FailsTheLock()
    {
        var result = await Run("gol preto abc1234", Ai("Gol", "Prata", "ABC-1234", string.Empty));

        Assert.Equal(CorrectorKind.Offline, result.By);
    }

    [Fact]
    public async Task SameColor_IsAccepted()
    {
        var result = await Run("gol preto abc1234", Ai("Gol", "Preto", "ABC-1234", string.Empty));

        Assert.Equal(CorrectorKind.Gemini, result.By);
    }

    [Theory]
    [InlineData("gol preta abc1234", "Preto")]
    [InlineData("gol PRETO abc1234", "preta")]
    [InlineData("gol branco abc1234", "Branca")]
    [InlineData("gol vermelha abc1234", "Vermelho")]
    public async Task GenderVariantsOfTheSameColor_AreAccepted(string raw, string aiColor)
    {
        var result = await Run(raw, Ai("Gol", aiColor, "ABC-1234", string.Empty));

        Assert.Equal(CorrectorKind.Gemini, result.By);
    }

    [Fact]
    public async Task AColorWithAnAcceptedModifier_IsAccepted()
    {
        var result = await Run("gol azul escuro abc1234", Ai("Gol", "Azul escuro", "ABC-1234", string.Empty));

        Assert.Equal(CorrectorKind.Gemini, result.By);
    }

    [Fact]
    public async Task AModifierTheRawTextDoesNotHave_FailsTheLock()
    {
        var result = await Run("gol azul abc1234", Ai("Gol", "Azul escuro", "ABC-1234", string.Empty));

        Assert.Equal(CorrectorKind.Offline, result.By);
    }

    [Fact]
    public async Task ColorWithoutAccentOrWithCapitals_IsAccepted()
    {
        var result = await Run("fox MARINHO azul abc1234", Ai("Fox", "Azul Marinho", "ABC-1234", string.Empty));

        Assert.Equal(CorrectorKind.Gemini, result.By);
    }

    [Fact]
    public async Task ColorLeftOutOfAVehicleAnswer_WhenTheRawHasOne_FailsTheLock()
    {
        var result = await Run("gol preto abc1234", Ai("Gol", string.Empty, "ABC-1234", string.Empty, "Gol ABC-1234"));

        Assert.Equal(CorrectorKind.Offline, result.By);
    }

    [Fact]
    public async Task AFreeAnswerWithNoColor_ForARawThatMentionsAColorWord_IsNotBlocked()
    {
        var result = await Run("culto especial com a rosa de saron hoje", new ParsedAlert(string.Empty, string.Empty, null, string.Empty, "Culto especial com a Rosa de Saron hoje"));

        Assert.Equal(CorrectorKind.Gemini, result.By);
    }

    [Fact]
    public async Task ARawWithTwoColors_AcceptsEitherOne_ButNotAThird()
    {
        var accepted = await Run("gol branco abc1234 vidro verde aberto", Ai("Gol", "Branco", "ABC-1234", "vidro verde aberto"));
        var rejected = await Run("gol branco abc1234 vidro verde aberto", Ai("Gol", "Cinza", "ABC-1234", "vidro verde aberto"));

        Assert.Equal(CorrectorKind.Gemini, accepted.By);
        Assert.Equal(CorrectorKind.Offline, rejected.By);
    }

    [Fact]
    public async Task RawWithoutAColorWord_GeminiColorEmpty_IsAccepted()
    {
        var result = await Run("gol abc1234 farol aceso", Ai("Gol", string.Empty, "ABC-1234", "farol aceso"));

        Assert.Equal(CorrectorKind.Gemini, result.By);
    }

    // ---- the equivalence map ----

    [Theory]
    [InlineData("preto", "preta", true)]
    [InlineData("Branca", "BRANCO", true)]
    [InlineData("vermelho", "vermelha", true)]
    [InlineData("amarelo", "amarela", true)]
    [InlineData("dourado", "dourada", true)]
    [InlineData("roxo", "roxa", true)]
    [InlineData("prata", "prata", true)]
    [InlineData("azul", "azul", true)]
    [InlineData("preto", "prata", false)]
    [InlineData("branco", "bege", false)]
    [InlineData("cinza", "grafite", false)]
    public void SameColor_UsesGenderVariants(string a, string b, bool expected)
    {
        Assert.Equal(expected, ColorLock.SameColor(a, b));
        Assert.Equal(expected, ColorLock.SameColor(b, a));
    }
}
