using System.Text;
using System.Text.RegularExpressions;
using AlertaIBT.Core.Correction;

namespace AlertaIBT.Core.Tests.Correction;

// The pt-BR dictionary of the offline corrector is a long list of ordinary words: the filter may block only the vulgar
// ones. Every piece of an entry (split at hyphens and dots, as the filter splits a text) that it blocks must be listed
// here, on purpose, after a person looked at it. Pieces are lower case, accents kept as in the dictionary.
public partial class ProfanityDictionaryAuditTests
{
    // Vulgar or offensive on purpose.
    private static readonly string[] Vulgar =
    [
        "babaca", "babaquice", "boceta", "bocetas", "bostao", "bundao", "bundinha", "bundona", "colhao", "debiloide", "ejaculacao", "ejacular", "jeba", "pepeca", "pica", "picas", "queca", "safadeza", "bicha", "bichas", "bichinha", "bichinhas", "bichona", "boiola", "boquete", "bosta", "bostas",
        "caralheira", "caralhete", "caralho", "corno", "cornos", "cornudo", "cornuda", "cretino", "crioulo", "crioula", "crioulos",
        "crioulas", "cu", "cus", "desencaralhar", "escroto", "estuprar", "estupro", "estupido", "estupidos",
        "foda", "foder", "fodinha", "imbecil", "masturbar", "masturbacao", "merda", "merdoso", "mongoloide", "orgasmo",
        "otario", "paneleiro", "pedofila", "pedofilia", "pedofilo", "porno", "pornografia", "porra", "porras", "punhete",
        "punheteiro", "puta", "putaria", "puteiro", "putas", "puto", "sapatao", "sexo", "siriricar", "siririca", "siriricas", "mongol", "tarrada",
        "tesao", "tesudo", "transar", "vagabundos", "veado", "veados", "viado", "xereca", "xoxota",
    ];

    // Ordinary words the filter blocks for a reason that is acceptable: all of them need a listed fragment inside a
    // chemical or a city. Reviewed one by one.
    private static readonly string[] AcceptableCollateral =
    [
        "ambucetamida", // contains the fragment of the vulgar word for a body part; a chemical
        "pora", // the city of Pora (porra folds to pora)
    ];

    [Fact]
    public void The_filter_blocks_no_word_of_the_dictionary_that_was_not_reviewed()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Dictionaries", "pt_BR.dic");
        var blocked = new SortedSet<string>(StringComparer.Ordinal);
        using var reader = new StreamReader(path, Encoding.UTF8);
        reader.ReadLine(); // the word count
        while (reader.ReadLine() is { } line)
        {
            var slash = line.IndexOf('/', StringComparison.Ordinal);
            var entry = slash < 0 ? line.Trim() : line[..slash].Trim();
            foreach (var piece in Pieces().Split(entry))
            {
                if (piece.Length > 0 && ProfanityFilter.IsImproper(piece))
                {
                    blocked.Add(Fold(piece));
                }
            }
        }

        var reviewed = Vulgar.Concat(AcceptableCollateral).ToHashSet(StringComparer.Ordinal);
        var surprises = blocked.Where(word => !reviewed.Contains(word)).ToArray();
        Assert.True(surprises.Length == 0, "Blocked dictionary words not reviewed: " + string.Join(", ", surprises));
    }

    private static string Fold(string text)
    {
        var folded = new StringBuilder();
        foreach (var c in text.Normalize(NormalizationForm.FormD))
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark)
            {
                folded.Append(char.ToLowerInvariant(c));
            }
        }

        return folded.ToString();
    }

    [GeneratedRegex(@"[^\p{L}\p{N}]+")]
    private static partial Regex Pieces();
}
