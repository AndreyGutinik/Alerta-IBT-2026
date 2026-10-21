using AlertaIBT.Core.Correction;

namespace AlertaIBT.Core.Tests.Correction;

public class FixedRulesTests
{
    [Fact]
    public void Apply_MatchesWithoutCaseAccentsOrSpacing()
    {
        var rules = FixedRules.Parse("farol acesso => farol aceso\nlixo\nparado local proibid -> parado em local proibido");

        Assert.Equal("farol aceso", rules.Apply("FAROL  AC\U000000C9SSO"));
        Assert.Equal("parado em local proibido", rules.Apply("Parado local PROIBID"));
        Assert.Equal(2, rules.Count);
    }

    [Fact]
    public void Apply_HyphenInTextMatchesSpacedHyphenInRule()
    {
        var rules = FixedRules.Parse("farol - acesso => farol aceso");

        Assert.Equal("gol branco farol aceso", rules.Apply("gol branco farol-acesso"));
        Assert.Equal("gol branco farol aceso", rules.Apply("gol branco farol  -  acesso"));
    }

    [Fact]
    public void Apply_AccentsInRuleMatchPlainAndAccentedText()
    {
        var rules = FixedRules.Parse("lan\U000000E7a => lanca certa");

        Assert.Equal("lanca certa", rules.Apply("lanca"));
        Assert.Equal("lanca certa", rules.Apply("LAN\U000000C7A"));
    }

    [Fact]
    public void Apply_NormalizesDecomposedTextBeforeMatching()
    {
        var rules = FixedRules.Parse("acesso => aceso");

        // "ACESSO" with a decomposed E + combining acute accent.
        Assert.Equal("aceso", rules.Apply("AC" + "E\U00000301" + "SSO"));
    }

    [Fact]
    public void Apply_MatchesWholeWordsOnly()
    {
        var rules = FixedRules.Parse("ma => mais");

        Assert.Equal("marrom mais", rules.Apply("marrom ma"));
        Assert.Equal("amar", rules.Apply("amar"));
        Assert.Equal("a mais b", rules.Apply("a ma b"));
        Assert.Equal("mais.", rules.Apply("ma."));
        Assert.Equal("ma1", rules.Apply("ma1"));
    }

    [Fact]
    public void Apply_InsertsRightSideLiterally()
    {
        Assert.Equal("$1", FixedRules.Parse("x => $1").Apply("x"));
        Assert.Equal("a.b", FixedRules.Parse("x => a.b").Apply("x"));
        Assert.Equal("Fiat Uno", FixedRules.Parse("uno => Fiat Uno").Apply("uno"));
    }

    [Fact]
    public void Apply_KeepsAccentedCharactersOfTheRightSideVerbatim()
    {
        var rules = FixedRules.Parse("farol acesso => far\U000000F3is \U000000C7a");

        Assert.Equal("far\U000000F3is \U000000C7a", rules.Apply("FAROL ACESSO"));
    }

    [Fact]
    public void Apply_EscapesRegexCharactersOfTheLeftSide()
    {
        var rules = FixedRules.Parse("a.b => dot");

        Assert.Equal("dot", rules.Apply("a.b"));
        Assert.Equal("axb", rules.Apply("axb"));
    }

    [Fact]
    public void Apply_ReplacesEveryMatch()
    {
        Assert.Equal("b b b", FixedRules.Parse("a => b").Apply("a A a"));
    }

    [Fact]
    public void Apply_RunsRulesInTheOrderWritten()
    {
        Assert.Equal("c", FixedRules.Parse("a => b\nb => c").Apply("a"));
        Assert.Equal("b", FixedRules.Parse("b => c\na => b").Apply("a"));
    }

    [Fact]
    public void Apply_LeavesTextAloneWhenNothingMatches()
    {
        var rules = FixedRules.Parse("farol acesso => farol aceso");

        Assert.Equal("gol branco", rules.Apply("gol branco"));
        Assert.Equal("", rules.Apply(""));
    }

    [Theory]
    [InlineData("a => b")]
    [InlineData("a -> b")]
    [InlineData("a = b")]
    [InlineData("a=>b")]
    [InlineData("  a  =>  b  ")]
    public void Parse_AcceptsEverySeparator(string line)
    {
        var rules = FixedRules.Parse(line);

        Assert.Equal(1, rules.Count);
        Assert.Equal("b", rules.Apply("a"));
    }

    [Fact]
    public void Parse_SplitsAtTheFirstFatArrowBeforeOtherSeparators()
    {
        var rules = FixedRules.Parse("a -> b => c");

        Assert.Equal(1, rules.Count);
        Assert.Equal("c", rules.Apply("a -> b"));
    }

    [Fact]
    public void Parse_SplitsAtTheFirstArrowBeforeEqualsSign()
    {
        var rules = FixedRules.Parse("a -> b = c");

        Assert.Equal("b = c", rules.Apply("a"));
    }

    [Fact]
    public void Parse_SplitsAtTheFirstEqualsSignOnly()
    {
        var rules = FixedRules.Parse("a = b = c");

        Assert.Equal("b = c", rules.Apply("a"));
    }

    [Fact]
    public void Parse_CollapsesWhitespaceInBothSides()
    {
        var rules = FixedRules.Parse("farol    acesso =>   farol \t  aceso");

        Assert.Equal("farol aceso", rules.Apply("farol acesso"));
    }

    [Fact]
    public void Parse_AcceptsWindowsLineBreaks()
    {
        var rules = FixedRules.Parse("a => b\r\nc => d\r\n");

        Assert.Equal(2, rules.Count);
        Assert.Equal("b d", rules.Apply("a c"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("lixo")]
    [InlineData("=> b")]
    [InlineData("a =>")]
    [InlineData("a =>   ")]
    [InlineData("-> b")]
    [InlineData("= b")]
    [InlineData(" => ")]
    public void Parse_IgnoresInvalidLines(string line)
    {
        var rules = FixedRules.Parse(line);

        Assert.Equal(0, rules.Count);
        Assert.Equal("a b", rules.Apply("a b"));
    }

    [Fact]
    public void Parse_CountsRepeatedRulesOnceAndTheFirstOneWins()
    {
        var rules = FixedRules.Parse("farol acesso => primeiro\nFAROL AC\U000000C9SSO => segundo\nfarol  acesso -> terceiro");

        Assert.Equal(1, rules.Count);
        Assert.Equal("primeiro", rules.Apply("farol acesso"));
    }

    [Fact]
    public void Empty_HasNoRulesAndReturnsTheTextUnchanged()
    {
        Assert.Equal(0, FixedRules.Empty.Count);
        Assert.Equal("AC" + "E\U00000301" + "SSO", FixedRules.Empty.Apply("AC" + "E\U00000301" + "SSO"));
    }

    [Fact]
    public void Inspect_CountsDistinctRulesNotesIgnoredLinesAndRepeats()
    {
        var report = FixedRules.Inspect("farol acesso => farol aceso\nlixo\n\nFAROL AC\U000000C9SSO -> outra\n  => sem esquerda\nvidro abrto = vidro aberto\nsem direita =>   ");

        Assert.Equal(2, report.ValidCount);
        Assert.Equal([2, 5, 7], report.IgnoredLines);
        Assert.Equal([4], report.RepeatedLines);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\n\n  \r\n")]
    public void Inspect_ABlankTextHasNothingToReport(string text)
    {
        var report = FixedRules.Inspect(text);

        Assert.Equal(0, report.ValidCount);
        Assert.Empty(report.IgnoredLines);
        Assert.Empty(report.RepeatedLines);
    }

    [Fact]
    public void Inspect_AgreesWithParseOnTheNumberOfRules()
    {
        const string Text = "a => b\nA => c\nx -> y\nz\nw = v";

        Assert.Equal(FixedRules.Parse(Text).Count, FixedRules.Inspect(Text).ValidCount);
    }

    [Fact]
    public void Inspect_NullThrows()
    {
        Assert.Throws<ArgumentNullException>(() => FixedRules.Inspect(null!));
    }

    [Fact]
    public void Parse_NullThrows()
    {
        Assert.Throws<ArgumentNullException>(() => FixedRules.Parse(null!));
    }

    [Fact]
    public void Apply_NullThrows()
    {
        Assert.Throws<ArgumentNullException>(() => FixedRules.Empty.Apply(null!));
    }
}
