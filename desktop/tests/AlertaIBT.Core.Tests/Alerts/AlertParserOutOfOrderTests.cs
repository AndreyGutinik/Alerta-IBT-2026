using AlertaIBT.Core.Alerts;

namespace AlertaIBT.Core.Tests.Alerts;

// Messages typed out of order that the offline parser reads without Gemini (the same cases are in shared/parser-cases.json).
public class AlertParserOutOfOrderTests
{
    [Theory]
    [InlineData("fiat abc1234 uno preto", "FIAT UNO", "PRETO", "")]
    [InlineData("fiat abc1234 uno preto chave dentro", "FIAT UNO", "PRETO", "CHAVE DENTRO")]
    [InlineData("fiat abc1234 uno azul marinho vidro aberto", "FIAT UNO", "AZUL MARINHO", "VIDRO ABERTO")]
    [InlineData("hb20 abc1234 sedan prata farol aceso", "HB20 SEDAN", "PRATA", "FAROL ACESO")]
    [InlineData("Prata, Gol, ABC-1234", "GOL", "PRATA", "")]
    [InlineData("Prata, Gol, farol aceso, ABC-1234", "GOL", "PRATA", "FAROL ACESO")]
    [InlineData("azul marinho, onix, abc1d23", "ONIX", "AZUL MARINHO", "")]
    [InlineData("ABC-1234, prata, gol", "GOL", "PRATA", "")]
    public void The_color_and_the_model_are_found_wherever_they_were_typed(string raw, string model, string color, string reason)
    {
        var alert = AlertParser.Parse(raw);

        Assert.Equal(model, alert.Model);
        Assert.Equal(color, alert.Color);
        Assert.Equal(reason, alert.Reason);
        Assert.NotNull(alert.Plate);
    }

    // What must keep working exactly as before.
    [Theory]
    [InlineData("civic preto abc1234 chave dentro do carro", "CIVIC", "PRETO", "CHAVE DENTRO DO CARRO")]
    [InlineData("gol abc1234 branco farol aceso", "GOL", "BRANCO", "FAROL ACESO")]
    [InlineData("gol branco abc1234 farol aceso", "GOL", "BRANCO", "FAROL ACESO")]
    [InlineData("gol, branco, abc1234", "GOL", "BRANCO", "")]
    [InlineData("gol,2015 branco abc1234", "GOL", "", "2015 BRANCO")]
    [InlineData("prata abc1d23 vidro aberto", "", "PRATA", "VIDRO ABERTO")]
    public void The_usual_orders_read_as_before(string raw, string model, string color, string reason)
    {
        var alert = AlertParser.Parse(raw);

        Assert.Equal(model, alert.Model);
        Assert.Equal(color, alert.Color);
        Assert.Equal(reason, alert.Reason);
    }

    // Where it would be a guess the color is not taken, so a member is asked for it instead of seeing a wrong notice.
    [Theory]
    [InlineData("gol abc1234 chave dentro do carro azul", "GOL", "", "CHAVE DENTRO DO CARRO AZUL")]
    [InlineData("gol abc1234 uno novo zero preto", "GOL", "", "UNO NOVO ZERO PRETO")]
    [InlineData("gol abc1234 vidro aberto preto", "GOL", "", "VIDRO ABERTO PRETO")]
    [InlineData("prata abc1234 gol", "", "PRATA", "GOL")]
    [InlineData("Prata, vidro aberto, ABC-1234", "", "PRATA", "VIDRO ABERTO")]
    [InlineData("Prata, Gol branco, ABC-1234", "", "PRATA", "GOL BRANCO")]
    public void An_ambiguous_order_is_left_as_it_was(string raw, string model, string color, string reason)
    {
        var alert = AlertParser.Parse(raw);

        Assert.Equal(model, alert.Model);
        Assert.Equal(color, alert.Color);
        Assert.Equal(reason, alert.Reason);
    }

    [Fact]
    public void A_member_whose_message_is_in_the_new_order_is_accepted()
    {
        var decision = AlertRules.Decide(AlertParser.Parse("fiat abc1234 uno preto"), SenderRole.Member);

        Assert.Equal(RuleKind.Vehicle, decision.Kind);
        Assert.Empty(decision.Missing);
    }
}
