using AlertaIBT.Core.Alerts;

namespace AlertaIBT.Core.Tests.Alerts;

public class PlateParserTests
{
    [Theory]
    // Old (ABC-1234) and Mercosul (ABC1D23, shown as ABC-1D23) plates, however they were typed.
    [InlineData("abc1234", "ABC-1234")]
    [InlineData("abc-1d23", "ABC-1D23")]
    [InlineData("QWE 1B23", "QWE-1B23")]
    [InlineData("gbr- 9003", "GBR-9003")]
    [InlineData(" abc.1234 ", "ABC-1234")]
    // Letter slots (1st to 3rd) accept look-alike digits: 0 -> O, 1 -> I, 5 -> S, 8 -> B.
    [InlineData("a8c1234", "ABC-1234")]
    [InlineData("0bc1234", "OBC-1234")]
    [InlineData("a1c1234", "AIC-1234")]
    [InlineData("ab51234", "ABS-1234")]
    // Number slots (4th, 6th, 7th) accept look-alike letters: O -> 0, I -> 1, S -> 5, B -> 8.
    [InlineData("abcI234", "ABC-1234")]
    [InlineData("abcO234", "ABC-0234")]
    [InlineData("abc12s4", "ABC-1254")]
    [InlineData("abc123b", "ABC-1238")]
    [InlineData("ABC1D2O", "ABC-1D20")]
    // The 5th slot is a letter (Mercosul) or a number (old) and stays exactly as typed.
    [InlineData("abc-1o23", "ABC-1O23")]
    public void Normalize_ReturnsCanonicalPlate(string candidate, string expected)
    {
        Assert.Equal(expected, PlateParser.Normalize(candidate));
    }

    [Theory]
    [InlineData("abc123")] // 6 characters
    [InlineData("corolla")] // 7 characters, but L and A cannot be numbers
    [InlineData("abc12345")] // 8 characters: too long, never truncated
    [InlineData("abcd234")] // D is not a look-alike for a number
    [InlineData("a2c1234")] // 2 is not a look-alike for a letter
    [InlineData("abc\U000000E91234")] // a non-ASCII letter is never silently dropped
    [InlineData("")]
    public void Normalize_ReturnsNull_WhenTextIsNotAPlate(string candidate)
    {
        Assert.Null(PlateParser.Normalize(candidate));
    }

    [Fact]
    public void Normalize_Throws_WhenCandidateIsNull()
    {
        Assert.Throws<ArgumentNullException>(() => PlateParser.Normalize(null!));
    }

    [Fact]
    public void TryExtract_Throws_WhenTextIsNull()
    {
        Assert.Throws<ArgumentNullException>(() => PlateParser.TryExtract(null!, out _, out _));
    }

    [Fact]
    public void TryExtract_FindsPlateAndRemovesPlacaLabel()
    {
        var found = PlateParser.TryExtract("gol branco placa ffo4745 farol aceso", out var plate, out var rest);

        Assert.True(found);
        Assert.Equal("FFO-4745", plate);
        Assert.Equal("gol branco\nfarol aceso", rest);
    }

    [Fact]
    public void TryExtract_ReturnsFalse_WhenThereIsNoPlate()
    {
        var found = PlateParser.TryExtract("corolla prata vidro aberto", out var plate, out var rest);

        Assert.False(found);
        Assert.Equal("", plate);
        Assert.Equal("corolla prata vidro aberto", rest);
    }

    [Theory]
    [InlineData("gol branco placa abc1234 farol aceso")]
    [InlineData("gol branco Placa: abc1234 farol aceso")]
    [InlineData("gol branco PLACA : ABC-1234 farol aceso")]
    [InlineData("gol branco placa:abc1234 farol aceso")]
    [InlineData("gol branco abc1234 farol aceso")]
    public void TryExtract_RemovesTheLabelWhicheverWayItIsTyped(string text)
    {
        Assert.True(PlateParser.TryExtract(text, out var plate, out var rest));

        Assert.Equal("ABC-1234", plate);
        Assert.Equal("gol branco\nfarol aceso", rest);
    }

    // The label may end in a colon or in a hyphen (with spaces around either): neither the word "placa" nor the
    // hyphen is left behind.
    [Theory]
    [InlineData("placa-abc1234", "")]
    [InlineData("placa -abc1234", "")]
    [InlineData("Placa- abc1234", "")]
    [InlineData("gol branco placa - abc1234 farol aceso", "gol branco\nfarol aceso")]
    [InlineData("PLACA -ABC-1234", "")]
    public void TryExtract_RemovesTheLabelHyphenToo(string text, string expectedRest)
    {
        Assert.True(PlateParser.TryExtract(text, out var plate, out var rest));

        Assert.Equal("ABC-1234", plate);
        Assert.Equal(expectedRest, rest);
    }

    [Fact]
    public void TryExtract_KeepsSeparatorsAroundThePlate()
    {
        Assert.True(PlateParser.TryExtract("#creta preto | placa:gbr- 9003 / farol aceso", out var plate, out var rest));

        Assert.Equal("GBR-9003", plate);
        Assert.Equal("#creta preto |\n/ farol aceso", rest);
    }

    // The plate is the most reliable boundary between the vehicle and the reason ("civic preto abc1234 chave dentro
    // do carro" has no trigger word at all), so a line break takes the place of the plate and of its label.
    [Theory]
    [InlineData("prata abc1d23", "prata")]
    [InlineData("hb20 vermelho abc1234 parado em frente \U000000E0 garagem", "hb20 vermelho\nparado em frente \U000000E0 garagem")]
    [InlineData("civic preto abc1234 chave dentro do carro", "civic preto\nchave dentro do carro")]
    [InlineData("civic preto placa abc1234 chave dentro do carro", "civic preto\nchave dentro do carro")]
    [InlineData("abc1234 vidro aberto", "vidro aberto")]
    public void TryExtract_LeavesALineBreakWhereThePlateWas(string text, string expectedRest)
    {
        Assert.True(PlateParser.TryExtract(text, out _, out var rest));

        Assert.Equal(expectedRest, rest);
    }

    // A candidate typed with spaces only between its letters and its numbers ("abc 1234") is just as likely a
    // word and a number ("gol 2015"), so it counts only when it is Mercosul-shaped or labeled with "placa".
    [Theory]
    [InlineData("prata corolla QWE 1B23 vidro aberto", "prata corolla\nvidro aberto")]
    [InlineData("onix preto QWE 1B23", "onix preto")]
    public void TryExtract_AcceptsAMercosulPlateTypedWithASpace(string text, string expectedRest)
    {
        Assert.True(PlateParser.TryExtract(text, out var plate, out var rest));

        Assert.Equal("QWE-1B23", plate);
        Assert.Equal(expectedRest, rest);
    }

    [Theory]
    [InlineData("placa abc 1234", "")]
    [InlineData("gol branco Placa: abc 1234 farol aceso", "gol branco\nfarol aceso")]
    public void TryExtract_AcceptsAnOldPlateTypedWithASpace_WhenLabeledWithPlaca(string text, string expectedRest)
    {
        Assert.True(PlateParser.TryExtract(text, out var plate, out var rest));

        Assert.Equal("ABC-1234", plate);
        Assert.Equal(expectedRest, rest);
    }

    [Theory]
    [InlineData("gol 2015 branco")]
    [InlineData("culto dia 2025")]
    [InlineData("gol branco abc 1234 farol aceso")]
    [InlineData("abc 1234")]
    public void TryExtract_IgnoresAWordAndANumberSeparatedOnlyBySpaces(string text)
    {
        Assert.False(PlateParser.TryExtract(text, out _, out _));
    }

    [Theory]
    [InlineData("gol branco abc-1234")]
    [InlineData("gol branco abc- 1234")]
    [InlineData("gol branco abc -1234")]
    [InlineData("gol branco abc - 1234")]
    public void TryExtract_AcceptsAHyphenWithOptionalSpacesAroundIt(string text)
    {
        Assert.True(PlateParser.TryExtract(text, out var plate, out var rest));

        Assert.Equal("ABC-1234", plate);
        Assert.Equal("gol branco", rest);
    }

    [Theory]
    [InlineData("gol 2015 branco abc1234", "ABC-1234", "gol 2015 branco")]
    [InlineData("QWE 1B23 xyz5678", "XYZ-5678", "QWE 1B23")]
    [InlineData("abc1234 QWE 1B23", "ABC-1234", "QWE 1B23")]
    [InlineData("QWE 1B23 abc-1234", "ABC-1234", "QWE 1B23")]
    public void TryExtract_PrefersAPlateWrittenInOnePiece_WhereverItIs(string text, string expectedPlate, string expectedRest)
    {
        Assert.True(PlateParser.TryExtract(text, out var plate, out var rest));

        Assert.Equal(expectedPlate, plate);
        Assert.Equal(expectedRest, rest);
    }

    // A plate in one piece wins even over a spaced plate that carries the "placa" label.
    [Fact]
    public void TryExtract_PrefersAPlateWrittenInOnePiece_EvenWhenAnotherOneIsLabeled()
    {
        Assert.True(PlateParser.TryExtract("placa abc 1234 gol xyz5678", out var plate, out var rest));

        Assert.Equal("XYZ-5678", plate);
        Assert.Equal("placa abc 1234 gol", rest);
    }

    [Fact]
    public void TryExtract_FindsAMercosulPlateJoinedByAHyphenInFreeText()
    {
        Assert.True(PlateParser.TryExtract("onix preto abc-1d23 vidro aberto", out var plate, out var rest));

        Assert.Equal("ABC-1D23", plate);
        Assert.Equal("onix preto\nvidro aberto", rest);
    }

    [Fact]
    public void TryExtract_TakesTheFirstSpacedPlate_WhenNoneIsWrittenInOnePiece()
    {
        Assert.True(PlateParser.TryExtract("QWE 1B23 ABC 1D23", out var plate, out var rest));

        Assert.Equal("QWE-1B23", plate);
        Assert.Equal("ABC 1D23", rest);
    }

    [Theory]
    [InlineData("abc1234 gol branco", "gol branco")]
    [InlineData("gol branco abc1234", "gol branco")]
    [InlineData("abc1234", "")]
    public void TryExtract_FindsThePlateAtEitherEndOfTheText(string text, string expectedRest)
    {
        Assert.True(PlateParser.TryExtract(text, out var plate, out var rest));

        Assert.Equal("ABC-1234", plate);
        Assert.Equal(expectedRest, rest);
    }

    [Theory]
    [InlineData("gol branco (abc1234)")]
    [InlineData("gol branco abc1234.")]
    [InlineData("gol branco,abc1234,farol")]
    public void TryExtract_AcceptsPunctuationAsDelimiter(string text)
    {
        Assert.True(PlateParser.TryExtract(text, out var plate, out _));

        Assert.Equal("ABC-1234", plate);
    }

    [Theory]
    [InlineData("abc12345")] // 8 letters and digits in a row
    [InlineData("xabc1234")] // glued to a letter before
    [InlineData("abc1234x")] // glued to a letter after
    [InlineData("abc1234\U000000E9")] // glued to a non-ASCII letter after
    [InlineData("placaffo4745")] // glued to the word "placa"
    public void TryExtract_IgnoresCandidatesGluedToOtherLettersOrDigits(string text)
    {
        Assert.False(PlateParser.TryExtract(text, out _, out _));
    }

    [Theory]
    // Ordinary words that fit the letter/number slots but have no digit at all.
    [InlineData("por motivos de seguranca")]
    [InlineData("vizinho furioso")]
    [InlineData("carros famosos")]
    public void TryExtract_IgnoresWordsWithoutAnyDigit(string text)
    {
        Assert.False(PlateParser.TryExtract(text, out _, out _));
    }

    [Fact]
    public void TryExtract_KeepsLookingAfterADigitlessMatch()
    {
        Assert.True(PlateParser.TryExtract("por motivos abc1234", out var plate, out var rest));

        Assert.Equal("ABC-1234", plate);
        Assert.Equal("por motivos", rest);
    }

    // Line breaks survive in rest (as \n) so callers can split segments on them; spaces and tabs collapse inside
    // each line, every line is trimmed and lines left empty are dropped.
    [Theory]
    [InlineData("gol branco\nabc1234\n\nfarol aceso \U0001F697", "gol branco\nfarol aceso \U0001F697")] // plate alone on its line, blank line, emoji
    [InlineData("Gol branco\nPlaca: ABC1234\nFarol aceso", "Gol branco\nFarol aceso")] // label and plate alone on their line
    [InlineData("gol branco abc1234\nfarol aceso", "gol branco\nfarol aceso")] // plate at the end of a line
    [InlineData("  gol   branco \r\n Placa:  abc1234 \n\n\tfarol  aceso ", "gol branco\nfarol aceso")] // CRLF, tabs, ragged spaces
    public void TryExtract_KeepsLineBreaksInRest(string text, string expectedRest)
    {
        Assert.True(PlateParser.TryExtract(text, out var plate, out var rest));

        Assert.Equal("ABC-1234", plate);
        Assert.Equal(expectedRest, rest);
    }

    [Fact]
    public void TryExtract_TidiesEachLineToo_WhenThereIsNoPlate()
    {
        var found = PlateParser.TryExtract("corolla  prata \r\n\r\n  vidro\taberto ", out var plate, out var rest);

        Assert.False(found);
        Assert.Equal("", plate);
        Assert.Equal("corolla prata\nvidro aberto", rest);
    }

    [Fact]
    public void TryExtract_OnlyRemovesThePlacaWordThatLabelsThePlate()
    {
        Assert.True(PlateParser.TryExtract("gol branco abc1234 placa solta", out var plate, out var rest));

        Assert.Equal("ABC-1234", plate);
        Assert.Equal("gol branco\nplaca solta", rest);
    }

    [Fact]
    public void TryExtract_TakesTheFirstPlateAndLeavesTheOthersInTheText()
    {
        Assert.True(PlateParser.TryExtract("abc1234 xyz5678", out var plate, out var rest));

        Assert.Equal("ABC-1234", plate);
        Assert.Equal("xyz5678", rest);
    }

    [Theory]
    [InlineData("gol branco abc1234 farol", "abc1234")]
    [InlineData("gol placa: abc-1234 farol", "placa: abc-1234")]
    [InlineData("gol qwe 1b23 farol", "qwe 1b23")]
    [InlineData("creta gbr- 9003", "gbr- 9003")]
    public void TryLocate_GivesTheSpanOfThePlateAndItsLabel(string text, string expected)
    {
        Assert.True(PlateParser.TryLocate(text, out var index, out var length));

        Assert.Equal(expected, text.Substring(index, length));
    }

    [Fact]
    public void TryLocate_FindsTheSamePlateAsTryExtract()
    {
        const string text = "gol 2015 abc 1234 qwe1b23";

        Assert.True(PlateParser.TryExtract(text, out var plate, out _));
        Assert.True(PlateParser.TryLocate(text, out var index, out var length));

        Assert.Equal(plate, PlateParser.Normalize(text.Substring(index, length).Replace("placa", "", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void TryLocate_ReturnsFalseWhenThereIsNoPlate()
    {
        Assert.False(PlateParser.TryLocate("gol branco farol aceso", out var index, out var length));
        Assert.Equal(0, index);
        Assert.Equal(0, length);
    }
}
