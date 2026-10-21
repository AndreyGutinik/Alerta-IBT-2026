using System.Text;
using System.Text.RegularExpressions;
using AlertaIBT.Core.Alerts;
using WeCantSpell.Hunspell;

namespace AlertaIBT.Core.Correction;

/// <summary>
/// Fixes spelling and accents with the pt-BR Hunspell dictionary (VERO), with no internet. It only swaps a word the
/// dictionary rejects and <see cref="DomainWords"/> does not list, and only for the first suggestion that is at most 2
/// edits away (ignoring case and accents), so a plate, a car model or a name it cannot fix safely stays as typed.
/// </summary>
public sealed partial class OfflineCorrector : ICorrector
{
    private const int MaxDistance = 2;

    // A domain word is a guess made without the dictionary, so it must be one edit away: with two, "pssoa" would become "pisca".
    private const int MaxDomainDistance = 1;

    private const int ShortestCorrectedWord = 3;

    // The operator and the administrator have no length limit, and each unknown word costs a dictionary search (tens of
    // milliseconds): only the start of a long text is corrected, the rest stays as typed.
    private const int MaxCorrectedWords = 80;

    private const int MaxCorrectedCharacters = 2000;

    private readonly WordList _words;

    private OfflineCorrector(WordList words)
    {
        _words = words;
    }

    /// <summary>
    /// Loads <c>pt_BR.aff</c> and <c>pt_BR.dic</c> from <paramref name="dictionaryDir"/>. It reads the whole
    /// dictionary (about a second), so load once and share the instance: it can be used from several threads.
    /// </summary>
    /// <exception cref="FileNotFoundException">One of the two files is not in the directory.</exception>
    public static OfflineCorrector Load(string dictionaryDir)
    {
        ArgumentNullException.ThrowIfNull(dictionaryDir);

        var affix = Path.Combine(dictionaryDir, "pt_BR.aff");
        var dictionary = Path.Combine(dictionaryDir, "pt_BR.dic");
        foreach (var file in new[] { affix, dictionary })
        {
            if (!File.Exists(file))
            {
                throw new FileNotFoundException("Dictionary file not found.", file);
            }
        }

        return new OfflineCorrector(WordList.CreateFromFiles(dictionary, affix));
    }

    /// <inheritdoc />
    /// <remarks>
    /// Never returns <c>null</c>. Exceptions are not caught (the pipeline falls back to the plain parser). The work
    /// runs on the thread pool, so a caller on the UI thread is not blocked.
    /// </remarks>
    public Task<ParsedAlert?> CorrectAsync(string raw, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(raw);

        return Task.Run(() => (ParsedAlert?)AlertParser.Parse(CorrectText(raw, ct)), ct);
    }

    // A word: letters, digits and marks, with hyphens and apostrophes between them ("porta-malas", "d'agua"). Anything
    // else (spaces, | / ; , . ! ( ) #) separates words and stays exactly where it was.
    [GeneratedRegex("[\\p{L}\\p{N}\\p{M}]+(?:['\\-\U00002019][\\p{L}\\p{N}\\p{M}]+)*")]
    private static partial Regex WordPattern();

    private string CorrectText(string raw, CancellationToken ct)
    {
        // The plate (with its label) is not text to correct: "qwe 1b23" would be read as a word and a number.
        var hasPlate = PlateParser.TryLocate(raw, out var plateStart, out var plateLength);

        var corrected = new StringBuilder(raw.Length);
        var copied = 0;
        var seen = 0;
        foreach (Match match in WordPattern().Matches(raw))
        {
            ct.ThrowIfCancellationRequested();

            if (seen++ >= MaxCorrectedWords || match.Index >= MaxCorrectedCharacters)
            {
                break;
            }

            if (hasPlate && match.Index < plateStart + plateLength && match.Index + match.Length > plateStart)
            {
                continue;
            }

            var replacement = CorrectToken(match.Value);
            if (replacement == match.Value)
            {
                continue;
            }

            corrected.Append(raw, copied, match.Index - copied).Append(replacement);
            copied = match.Index + match.Length;
        }

        return corrected.Append(raw, copied, raw.Length - copied).ToString();
    }

    private string CorrectToken(string token)
    {
        if (token.Any(char.IsDigit))
        {
            return token;
        }

        if (IsKnown(token, out var known))
        {
            return known;
        }

        // "farol-abrto": the whole thing is unknown, so each part is looked at on its own.
        return token.Contains('-') ? string.Join('-', token.Split('-').Select(CorrectWord)) : CorrectWord(token);
    }

    private string CorrectWord(string word)
    {
        if (word.Length < ShortestCorrectedWord)
        {
            return word;
        }

        if (IsKnown(word, out var known))
        {
            return known;
        }

        // "Kauan", "Gutnik", "Andrey": a word written with a capital and small letters is taken for a name, and a
        // name is not in the dictionary. Words typed in capitals (CAIXA ALTA) are still corrected.
        if (IsCapitalized(word))
        {
            return word;
        }

        var folded = Vocabulary.Fold(word);

        // A domain word within reach comes first: for "abrto" Hunspell offers only "abeto" (a tree), and "aberto" is
        // what a parking notice means.
        if (NearestDomainWord(folded) is { } domainWord)
        {
            return domainWord;
        }

        // The first suggestion within reach; but a domain word within reach beats it ("recepssao" is two edits from
        // both "recesso", which Hunspell lists first, and "recep\U000000E7\U000000E3o").
        string? firstNear = null;
        foreach (var suggestion in _words.Suggest(word))
        {
            // A suggestion made of two words would change how many words the text has.
            if (suggestion.Any(char.IsWhiteSpace)
                || EditDistance.Levenshtein(Vocabulary.Fold(suggestion), folded) > MaxDistance)
            {
                continue;
            }

            if (DomainWords.All.Contains(suggestion))
            {
                return suggestion;
            }

            firstNear ??= suggestion;
        }

        return firstNear ?? word;
    }

    private static bool IsCapitalized(string word) =>
        char.IsUpper(word[0]) && word.Skip(1).Any(char.IsLower);

    private static string? NearestDomainWord(string folded)
    {
        const int allowed = MaxDomainDistance;
        string? nearest = null;
        var nearestDistance = int.MaxValue;
        foreach (var (candidate, spelling) in DomainWords.Candidates)
        {
            if (Math.Abs(candidate.Length - folded.Length) > allowed)
            {
                continue;
            }

            var distance = EditDistance.Levenshtein(candidate, folded);
            if (distance <= allowed && distance < nearestDistance)
            {
                nearest = spelling;
                nearestDistance = distance;
            }
        }

        return nearest;
    }

    // Whether the word needs no correction: the dictionary accepts it or it is a domain word. A domain word typed
    // without its accents ("portao") still gets them.
    private bool IsKnown(string word, out string spelled)
    {
        spelled = word;
        if (DomainWords.TryGetSpelling(word, out var spelling))
        {
            // Typed with no accent at all, while the proper spelling has some.
            if (word.All(char.IsAscii) && spelling != Vocabulary.Fold(spelling))
            {
                spelled = spelling;
            }

            return true;
        }

        return _words.Check(word);
    }
}
