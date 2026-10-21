using System.Collections.Frozen;
using System.Globalization;
using System.Text;

namespace AlertaIBT.Core.Alerts;

/// <summary>
/// The words the parser knows: colors, color modifiers and the words that start a reason. Every entry is lower case
/// and has no accents; a typed word is folded (see <see cref="Fold"/>: lower case, no accents, no punctuation stuck
/// to its ends) before it is compared.
/// </summary>
internal static class Vocabulary
{
    /// <summary>The color words, as whole words.</summary>
    public static IReadOnlySet<string> Colors { get; } = new[]
    {
        "preto", "preta", "branco", "branca", "prata", "cinza", "grafite", "vermelho", "vermelha", "azul", "verde",
        "amarelo", "amarela", "bege", "marrom", "dourado", "dourada", "vinho", "laranja", "rosa", "roxo", "roxa",
        "champanhe",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>The words that join the color right before them ("azul marinho", "cinza metalico").</summary>
    public static IReadOnlySet<string> ColorModifiers { get; } = new[]
    {
        "escuro", "escura", "claro", "clara", "metalico", "metalica", "fosco", "fosca", "marinho",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// How a word that starts a reason begins ("estacionad" covers estacionado, estacionada, estacionados...). They
    /// are the words of the plan and of the reason detection of the Electron app (<c>src/main.js</c> in the tag v7.0.0), plus
    /// <c>farois</c> (the plural of farol without its accent) and <c>acesa</c>.
    /// </summary>
    public static IReadOnlyList<string> ReasonStarts { get; } =
    [
        "farol", "farois", "vidro", "estacionad", "parad", "porta", "pisca", "luz", "alarme", "pneu", "dono",
        "comparecer", "bloqueando", "vaga", "contramao", "proibid", "aceso", "acesa",
    ];

    /// <summary>
    /// Words that start a reason only when they are the whole word: "somente" and "sombra" are not about sound.
    /// </summary>
    public static IReadOnlySet<string> ReasonWords { get; } = new[] { "som", "sons" }.ToFrozenSet(StringComparer.Ordinal);

    public static bool IsColor(string word) => Colors.Contains(Fold(word));

    public static bool IsColorModifier(string word) => ColorModifiers.Contains(Fold(word));

    public static bool IsReasonStart(string word)
    {
        var folded = Fold(word);
        return ReasonWords.Contains(folded)
            || ReasonStarts.Any(start => folded.StartsWith(start, StringComparison.Ordinal));
    }

    /// <summary>
    /// Lower case, no accents and no punctuation stuck to the ends: <c>Met&#225;lico</c>, <c>metalico</c>,
    /// <c>(metalico</c> and <c>metalico.</c> fold to the same word.
    /// </summary>
    public static string Fold(string word)
    {
        var folded = new StringBuilder(word.Length);
        foreach (var c in word.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                folded.Append(char.ToLowerInvariant(c));
            }
        }

        return TrimEdges(folded.ToString());
    }

    /// <summary>Cuts what is at both ends of <paramref name="text"/> and is not a letter or a digit.</summary>
    public static string TrimEdges(string text) => TrimEnd(TrimStart(text));

    /// <summary>Cuts what is at the start of <paramref name="text"/> and is not a letter or a digit.</summary>
    public static string TrimStart(string text)
    {
        var start = 0;
        while (start < text.Length && !char.IsLetterOrDigit(text[start]))
        {
            start++;
        }

        return text[start..];
    }

    /// <summary>Cuts what is at the end of <paramref name="text"/> and is not a letter or a digit.</summary>
    public static string TrimEnd(string text)
    {
        var end = text.Length;
        while (end > 0 && !char.IsLetterOrDigit(text[end - 1]))
        {
            end--;
        }

        return text[..end];
    }
}
