using System.Globalization;
using AlertaIBT.Core.Alerts;

namespace AlertaIBT.Core.Tests.Alerts;

public class AlertParserTests
{
    // ---------------------------------------------------------------------------------------------------------
    // Parse + Format: from what people type to the text on the LED
    // ---------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("#creta preto | placa:gbr- 9003 / farol aceso", "CRETA PRETO | PLACA: GBR-9003 | FAROL ACESO")]
    [InlineData("prata corolla bra2e19 vidro aberto", "COROLLA PRATA | PLACA: BRA-2E19 | VIDRO ABERTO")]
    [InlineData("onix preto qwe1b23", "ONIX PRETO | PLACA: QWE-1B23")]
    [InlineData(
        "hb20 vermelho abc1234 parado em frente \U000000E0 garagem",
        "HB20 VERMELHO | PLACA: ABC-1234 | PARADO EM FRENTE \U000000C0 GARAGEM")]
    public void Format_BuildsModelColorPlateAndReason(string raw, string expected)
    {
        Assert.Equal(expected, AlertFormatter.Format(AlertParser.Parse(raw)));
    }

    // A message with line breaks, an emoji and a plate alone on its line becomes one line.
    [Fact]
    public void Format_TurnsAMessageWithLineBreaksAndEmojiIntoOneLine()
    {
        var alert = AlertParser.Parse("gol branco\nffo4745\n\nfarol aceso \U0001F697");

        var text = AlertFormatter.Format(alert);

        Assert.Equal("GOL BRANCO | PLACA: FFO-4745 | FAROL ACESO", text);
        Assert.DoesNotContain("\n", text);
        Assert.Equal("GOL BRANCO FFO4745 FAROL ACESO", alert.FreeText);
    }

    [Fact]
    public void Format_TurnsALongMessageWithManyLineBreaksIntoOneLine()
    {
        var lines = Enumerable.Range(0, 20).Select(_ => "farol aceso e vidro aberto");
        var alert = AlertParser.Parse("gol branco abc1234\n" + string.Join('\n', lines));

        var text = AlertFormatter.Format(alert);

        Assert.DoesNotContain("\n", text);
        Assert.StartsWith("GOL BRANCO | PLACA: ABC-1234 | FAROL ACESO E VIDRO ABERTO, FAROL ACESO", text);
    }

    // Nothing is cut, however long the message is: only whitespace is collapsed.
    [Theory]
    [InlineData(400)]
    [InlineData(5000)]
    public void FormatFree_AcceptsALongText_WithoutCuttingIt(int length)
    {
        // "gato gato gato ...", with the last word stretched ("gatos") to reach the exact length.
        var raw = string.Join(' ', Enumerable.Repeat("gato", (length + 1) / 5)).PadRight(length, 's');
        Assert.Equal(length, raw.Length);

        var alert = AlertParser.Parse(raw);

        Assert.Equal(RuleKind.Free, AlertRules.Decide(alert, SenderRole.Operator).Kind);
        Assert.Equal(raw.ToUpperInvariant(), AlertFormatter.FormatFree(alert.FreeText));
    }

    [Fact]
    public void Format_AcceptsAVehicleMessageWithALongReason_WithoutCuttingIt()
    {
        var reason = "farol " + string.Join(' ', Enumerable.Repeat("aceso", 80));
        var alert = AlertParser.Parse("gol branco abc1234 " + reason);

        Assert.Equal(RuleKind.Vehicle, AlertRules.Decide(alert, SenderRole.Member).Kind);
        Assert.Equal("GOL BRANCO | PLACA: ABC-1234 | " + reason.ToUpperInvariant(), AlertFormatter.Format(alert));
    }

    // ---------------------------------------------------------------------------------------------------------
    // Parse: vehicle, color, plate and reason
    // ---------------------------------------------------------------------------------------------------------

    [Theory]
    // The plate is the boundary between the vehicle and the reason, even when no word says so.
    [InlineData("civic preto abc1234 chave dentro do carro", "CIVIC", "PRETO", "ABC-1234", "CHAVE DENTRO DO CARRO")]
    [InlineData("hb20 vermelho abc1234 parado em frente \U000000E0 garagem", "HB20", "VERMELHO", "ABC-1234", "PARADO EM FRENTE \U000000C0 GARAGEM")]
    [InlineData("#creta preto | placa:gbr- 9003 / farol aceso", "CRETA", "PRETO", "GBR-9003", "FAROL ACESO")]
    // The color goes after the model, wherever it was typed; the other words keep their order.
    [InlineData("prata corolla bra2e19 vidro aberto", "COROLLA", "PRATA", "BRA-2E19", "VIDRO ABERTO")]
    [InlineData("fiat preto uno abc1234", "FIAT UNO", "PRETO", "ABC-1234", "")]
    [InlineData("jeep compass flex preto abc1234", "JEEP COMPASS FLEX", "PRETO", "ABC-1234", "")]
    [InlineData("gol - branco abc1234", "GOL", "BRANCO", "ABC-1234", "")]
    [InlineData("gol prata e preto abc1234", "GOL E PRETO", "PRATA", "ABC-1234", "")] // the first color word is the color
    // A color modifier joins the color.
    [InlineData("gol azul marinho abc1234", "GOL", "AZUL MARINHO", "ABC-1234", "")]
    [InlineData("hilux sw4 cinza metalica abc1234", "HILUX SW4", "CINZA METALICA", "ABC-1234", "")]
    // No color with the vehicle: the one that starts the next segment is taken (with its modifier).
    [InlineData("gol abc1234 branco farol aceso", "GOL", "BRANCO", "ABC-1234", "FAROL ACESO")]
    [InlineData("gol abc1234 azul escuro vidro aberto", "GOL", "AZUL ESCURO", "ABC-1234", "VIDRO ABERTO")]
    [InlineData("gol abc1234 branco", "GOL", "BRANCO", "ABC-1234", "")]
    [InlineData("gol abc1234 branco, farol aceso", "GOL", "BRANCO", "ABC-1234", "FAROL ACESO")]
    // ...but only the segment right after the vehicle, and only when the vehicle has no color of its own.
    [InlineData("gol abc1234 vidro aberto, branco", "GOL", "", "ABC-1234", "VIDRO ABERTO, BRANCO")]
    [InlineData("gol branco abc1234 preto", "GOL", "BRANCO", "ABC-1234", "PRETO")]
    [InlineData("gol farol aceso branco abc1234", "GOL", "", "ABC-1234", "FAROL ACESO BRANCO")]
    // Segments: line breaks, | / ; and commas; every segment after the vehicle is a reason, joined with ", ".
    [InlineData("gol branco abc1234, farol aceso, vidro aberto", "GOL", "BRANCO", "ABC-1234", "FAROL ACESO, VIDRO ABERTO")]
    [InlineData("gol branco - abc1234 - farol aceso", "GOL", "BRANCO", "ABC-1234", "FAROL ACESO")]
    [InlineData("gol branco abc1234 farol aceso, vidro aberto / porta aberta", "GOL", "BRANCO", "ABC-1234", "FAROL ACESO, VIDRO ABERTO, PORTA ABERTA")]
    [InlineData("gol branco abc1234 farol aceso vidro aberto", "GOL", "BRANCO", "ABC-1234", "FAROL ACESO VIDRO ABERTO")]
    [InlineData("gol branco; abc1234; farol aceso", "GOL", "BRANCO", "ABC-1234", "FAROL ACESO")]
    [InlineData("gol branco,\nabc1234\n\nfarol aceso", "GOL", "BRANCO", "ABC-1234", "FAROL ACESO")]
    // Only the first segment that does not start with a trigger is the vehicle.
    [InlineData("gol branco, abc1234, celta prata", "GOL", "BRANCO", "ABC-1234", "CELTA PRATA")]
    // A reason typed before the vehicle stays a reason (and does not hide the color that comes after the vehicle).
    [InlineData("farol aceso, gol branco abc1234", "GOL", "BRANCO", "ABC-1234", "FAROL ACESO")]
    [InlineData("farol aceso, gol abc1234 branco", "GOL", "BRANCO", "ABC-1234", "FAROL ACESO")]
    // Without a plate the trigger word splits the vehicle from the reason.
    [InlineData("gol branco farol aceso", "GOL", "BRANCO", null, "FAROL ACESO")]
    [InlineData("gol branco farol aceso, vidro aberto", "GOL", "BRANCO", null, "FAROL ACESO, VIDRO ABERTO")]
    [InlineData("gol branco far\U000000F3is acesos", "GOL", "BRANCO", null, "FAR\U000000D3IS ACESOS")]
    [InlineData("gol preto som alto abc1234", "GOL", "PRETO", "ABC-1234", "SOM ALTO")]
    [InlineData("gol branco abc1234 estacionado na contram\U000000E3o", "GOL", "BRANCO", "ABC-1234", "ESTACIONADO NA CONTRAM\U000000C3O")]
    // The trigger and everything after it make one reason; everything before it is the vehicle.
    [InlineData("gol branco abc1234 farol aceso e vidro aberto", "GOL", "BRANCO", "ABC-1234", "FAROL ACESO E VIDRO ABERTO")]
    [InlineData("gol branco farol aceso e vidro aberto abc1234", "GOL", "BRANCO", "ABC-1234", "FAROL ACESO E VIDRO ABERTO")]
    // Missing parts come back empty, never null (the plate is null).
    [InlineData("corolla abc1234 vidro aberto", "COROLLA", "", "ABC-1234", "VIDRO ABERTO")]
    [InlineData("prata abc1d23", "", "PRATA", "ABC-1D23", "")]
    [InlineData("abc1234", "", "", "ABC-1234", "")]
    [InlineData("abc1234 vidro aberto", "", "", "ABC-1234", "VIDRO ABERTO")]
    [InlineData("farol aceso", "", "", null, "FAROL ACESO")]
    [InlineData("   ", "", "", null, "")]
    // Edges of a segment: anything that is not a letter or a digit is trimmed, and so is a lone symbol word.
    [InlineData("(gol branco) abc1234 [farol aceso]", "GOL", "BRANCO", "ABC-1234", "FAROL ACESO")]
    [InlineData("gol branco (abc1234) farol aceso", "GOL", "BRANCO", "ABC-1234", "FAROL ACESO")]
    [InlineData("gol branco abc1234 vidro & farol aberto", "GOL", "BRANCO", "ABC-1234", "VIDRO FAROL ABERTO")]
    // The word "placa" is never part of a model; in a reason it stays.
    [InlineData("placa gol branco abc1234", "GOL", "BRANCO", "ABC-1234", "")]
    [InlineData("gol branco abc1234 placa solta", "GOL", "BRANCO", "ABC-1234", "PLACA SOLTA")]
    // Case does not matter.
    [InlineData("GOL BRANCO ABC1234 FAROL ACESO", "GOL", "BRANCO", "ABC-1234", "FAROL ACESO")]
    [InlineData("Gol Branco Abc1234 Farol Aceso", "GOL", "BRANCO", "ABC-1234", "FAROL ACESO")]
    // Punctuation stuck to a word does not hide it: a word is compared without the characters at its ends that are not
    // letters or digits, and goes into the model and the color without them. The same trimming applies to the two halves
    // of a vehicle segment cut at a trigger, and to what is left of a segment after its color was taken.
    [InlineData("gol branco. farol aceso abc1234", "GOL", "BRANCO", "ABC-1234", "FAROL ACESO")]
    [InlineData("gol branco (farol aceso) abc1234", "GOL", "BRANCO", "ABC-1234", "FAROL ACESO")]
    [InlineData("gol, branco: abc1234 farol aceso!", "GOL", "BRANCO", "ABC-1234", "FAROL ACESO")]
    [InlineData("gol (branco) farol aceso abc1234", "GOL", "BRANCO", "ABC-1234", "FAROL ACESO")]
    [InlineData("gol azul marinho. farol aceso abc1234", "GOL", "AZUL MARINHO", "ABC-1234", "FAROL ACESO")]
    [InlineData("hb20. vermelho farol aceso abc1234", "HB20", "VERMELHO", "ABC-1234", "FAROL ACESO")]
    [InlineData("gol (flex) branco abc1234", "GOL FLEX", "BRANCO", "ABC-1234", "")]
    [InlineData("placa: gol branco abc1234", "GOL", "BRANCO", "ABC-1234", "")]
    [InlineData("gol abc1234 branco. farol aceso", "GOL", "BRANCO", "ABC-1234", "FAROL ACESO")]
    [InlineData("gol abc1234 branco (farol aceso)", "GOL", "BRANCO", "ABC-1234", "FAROL ACESO")]
    [InlineData("gol branco (som alto) abc1234", "GOL", "BRANCO", "ABC-1234", "SOM ALTO")]
    [InlineData("gol branco abc1234 farol aceso. vidro aberto", "GOL", "BRANCO", "ABC-1234", "FAROL ACESO. VIDRO ABERTO")] // inside a reason the punctuation stays as typed
    // A comma with a digit on each side is part of a number, not a separator.
    [InlineData("gol 1,0 branco abc1234", "GOL 1,0", "BRANCO", "ABC-1234", "")]
    [InlineData("gol 1,0, branco abc1234", "GOL 1,0", "BRANCO", "ABC-1234", "")]
    [InlineData("gol 1,0 branco abc1234, farol aceso", "GOL 1,0", "BRANCO", "ABC-1234", "FAROL ACESO")]
    [InlineData("gol 2015, branco abc1234", "GOL 2015", "BRANCO", "ABC-1234", "")] // a digit on one side only: still a separator
    [InlineData("gol branco 2015, chave dentro abc1234", "GOL 2015", "BRANCO", "ABC-1234", "CHAVE DENTRO")] // digit before only
    [InlineData("gol,2015 branco abc1234", "GOL", "", "ABC-1234", "2015 BRANCO")] // digit after only
    public void Parse_SplitsVehicleColorPlateAndReason(string raw, string model, string color, string? plate, string reason)
    {
        var alert = AlertParser.Parse(raw);

        Assert.Equal(model, alert.Model);
        Assert.Equal(color, alert.Color);
        Assert.Equal(plate, alert.Plate);
        Assert.Equal(reason, alert.Reason);
    }

    // Each separator ends the vehicle even when the reason does not start with a trigger word and neither the plate
    // nor a line break marks the boundary.
    [Theory]
    [InlineData(" | ")]
    [InlineData("|")]
    [InlineData(" / ")]
    [InlineData("; ")]
    [InlineData(", ")]
    [InlineData("\n")]
    public void Parse_EndsTheVehicleAtEachSeparator(string separator)
    {
        var alert = AlertParser.Parse($"gol branco{separator}chave dentro abc1234");

        Assert.Equal("GOL", alert.Model);
        Assert.Equal("BRANCO", alert.Color);
        Assert.Equal("ABC-1234", alert.Plate);
        Assert.Equal("CHAVE DENTRO", alert.Reason);
    }

    [Fact]
    public void Parse_FillsEveryField()
    {
        var alert = AlertParser.Parse("#hb20 vermelho | placa: abc1234 | parado em frente \U000000E0 garagem");

        Assert.Equal(
            new ParsedAlert(
                "HB20",
                "VERMELHO",
                "ABC-1234",
                "PARADO EM FRENTE \U000000C0 GARAGEM",
                "HB20 VERMELHO | PLACA: ABC1234 | PARADO EM FRENTE \U000000C0 GARAGEM"),
            alert);
    }

    // FreeText is always filled: the cleaned text, one line, upper case, punctuation as typed.
    [Theory]
    [InlineData("gol branco farol aceso", "GOL BRANCO FAROL ACESO")]
    [InlineData("gol branco\nffo4745\n\nfarol aceso \U0001F697", "GOL BRANCO FFO4745 FAROL ACESO")]
    [InlineData("###gol   branco", "GOL BRANCO")]
    [InlineData("  #gol\tbranco \r\n ", "GOL BRANCO")]
    [InlineData("\U0001F697 #gol branco", "GOL BRANCO")]
    [InlineData("pais do pedro, sala kids | favor / comparecer; ok", "PAIS DO PEDRO, SALA KIDS | FAVOR / COMPARECER; OK")]
    [InlineData("gol #2 branco", "GOL #2 BRANCO")]
    [InlineData("", "")]
    [InlineData("#", "")]
    public void Parse_KeepsTheCleanedTextAsFreeText(string raw, string expected)
    {
        Assert.Equal(expected, AlertParser.Parse(raw).FreeText);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Vocabulary: colors, modifiers and the words that start a reason
    // ---------------------------------------------------------------------------------------------------------

    public static TheoryData<string> ColorWords() => Words(
        "preto preta branco branca prata cinza grafite vermelho vermelha azul verde amarelo amarela bege marrom " +
        "dourado dourada vinho laranja rosa roxo roxa champanhe");

    [Theory]
    [MemberData(nameof(ColorWords))]
    public void Parse_RecognizesEveryColorWord(string color)
    {
        var alert = AlertParser.Parse($"gol {color} abc1234");

        Assert.Equal("GOL", alert.Model);
        Assert.Equal(color.ToUpperInvariant(), alert.Color);
    }

    public static TheoryData<string> ColorModifierWords() => Words("escuro escura claro clara metalico metalica fosco fosca marinho");

    [Theory]
    [MemberData(nameof(ColorModifierWords))]
    public void Parse_JoinsEveryColorModifierToTheColor(string modifier)
    {
        var alert = AlertParser.Parse($"gol azul {modifier} abc1234");

        Assert.Equal("GOL", alert.Model);
        Assert.Equal($"AZUL {modifier.ToUpperInvariant()}", alert.Color);
    }

    [Fact]
    public void Parse_MatchesColorsAndModifiersWithoutCaringAboutAccentsOrCase()
    {
        var alert = AlertParser.Parse("onix CINZA Met\U000000E1lico abc1234");

        Assert.Equal("ONIX", alert.Model);
        Assert.Equal("CINZA MET\U000000C1LICO", alert.Color);
    }

    [Fact]
    public void Parse_OnlyTakesAModifierThatComesRightAfterTheColor()
    {
        var alert = AlertParser.Parse("gol escuro azul abc1234");

        Assert.Equal("GOL ESCURO", alert.Model);
        Assert.Equal("AZUL", alert.Color);
    }

    public static TheoryData<string> TriggerWords() => Words(
        "farol far\U000000F3is vidro vidros estacionado estacionada parado parada porta portas pisca pisca-pisca luz luzes " +
        "alarme pneu pneus dono donos comparecer bloqueando vaga vagas contram\U000000E3o proibido proibida aceso acesa " +
        "acesos som sons FAROL Vidro");

    [Theory]
    [MemberData(nameof(TriggerWords))]
    public void Parse_StartsTheReasonAtATriggerWord(string trigger)
    {
        var alert = AlertParser.Parse($"gol branco {trigger} aberto");

        Assert.Equal("GOL", alert.Model);
        Assert.Equal("BRANCO", alert.Color);
        Assert.Equal($"{trigger} aberto".ToUpperInvariant(), alert.Reason);
    }

    // Words that only look like a trigger: "som" and "sons" count only as whole words, and the others must start
    // with the trigger ("parati" is a car, "porto" is a city, "reparado" and "abandono" only contain one).
    public static TheoryData<string> NotTriggerWords() => Words(
        "somente sombra soma parati porto paralama dona faro vidraria reparado importa abandono");

    [Theory]
    [MemberData(nameof(NotTriggerWords))]
    public void Parse_KeepsAWordThatIsNotATriggerInTheVehicle(string word)
    {
        var alert = AlertParser.Parse($"gol branco {word} abc1234");

        Assert.Equal($"GOL {word.ToUpperInvariant()}", alert.Model);
        Assert.Equal("BRANCO", alert.Color);
        Assert.Equal("", alert.Reason);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Cleaning: emoji and pictographs, the leading #, upper case
    // ---------------------------------------------------------------------------------------------------------

    public static TheoryData<string> Pictographs() =>
    [
        "\U0001F697", // car (Other Symbol)
        "\U00002764\U0000FE0F", // heart + emoji presentation selector
        "\U00002764\U0000FE0E", // heart + text presentation selector
        "\U0001F468\U0000200D\U0001F469\U0000200D\U0001F467", // family: people joined by zero width joiners
        "\U0001F44D\U0001F3FD", // thumbs up + skin tone (Modifier Symbol)
        "\U0001F1E7\U0001F1F7", // flag: two regional indicators
        "\U00002705", // check mark
        "\U000026A0\U0000FE0F", // warning sign + emoji presentation selector
        "\U000000A9", // copyright sign: any Other Symbol goes
        "\U000000A8", // diaeresis: any Modifier Symbol goes
        "\U0000200D", // a zero width joiner alone
        "\U000020E3", // a combining enclosing keycap alone (Enclosing Mark)
        "\U000020DD", // combining enclosing circle: any Enclosing Mark goes
    ];

    [Theory]
    [MemberData(nameof(Pictographs))]
    public void Parse_DropsPictographs(string pictograph)
    {
        var alert = AlertParser.Parse($"{pictograph}gol {pictograph} branco abc1234 {pictograph} farol aceso{pictograph}");

        Assert.Equal("GOL", alert.Model);
        Assert.Equal("BRANCO", alert.Color);
        Assert.Equal("ABC-1234", alert.Plate);
        Assert.Equal("FAROL ACESO", alert.Reason);
        Assert.Equal("GOL BRANCO ABC1234 FAROL ACESO", alert.FreeText);
    }

    [Theory]
    [MemberData(nameof(Pictographs))]
    public void FormatFree_DropsPictographs(string pictograph)
    {
        Assert.Equal("GOL BRANCO", AlertFormatter.FormatFree($"gol {pictograph} branco {pictograph}"));
    }

    [Fact]
    public void FormatFree_DropsALoneSurrogate_ItCannotBeEncoded()
    {
        Assert.Equal("GOL BRANCO", AlertFormatter.FormatFree("gol" + (char)0xD83D + " branco"));
    }

    [Fact]
    public void FormatFree_KeepsCombiningMarksAndSymbolsThatAreNotPictographs()
    {
        // Math and currency symbols and punctuation are plain text; a combining acute accent is part of a letter.
        Assert.Equal("R$ 10 + 5 = 15 | A/B < C ~ D", AlertFormatter.FormatFree("r$ 10 + 5 = 15 | a/b < c ~ d"));
        Assert.Equal("CAFE\U00000301", AlertFormatter.FormatFree("cafe\U00000301"));
    }

    // A keycap emoji is a digit, a variation selector and an enclosing keycap: the digit stays, the rest goes.
    [Fact]
    public void Parse_DropsTheEnclosingKeycapButKeepsItsDigit()
    {
        var alert = AlertParser.Parse("gol branco abc1234 1\U0000FE0F\U000020E3 farol aceso");

        Assert.Equal("1 FAROL ACESO", alert.Reason);
        Assert.Equal("GOL BRANCO | PLACA: ABC-1234 | 1 FAROL ACESO", AlertFormatter.Format(alert));
        Assert.Equal("GOL BRANCO ABC1234 1 FAROL ACESO", alert.FreeText);
    }

    [Fact]
    public void Parse_RemovesEveryLeadingHash_AfterTrimming()
    {
        var alert = AlertParser.Parse("  ###gol branco abc1234");

        Assert.Equal("GOL", alert.Model);
        Assert.Equal("GOL BRANCO ABC1234", alert.FreeText);
    }

    [Fact]
    public void Format_UppercasesWithPtBr_WhateverTheCurrentCulture()
    {
        var original = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
        try
        {
            // In Turkish the capital of "i" is a dotted I; in pt-BR it is a plain I.
            var alert = AlertParser.Parse("ilha branca abc1234 farol aceso");

            Assert.Equal("ILHA BRANCA | PLACA: ABC-1234 | FAROL ACESO", AlertFormatter.Format(alert));
            Assert.Equal("ILHA", AlertFormatter.FormatFree("ilha"));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    // Format and FormatFree
    // ---------------------------------------------------------------------------------------------------------

    // A ParsedAlert built elsewhere (the Gemini corrector) may come in any case.
    [Fact]
    public void Format_UppercasesEveryField()
    {
        var alert = new ParsedAlert("hb20", "vermelho", "abc-1234", "parado \U000000E0 frente", "hb20 vermelho");

        Assert.Equal("HB20 VERMELHO | PLACA: ABC-1234 | PARADO \U000000C0 FRENTE", AlertFormatter.Format(alert));
    }

    [Theory]
    [InlineData("gol", "branco", "abc-1234", "farol aceso", "GOL BRANCO | PLACA: ABC-1234 | FAROL ACESO")]
    [InlineData("gol", "branco", null, "farol aceso", "GOL BRANCO | FAROL ACESO")]
    [InlineData("gol", "branco", "abc-1234", "", "GOL BRANCO | PLACA: ABC-1234")]
    [InlineData("gol", "", "abc-1234", "farol aceso", "GOL | PLACA: ABC-1234 | FAROL ACESO")]
    [InlineData("", "prata", "abc-1d23", "", "PRATA | PLACA: ABC-1D23")]
    [InlineData("", "", "abc-1234", "vidro aberto", "PLACA: ABC-1234 | VIDRO ABERTO")]
    [InlineData("", "", "abc-1234", "", "PLACA: ABC-1234")]
    [InlineData("", "", null, "vidro aberto", "VIDRO ABERTO")]
    [InlineData("gol", "", null, "", "GOL")]
    [InlineData("", "", null, "", "")]
    [InlineData("gol", "branco", "   ", "farol", "GOL BRANCO | FAROL")]
    [InlineData("  gol  ", "branco ", "abc-1234", " farol\n  aceso ", "GOL BRANCO | PLACA: ABC-1234 | FAROL ACESO")]
    [InlineData("gol", "branco", "abc-1234", "\U0001F697", "GOL BRANCO | PLACA: ABC-1234")]
    [InlineData("gol \U0001F697", "branco", "abc-1234", "farol aceso \U0001F697", "GOL BRANCO | PLACA: ABC-1234 | FAROL ACESO")]
    public void Format_DropsEmptyPartsWithTheirSeparator(string model, string color, string? plate, string reason, string expected)
    {
        var alert = new ParsedAlert(model, color, plate, reason, "texto");

        Assert.Equal(expected, AlertFormatter.Format(alert));
    }

    [Theory]
    [InlineData("pais do pedro henrique, sala kids, favor comparecer \U000000E0 recep\U000000E7\U000000E3o", "PAIS DO PEDRO HENRIQUE, SALA KIDS, FAVOR COMPARECER \U000000C0 RECEP\U000000C7\U000000C3O")]
    [InlineData("  pais do\r\n pedro,\t sala   kids \n", "PAIS DO PEDRO, SALA KIDS")]
    [InlineData("a\U00002028b\U00002029c\U00000085d", "A B C D")]
    [InlineData("#gol | branco", "#GOL | BRANCO")]
    [InlineData("", "")]
    [InlineData(" \n\t ", "")]
    public void FormatFree_CollapsesWhitespaceAndUppercases(string text, string expected)
    {
        Assert.Equal(expected, AlertFormatter.FormatFree(text));
    }

    // ---------------------------------------------------------------------------------------------------------
    // AlertRules
    // ---------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("gol", "branco", "abc-1234")]
    [InlineData("gol", "branco", null, "placa")]
    [InlineData("gol", "", "abc-1234", "cor")]
    [InlineData("", "branco", "abc-1234", "modelo")]
    [InlineData("gol", "", null, "cor", "placa")]
    [InlineData("", "branco", null, "modelo", "placa")]
    [InlineData("", "", "abc-1234", "modelo", "cor")]
    [InlineData("", "", null, "modelo", "cor", "placa")]
    [InlineData("  ", "\t", " ", "modelo", "cor", "placa")]
    public void Missing_ListsModeloCorPlaca_InThatOrder(string model, string color, string? plate, params string[] expected)
    {
        var alert = new ParsedAlert(model, color, plate, "", "texto");

        Assert.Equal(expected, AlertRules.Missing(alert));
    }

    [Theory]
    [InlineData("gol branco farol aceso", "placa")]
    [InlineData("corolla abc1234 vidro aberto", "cor")]
    [InlineData("prata abc1d23", "modelo")]
    public void Decide_RejectsAMember_AndNamesWhatIsMissing(string raw, string missing)
    {
        var decision = AlertRules.Decide(AlertParser.Parse(raw), SenderRole.Member);

        Assert.Equal(RuleKind.Reject, decision.Kind);
        Assert.Equal(new[] { missing }, decision.Missing);
    }

    [Theory]
    [InlineData(SenderRole.Operator)]
    [InlineData(SenderRole.Admin)]
    public void Decide_TakesAnAnnouncementWithoutPlateAsFreeText_ForOperatorAndAdmin(SenderRole role)
    {
        var decision = AlertRules.Decide(AlertParser.Parse("gol branco farol aceso"), role);

        Assert.Equal(RuleKind.Free, decision.Kind);
        Assert.Equal(new[] { "placa" }, decision.Missing);
    }

    [Theory]
    [InlineData(SenderRole.Member)]
    [InlineData(SenderRole.Operator)]
    [InlineData(SenderRole.Admin)]
    public void Decide_AcceptsAVehicle_WhenNothingIsMissing(SenderRole role)
    {
        var decision = AlertRules.Decide(AlertParser.Parse("gol branco abc1234 farol aceso"), role);

        Assert.Equal(RuleKind.Vehicle, decision.Kind);
        Assert.Empty(decision.Missing);
    }

    // Operator and admin only need the plate; the parts that are missing just vanish from the text.
    [Theory]
    [InlineData(SenderRole.Operator)]
    [InlineData(SenderRole.Admin)]
    public void Decide_TakesAVehicleWithAPlate_EvenWhenModelOrColorIsMissing(SenderRole role)
    {
        var onlyReason = AlertParser.Parse("abc1234 vidro aberto");
        var noColor = AlertParser.Parse("corolla abc1234 vidro aberto");

        var decision = AlertRules.Decide(onlyReason, role);
        Assert.Equal(RuleKind.Vehicle, decision.Kind);
        Assert.Equal(new[] { "modelo", "cor" }, decision.Missing);
        Assert.Equal("PLACA: ABC-1234 | VIDRO ABERTO", AlertFormatter.Format(onlyReason));

        decision = AlertRules.Decide(noColor, role);
        Assert.Equal(RuleKind.Vehicle, decision.Kind);
        Assert.Equal(new[] { "cor" }, decision.Missing);
        Assert.Equal("COROLLA | PLACA: ABC-1234 | VIDRO ABERTO", AlertFormatter.Format(noColor));
    }

    // Nothing typed, or only # and emoji: refused for everybody.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("#")]
    [InlineData("###  ")]
    [InlineData("\U0001F697")]
    [InlineData("# \U0001F697 \n")]
    public void Decide_Rejects_WhenNothingIsLeftToShow(string raw)
    {
        var alert = AlertParser.Parse(raw);

        Assert.All(
            Enum.GetValues<SenderRole>(),
            role =>
            {
                var decision = AlertRules.Decide(alert, role);
                Assert.Equal(RuleKind.Reject, decision.Kind);
                Assert.Equal(new[] { "modelo", "cor", "placa" }, decision.Missing);
            });
    }

    [Fact]
    public void Decide_Rejects_WhenFreeTextIsEmpty_EvenIfOtherFieldsAreFilled()
    {
        var alert = new ParsedAlert("gol", "branco", "abc-1234", "farol aceso", "");

        Assert.All(Enum.GetValues<SenderRole>(), role => Assert.Equal(RuleKind.Reject, AlertRules.Decide(alert, role).Kind));
    }

    [Fact]
    public void Decide_AlwaysReportsTheMissingParts_WhateverTheKind()
    {
        var reject = AlertRules.Decide(AlertParser.Parse("corolla prata"), SenderRole.Member);
        var free = AlertRules.Decide(AlertParser.Parse("corolla prata farol aceso"), SenderRole.Operator);
        var vehicle = AlertRules.Decide(AlertParser.Parse("prata abc1234"), SenderRole.Admin);

        Assert.Equal(RuleKind.Reject, reject.Kind);
        Assert.Equal(new[] { "placa" }, reject.Missing);
        Assert.Equal(RuleKind.Free, free.Kind);
        Assert.Equal(new[] { "placa" }, free.Missing);
        Assert.Equal(RuleKind.Vehicle, vehicle.Kind);
        Assert.Equal(new[] { "modelo" }, vehicle.Missing);
    }

    // An unknown role gets the strictest rules.
    [Fact]
    public void Decide_TreatsAnUnknownRoleLikeAMember()
    {
        var decision = AlertRules.Decide(AlertParser.Parse("gol branco farol aceso"), (SenderRole)99);

        Assert.Equal(RuleKind.Reject, decision.Kind);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Null arguments
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void Members_Throw_WhenGivenNull()
    {
        Assert.Throws<ArgumentNullException>(() => AlertParser.Parse(null!));
        Assert.Throws<ArgumentNullException>(() => AlertFormatter.Format(null!));
        Assert.Throws<ArgumentNullException>(() => AlertFormatter.FormatFree(null!));
        Assert.Throws<ArgumentNullException>(() => AlertRules.Missing(null!));
        Assert.Throws<ArgumentNullException>(() => AlertRules.Decide(null!, SenderRole.Member));
    }

    private static TheoryData<string> Words(string list)
    {
        var data = new TheoryData<string>();
        foreach (var word in list.Split(' '))
        {
            data.Add(word);
        }

        return data;
    }
}
