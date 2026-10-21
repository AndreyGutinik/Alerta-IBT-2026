using AlertaIBT.Core.Correction;

namespace AlertaIBT.Core.Tests.Correction;

public class BuiltInFixedRulesTests
{
    private static readonly string[] Adjectives = new[]
    {
        "aberto", "fechado", "quebrado", "trincado", "riscado", "baixado", "levantado", "entreaberto",
    }.SelectMany(a => new[] { a, a[..^1] + "a", a + "s", a[..^1] + "as" }).ToArray();

    private static FixedRules Rules(string user = "") => new FixedRulesProvider(() => user).Current();

    [Fact]
    public void The_rules_are_generated_lines_in_the_fixed_rule_format()
    {
        var lines = BuiltInFixedRules.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(Adjectives.Length * 2, lines.Length);
        Assert.Contains("video aberto => vidro aberto", lines);
        Assert.Contains("videos entreabertas => vidros entreabertas", lines);
        Assert.Equal(lines.Length, FixedRules.Inspect(BuiltInFixedRules.Text).ValidCount);
        Assert.Empty(FixedRules.Inspect(BuiltInFixedRules.Text).RepeatedLines);
    }

    [Theory]
    [InlineData("video aberto", "vidro aberto")]
    [InlineData("Video Aberto", "vidro aberto")]
    [InlineData("v\U000000EDdeo aberto", "vidro aberto")]
    [InlineData("videos abertos", "vidros abertos")]
    [InlineData("video fechada", "vidro fechada")]
    [InlineData("video  quebrado", "vidro quebrado")]
    [InlineData("farol aceso e video entreaberto", "farol aceso e vidro entreaberto")]
    [InlineData("video trincado e video riscado", "vidro trincado e vidro riscado")]
    [InlineData("video baixado", "vidro baixado")]
    [InlineData("videos levantadas", "vidros levantadas")]
    public void Video_with_a_window_adjective_becomes_vidro(string text, string expected)
    {
        Assert.Equal(expected, Rules().Apply(text), ignoreCase: true);
    }

    [Theory]
    [InlineData("video")]
    [InlineData("video do culto")]
    [InlineData("o video esta aberto")]
    [InlineData("videoconferencia aberta")]
    [InlineData("video aberturas")]
    [InlineData("vidro aberto")]
    [InlineData("televideo aberto")]
    public void Anything_else_is_left_alone(string text)
    {
        Assert.Equal(text, Rules().Apply(text));
    }

    [Fact]
    public void A_user_rule_with_the_same_left_side_wins()
    {
        var rules = Rules("video aberto => tela aberta");

        Assert.Equal("tela aberta", rules.Apply("video aberto"));
        Assert.Equal("vidro fechado", rules.Apply("video fechado")); // the built-ins still work for the rest
    }

    [Fact]
    public void A_user_rule_with_accents_or_other_spacing_in_the_same_left_side_still_wins()
    {
        var rules = Rules("V\U000000EDdeo   Aberto => tela aberta");

        Assert.Equal("tela aberta", rules.Apply("video aberto"));
    }

    [Fact]
    public void The_user_rules_run_before_the_built_in_ones_and_a_user_text_without_a_final_new_line_is_fine()
    {
        var rules = Rules("carro => automovel");

        Assert.Equal("automovel com vidro aberto", rules.Apply("carro com video aberto"));
    }

    [Fact]
    public void No_user_rules_leave_only_the_built_in_ones()
    {
        Assert.Equal(Adjectives.Length * 2, Rules().Count);
        Assert.Equal(Adjectives.Length * 2 + 1, Rules("a => b").Count);
    }
}
