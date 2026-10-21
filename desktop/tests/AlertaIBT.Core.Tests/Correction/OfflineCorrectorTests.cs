using AlertaIBT.Core.Alerts;
using AlertaIBT.Core.Correction;

namespace AlertaIBT.Core.Tests.Correction;

// Loading the dictionary takes a while: one instance for the whole class.
public class OfflineCorrectorFixture
{
    public OfflineCorrector Corrector { get; } =
        OfflineCorrector.Load(Path.Combine(AppContext.BaseDirectory, "Dictionaries"));
}

public class OfflineCorrectorTests(OfflineCorrectorFixture fixture) : IClassFixture<OfflineCorrectorFixture>
{
    private readonly OfflineCorrector _corrector = fixture.Corrector;

    private async Task<ParsedAlert> Correct(string raw)
    {
        var result = await _corrector.CorrectAsync(raw, CancellationToken.None);
        Assert.NotNull(result);
        return result;
    }

    // ---- the brief ----

    [Fact]
    public async Task Correct_FixesTheReasonOfAVehicleAlert()
    {
        var alert = await Correct("corolla prata abc1d23 vidro abrto");

        Assert.Equal("COROLLA", alert.Model);
        Assert.Equal("PRATA", alert.Color);
        Assert.Equal("ABC-1D23", alert.Plate);
        Assert.Equal("VIDRO ABERTO", alert.Reason);
    }

    [Fact]
    public async Task Correct_FixesAnAnnouncement()
    {
        var alert = await Correct("pais do pedro compareser na recepssao");

        Assert.Contains("COMPARECER", alert.FreeText);
        Assert.Contains("RECEP\U000000C7\U000000C3O", alert.FreeText);
        Assert.Equal("PAIS DO PEDRO COMPARECER NA RECEP\U000000C7\U000000C3O", alert.FreeText);
    }

    [Theory]
    [InlineData("gol branco abc1234 farol aceso", "ABC-1234")]
    [InlineData("gol branco abc-1234 farol aceso", "ABC-1234")]
    [InlineData("gol branco placa: abc1d23 farol aceso", "ABC-1D23")]
    [InlineData("creta preto gbr- 9003 farol aceso", "GBR-9003")]
    [InlineData("gol branco qwe 1b23 farol aceso", "QWE-1B23")]
    public async Task Correct_NeverTouchesThePlate(string raw, string plate)
    {
        var alert = await Correct(raw);

        Assert.Equal(plate, alert.Plate);
        Assert.Equal(AlertParser.Parse(raw).FreeText, alert.FreeText);
    }

    [Theory]
    [InlineData("hb20 corolla onix")]
    [InlineData("creta ecosport kwid tracker")]
    [InlineData("hb20s kicks virtus nivus")]
    [InlineData("xre nmax pcx bros")]
    public async Task Correct_LeavesVehicleNamesAsTyped(string raw)
    {
        var alert = await Correct(raw);

        Assert.Equal(raw.ToUpperInvariant(), alert.FreeText);
    }

    [Fact]
    public async Task Correct_KeepsAWordWithNoSafeSuggestion()
    {
        var alert = await Correct("zxqwvk branco");

        Assert.Equal("ZXQWVK BRANCO", alert.FreeText);
    }

    [Fact]
    public async Task Correct_SkipsFarSuggestionsAndTakesTheFirstNearOne()
    {
        // Hunspell offers "receptossoma", "recesso"... before "recep\U000000E7\U000000E3o"; the first two edits away wins.
        var alert = await Correct("recepssao");

        Assert.Equal("RECEP\U000000C7\U000000C3O", alert.FreeText);
    }

    [Theory]
    [InlineData("pssoa", "PISCA")]
    [InlineData("xyz", "LUZ")]
    public async Task Correct_DoesNotTurnAWordIntoADomainWordTwoEditsAway(string raw, string notThis)
    {
        var alert = await Correct(raw);

        Assert.NotEqual(notThis, alert.FreeText);
    }

    [Fact]
    public async Task Correct_PrefersTheDomainWordOneEditAway()
    {
        // Hunspell only offers "abeto" (a tree) for "abrto".
        var alert = await Correct("vidro abrto");

        Assert.Equal("VIDRO ABERTO", alert.Reason);
    }

    [Fact]
    public async Task Correct_LeavesCapitalizedWordsAlone_TheyAreNames()
    {
        var alert = await Correct("pais do Kauan e da Gutnik compareser");

        Assert.Equal("PAIS DO KAUAN E DA GUTNIK COMPARECER", alert.FreeText);
    }

    [Fact]
    public async Task Correct_StillAddsAccentsToACapitalizedDomainWord()
    {
        var alert = await Correct("Recepcao");

        Assert.Equal("RECEP\U000000C7\U000000C3O", alert.FreeText);
    }

    // ---- R3 ----

    [Theory]
    [InlineData("gol ok vc", "GOL OK VC")]
    [InlineData("gol tb", "GOL TB")]
    public async Task Correct_LeavesTokensOfOneOrTwoLettersAlone(string raw, string expected)
    {
        Assert.Equal(expected, (await Correct(raw)).FreeText);
    }

    [Fact]
    public async Task Correct_LeavesTokensWithDigitsAlone()
    {
        var alert = await Correct("fox2015 1,0 branco c3 gol");

        Assert.Equal("FOX2015 1,0 BRANCO C3 GOL", alert.FreeText);
    }

    [Fact]
    public async Task Correct_LeavesTheHashAndTheWordPlacaAlone()
    {
        var alert = await Correct("#gol branco placa abc1234");

        Assert.Equal("ABC-1234", alert.Plate);
        Assert.Equal("GOL", alert.Model);
        Assert.Equal("BRANCO", alert.Color);
    }

    [Fact]
    public async Task Correct_KeepsSeparatorsLineBreaksAndPunctuation()
    {
        var alert = await Correct("vidro abrto, e porta abrta!");

        Assert.Equal("VIDRO ABERTO, E PORTA ABERTA!", alert.FreeText);
        Assert.Equal("VIDRO ABERTO, PORTA ABERTA", alert.Reason);
    }

    [Fact]
    public async Task Correct_SegmentsStillWorkAfterTheCorrection()
    {
        var alert = await Correct("corolla prata | abc1234 / vidro abrto\nfarol aceso");

        Assert.Equal("COROLLA", alert.Model);
        Assert.Equal("PRATA", alert.Color);
        Assert.Equal("ABC-1234", alert.Plate);
        Assert.Equal("VIDRO ABERTO, FAROL ACESO", alert.Reason);
    }

    [Fact]
    public async Task Correct_FixesWordsGluedToPunctuationAndSlashes()
    {
        var alert = await Correct("(vidro abrto) porta/abrta");

        Assert.Equal("(VIDRO ABERTO) PORTA/ABERTA", alert.FreeText);
    }

    [Fact]
    public async Task Correct_AddsMissingAccentsWhenTheWordIsOtherwiseRight()
    {
        var alert = await Correct("recepcao e portao");

        Assert.Equal("RECEP\U000000C7\U000000C3O E PORT\U000000C3O", alert.FreeText);
    }

    [Fact]
    public async Task Correct_LeavesWordsTheDictionaryAcceptsAlone()
    {
        var alert = await Correct("acesso livre aos pais");

        Assert.Equal("ACESSO LIVRE AOS PAIS", alert.FreeText);
    }

    [Fact]
    public async Task Correct_HyphenatedDomainWordsAndPartsAreHandled()
    {
        var alert = await Correct("gol preto porta-malas aberto pisca-alerta");

        Assert.Equal("GOL PRETO PORTA-MALAS ABERTO PISCA-ALERTA", alert.FreeText);
    }

    [Fact]
    public async Task Correct_FixesThePartsOfAHyphenatedWord()
    {
        var alert = await Correct("farol-abrto");

        Assert.Equal("FAROL-ABERTO", alert.FreeText);
    }

    [Fact]
    public async Task Correct_EmptyOrBlankRawGivesTheParsedEmptyAlert()
    {
        Assert.Equal(AlertParser.Parse(""), await Correct(""));
        Assert.Equal(AlertParser.Parse("  "), await Correct("  "));
    }

    [Fact]
    public async Task Correct_NeverReturnsNullForARealText()
    {
        Assert.NotNull(await _corrector.CorrectAsync("???", CancellationToken.None));
        Assert.NotNull(await _corrector.CorrectAsync("\U0001F697 \U0001F697", CancellationToken.None));
    }

    [Fact]
    public async Task Correct_ACancelledTokenThrows()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _corrector.CorrectAsync("vidro abrto", cts.Token));
    }

    [Fact]
    public async Task Correct_CanRunManyTimesAtOnce()
    {
        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Correct("corolla prata abc1d23 vidro abrto")));

        Assert.All(results, alert => Assert.Equal("VIDRO ABERTO", alert.Reason));
    }

    [Fact]
    public async Task Correct_WorksOnTextTypedInCapitals()
    {
        var alert = await Correct("COROLLA PRATA ABC1D23 VIDRO ABRTO");

        Assert.Equal("VIDRO ABERTO", alert.Reason);
        Assert.Equal("ABC-1D23", alert.Plate);
    }

    [Fact]
    public void Load_ThrowsWhenTheDictionaryIsMissing()
    {
        Assert.ThrowsAny<IOException>(() => OfflineCorrector.Load(Path.Combine(AppContext.BaseDirectory, "no-such-dir")));
    }
}

public class DomainWordsTests
{
    [Fact]
    public void All_HasAtLeast120Entries()
    {
        Assert.True(DomainWords.All.Count >= 120, $"only {DomainWords.All.Count}");
    }

    [Theory]
    [InlineData("hb20")]
    [InlineData("HB20")]
    [InlineData("corolla")]
    [InlineData("onix")]
    [InlineData("\U000000D4nix")]
    [InlineData("creta")]
    [InlineData("citro\U000000EBn")]
    [InlineData("gwm")]
    [InlineData("haval")]
    [InlineData("byd")]
    [InlineData("tiggo")]
    [InlineData("titan")]
    [InlineData("nmax")]
    [InlineData("pcx")]
    [InlineData("xre")]
    public void All_ContainsCarsMakesAndMotorcyclesIgnoringCaseAndAccents(string word)
    {
        Assert.Contains(word, DomainWords.All);
    }

    [Theory]
    [InlineData("branco")]
    [InlineData("BRANCA")]
    [InlineData("champanhe")]
    [InlineData("marinho")]
    [InlineData("MET\U000000C1LICO")]
    [InlineData("metalico")]
    [InlineData("escuro")]
    public void All_ContainsEveryColorAndModifierOfTheVocabulary(string word)
    {
        Assert.Contains(word, DomainWords.All);
    }

    [Fact]
    public void All_ContainsAllTheColorsAndModifiers()
    {
        foreach (var word in Vocabulary.Colors.Concat(Vocabulary.ColorModifiers))
        {
            Assert.Contains(word, DomainWords.All);
        }
    }

    [Theory]
    [InlineData("farol")]
    [InlineData("far\U000000F3is")]
    [InlineData("aceso")]
    [InlineData("acesa")]
    [InlineData("acesos")]
    [InlineData("vidro")]
    [InlineData("vidros")]
    [InlineData("aberto")]
    [InlineData("aberta")]
    [InlineData("porta")]
    [InlineData("portas")]
    [InlineData("porta-malas")]
    [InlineData("pisca")]
    [InlineData("pisca-alerta")]
    [InlineData("alerta")]
    [InlineData("alarme")]
    [InlineData("disparado")]
    [InlineData("pneu")]
    [InlineData("pneus")]
    [InlineData("furado")]
    [InlineData("murcho")]
    [InlineData("estacionado")]
    [InlineData("estacionada")]
    [InlineData("parado")]
    [InlineData("parada")]
    [InlineData("bloqueando")]
    [InlineData("garagem")]
    [InlineData("port\U000000E3o")]
    [InlineData("sa\U000000EDda")]
    [InlineData("entrada")]
    [InlineData("vaga")]
    [InlineData("idoso")]
    [InlineData("deficiente")]
    [InlineData("contram\U000000E3o")]
    [InlineData("proibido")]
    [InlineData("local")]
    [InlineData("cal\U000000E7ada")]
    [InlineData("faixa")]
    [InlineData("dono")]
    [InlineData("comparecer")]
    [InlineData("recep\U000000E7\U000000E3o")]
    [InlineData("urgente")]
    [InlineData("luz")]
    [InlineData("luzes")]
    [InlineData("ligado")]
    [InlineData("ligada")]
    [InlineData("som")]
    [InlineData("chave")]
    public void All_ContainsTheReasonWords(string word)
    {
        Assert.Contains(word, DomainWords.All);
    }

    [Fact]
    public void All_DoesNotContainOrdinaryMisspellings()
    {
        Assert.DoesNotContain("abrto", DomainWords.All);
        Assert.DoesNotContain("compareser", DomainWords.All);
    }
}
