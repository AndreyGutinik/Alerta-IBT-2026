using System.Collections.Frozen;
using AlertaIBT.Core.Alerts;

namespace AlertaIBT.Core.Correction;

/// <summary>
/// The color rule of the safety lock: Gemini may fix how a color is written, never change which color it is
/// ("gol preto" must not become "GOL PRATA"). The two genders of a color count as the same color.
/// </summary>
internal static class ColorLock
{
    // The feminine spelling to the masculine one (folded: no accents, lower case).
    private static readonly FrozenDictionary<string, string> Masculine = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["preta"] = "preto",
        ["branca"] = "branco",
        ["vermelha"] = "vermelho",
        ["amarela"] = "amarelo",
        ["dourada"] = "dourado",
        ["roxa"] = "roxo",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>Whether two color words are the same color: same folded word, or the two genders of one color.</summary>
    public static bool SameColor(string a, string b) => Canonical(a) == Canonical(b);

    /// <summary>The color words of <paramref name="folded"/> words, each in its masculine form.</summary>
    public static HashSet<string> ColorsIn(IEnumerable<string> foldedWords) =>
        foldedWords.Where(Vocabulary.IsColor).Select(Canonical).ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Whether Gemini's color is allowed for a text that has the color words <paramref name="rawColors"/> (from
    /// <see cref="ColorsIn"/>). A text with no color word allows anything; otherwise Gemini's color must name only colors
    /// of the text, at least one. An empty color is allowed only for an answer that is not about a vehicle.
    /// </summary>
    public static bool Allows(HashSet<string> rawColors, string aiColor, bool vehicleAnswer)
    {
        if (rawColors.Count == 0)
        {
            return true;
        }

        var wanted = ColorsIn(aiColor.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(Vocabulary.Fold));
        if (string.IsNullOrWhiteSpace(aiColor))
        {
            return !vehicleAnswer;
        }

        return wanted.Count > 0 && wanted.IsSubsetOf(rawColors);
    }

    private static string Canonical(string word)
    {
        var folded = Vocabulary.Fold(word);
        return Masculine.TryGetValue(folded, out var masculine) ? masculine : folded;
    }
}
