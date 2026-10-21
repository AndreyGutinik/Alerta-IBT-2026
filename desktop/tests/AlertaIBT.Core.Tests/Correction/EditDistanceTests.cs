using AlertaIBT.Core.Correction;

namespace AlertaIBT.Core.Tests.Correction;

public class EditDistanceTests
{
    [Theory]
    [InlineData("", "", 0)]
    [InlineData("abc", "abc", 0)]
    [InlineData("", "abc", 3)]
    [InlineData("abc", "", 3)]
    [InlineData("acesso", "aceso", 1)]
    [InlineData("abrto", "aberto", 1)]
    [InlineData("kitten", "sitting", 3)]
    [InlineData("recepssao", "recepcao", 2)]
    [InlineData("gol", "fox", 2)]
    [InlineData("ab", "ba", 2)]
    public void Levenshtein_CountsInsertionsDeletionsAndSubstitutions(string a, string b, int expected)
    {
        Assert.Equal(expected, EditDistance.Levenshtein(a, b));
        Assert.Equal(expected, EditDistance.Levenshtein(b, a));
    }

    [Fact]
    public void Levenshtein_IsCaseSensitive()
    {
        Assert.Equal(1, EditDistance.Levenshtein("Gol", "gol"));
    }

    [Fact]
    public void Levenshtein_RejectsNull()
    {
        Assert.Throws<ArgumentNullException>(() => EditDistance.Levenshtein(null!, "a"));
        Assert.Throws<ArgumentNullException>(() => EditDistance.Levenshtein("a", null!));
    }
}
