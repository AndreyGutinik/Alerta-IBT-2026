using AlertaIBT.Core.Correction;

namespace AlertaIBT.Core.Tests.Correction;

public class FixedRulesProviderTests
{
    [Fact]
    public void The_same_text_gives_the_very_same_parsed_rules_and_a_new_text_gives_new_ones()
    {
        var text = "carro => automovel";
        var reads = 0;
        var provider = new FixedRulesProvider(() =>
        {
            reads++;
            return text;
        });

        var first = provider.Current();
        var second = provider.Current();
        text = "gol => Gol";
        var third = provider.Current();

        Assert.Same(first, second);
        Assert.NotSame(first, third);
        Assert.Equal("automovel azul", first.Apply("carro azul"));
        Assert.Equal("Gol azul", third.Apply("gol azul"));
        Assert.Equal(3, reads);
    }

    [Fact]
    public void Empty_text_means_no_rules()
    {
        var provider = new FixedRulesProvider(() => string.Empty);

        Assert.Same(FixedRules.Empty, provider.Current());
    }

    [Fact]
    public void Many_threads_reading_while_the_text_changes_always_get_a_whole_rule_set()
    {
        var text = "a => b";
        var provider = new FixedRulesProvider(() => Volatile.Read(ref text));

        Parallel.For(0, 200, i =>
        {
            if (i % 20 == 0)
            {
                Volatile.Write(ref text, $"x{i} => y{i}");
            }

            Assert.NotNull(provider.Current());
        });
    }
}
