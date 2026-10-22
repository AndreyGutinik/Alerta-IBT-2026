using System.Collections.Frozen;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace AlertaIBT.Core.Correction;

/// <summary>
/// Says whether a text holds vulgar, offensive or sexual words, offline and for everybody (the correction by Gemini is not
/// the only guard: Gemini can be off, slow or fooled). It reads a normalized form of the text, so disguises do not help:
/// capitals, accents, repeated letters (<c>caralhooo</c>), common leetspeak (<c>p0rr4</c>, <c>put@</c>), punctuation or
/// spaces between the letters (<c>p.o.r.r.a</c>, <c>p o r r a</c>), and one <c>*</c> in place of a letter (<c>p*rra</c>).
/// </summary>
/// <remarks>
/// <para>
/// The built-in list (<see cref="ProfanityWords"/>) is Brazilian Portuguese plus the English words people type. Short words
/// match only as a whole word, so Cuiaba, Cunha, computador, deputado, porao or Pinto Silva are never hit; a word that is
/// also common in parking messages (macaco, pau, pica, rola, mala, porta) is not in the list at all. A plate is never read
/// as text: plates are cut out before the words are looked at.
/// </para>
/// <para>
/// The owner can add words in <c>palavras-bloqueadas.txt</c> (one word or phrase per line, <c>#</c> starts a comment, UTF-8).
/// An extra word matches as a whole word (or the whole phrase, word for word) after the same normalization; the file is read
/// again whenever its last-write time changes, and a missing or unreadable file only means "built-ins alone".
/// </para>
/// <para>Thread-safe. The text that was checked is never logged.</para>
/// </remarks>
public sealed partial class ProfanityFilter
{
    /// <summary>The name of the optional file of extra words, inside the data folder of the app.</summary>
    public const string ExtraWordsFileName = "palavras-bloqueadas.txt";

    private const int MaxExtraEntries = 2000;
    private const int MaxExtraLength = 80;
    private const int MinJoinedRun = 3;
    private const int ContextWindow = 3;
    private const int MaxOnesExpanded = 3;

    private const char PlateMark = (char)1;

    private static readonly FrozenSet<string> ExactSet = ProfanityWords.Exact.Select(Collapse).ToFrozenSet(StringComparer.Ordinal);

    // The same words as written, for the one-letter-hidden form ("p*rra") before repeated letters are folded.
    private static readonly string[] ExactForWildcard = ProfanityWords.Exact.Where(word => word.Length >= 4).ToArray();

    private static readonly string[] CollapsedForWildcard = ExactSet.Where(word => word.Length >= 4).ToArray();

    private static readonly string[] Prefixes = ProfanityWords.Prefixes.Select(Collapse).ToArray();

    private static readonly string[] Fragments = ProfanityWords.Fragments.Select(Collapse).ToArray();

    private static readonly FrozenSet<string> RawSet = ProfanityWords.Raw.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenDictionary<string, Exemption> Exemptions = BuildExemptions();

    private static readonly FrozenDictionary<string, FrozenSet<string>> FollowExemptions = ProfanityWords.FollowExemptions
        .SelectMany(rule => rule.Words.Select(word => (Word: Collapse(word), Next: rule.Next.Select(Collapse).ToFrozenSet(StringComparer.Ordinal))))
        .ToFrozenDictionary(pair => pair.Word, pair => pair.Next, StringComparer.Ordinal);

    private static readonly FrozenDictionary<string, ContextRule> ContextRules = ProfanityWords.ContextRules
        .SelectMany(rule => rule.Words.Select(word => (Word: Collapse(word), Rule: new ContextRule(
            rule.Before.Select(Collapse).ToFrozenSet(StringComparer.Ordinal),
            rule.NotNext.Select(Collapse).ToFrozenSet(StringComparer.Ordinal)))))
        .ToFrozenDictionary(pair => pair.Word, pair => pair.Rule, StringComparer.Ordinal);

    private readonly string? _extraFile;
    private readonly ILogger? _logger;
    private readonly Lock _gate = new();
    private bool _loaded;
    private DateTime _stamp;
    private string[] _extra = [];

    /// <param name="extraWordsFile">
    /// The file of extra words (<see cref="ExtraWordsFileName"/> in the data folder); <c>null</c> for the built-in list alone.
    /// </param>
    /// <param name="logger">Receives one line when the file cannot be read (never a word).</param>
    public ProfanityFilter(string? extraWordsFile = null, ILogger? logger = null)
    {
        _extraFile = string.IsNullOrWhiteSpace(extraWordsFile) ? null : extraWordsFile;
        _logger = logger;
    }

    /// <summary>Whether <paramref name="text"/> holds a word of the built-in list.</summary>
    /// <param name="text">What a person typed, or the final text of an alert.</param>
    public static bool IsImproper(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        return HasBuiltInWord(Scan(text));
    }

    /// <summary>Whether <paramref name="text"/> holds a built-in word or one of the extra words of the file.</summary>
    /// <param name="text">What a person typed, or the final text of an alert.</param>
    public bool IsBlocked(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var scan = Scan(text);
        return HasBuiltInWord(scan) || HasExtraWord(scan, CurrentExtraWords());
    }

    // ---- reading the text ----

    // A word of the text: every way it could have been written (leetspeak) and the plain letters and digits.
    private sealed record Tok(string[] Variants, string Plain)
    {
        public static readonly Tok Boundary = new([], string.Empty);

        public bool IsBoundary => Variants.Length == 0;
    }

    private static List<Tok> Scan(string text)
    {
        var folded = PlateLike().Replace(Fold(text), " " + PlateMark + " ");
        var tokens = new List<Tok>();
        var current = new StringBuilder();
        foreach (var c in folded)
        {
            if (char.IsLetterOrDigit(c) || c is '@' or '$' or '*' or '!')
            {
                current.Append(c);
                continue;
            }

            AddToken(tokens, current, c == PlateMark);
        }

        AddToken(tokens, current, false);
        AddJoinedRuns(tokens);
        return tokens;
    }

    private static void AddToken(List<Tok> tokens, StringBuilder current, bool boundary)
    {
        if (current.Length > 0)
        {
            var core = current.ToString().Trim('!', '*');
            current.Clear();
            if (core.Length > 0)
            {
                tokens.Add(MakeToken(core));
            }
        }

        if (boundary)
        {
            tokens.Add(Tok.Boundary);
        }
    }

    private static Tok MakeToken(string core)
    {
        var plain = new string(core.Where(char.IsLetterOrDigit).ToArray());
        List<string> forms = [core];
        if (core.Length > 1 && core[0] == '@')
        {
            forms.Add(core[1..]); // "@user" is a mention, not an "a"
        }

        List<string> variants = [];
        foreach (var form in forms)
        {
            variants.Add(Leet(form, 'i'));
            if (form.Count(c => c == '1') is > 0 and <= MaxOnesExpanded)
            {
                variants.Add(Leet(form, 'l'));
            }
        }

        return new Tok([.. variants.Distinct(StringComparer.Ordinal)], plain);
    }

    // p.o.r.r.a and p o r r a: three or more one-character words in a row are one word.
    private static void AddJoinedRuns(List<Tok> tokens)
    {
        var joined = new List<Tok>();
        var run = new StringBuilder();
        var runLength = 0;
        void Flush()
        {
            if (runLength >= MinJoinedRun)
            {
                joined.Add(MakeToken(run.ToString()));
            }

            run.Clear();
            runLength = 0;
        }

        foreach (var token in tokens)
        {
            if (!token.IsBoundary && token.Plain.Length == 1 && token.Variants.All(v => v.Length == 1))
            {
                run.Append(token.Plain);
                runLength++;
            }
            else
            {
                Flush();
            }
        }

        Flush();
        tokens.AddRange(joined);
    }

    // Lower case, no accents, no invisible characters.
    private static string Fold(string text)
    {
        var folded = new StringBuilder(text.Length);
        foreach (var c in text.Normalize(NormalizationForm.FormD))
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(c);
            if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.Format)
            {
                continue;
            }

            folded.Append(char.ToLowerInvariant(c));
        }

        return folded.ToString();
    }

    private static string Leet(string form, char one)
    {
        var mapped = new StringBuilder(form.Length);
        foreach (var c in form)
        {
            mapped.Append(c switch
            {
                '0' => 'o',
                '1' => one,
                '3' => 'e',
                '4' => 'a',
                '5' => 's',
                '7' => 't',
                '@' => 'a',
                '$' => 's',
                '!' => 'i',
                '*' => '?',
                _ => c,
            });
        }

        return mapped.ToString();
    }

    private static string Collapse(string word)
    {
        var folded = new StringBuilder(word.Length);
        foreach (var c in word)
        {
            if (folded.Length == 0 || folded[^1] != c)
            {
                folded.Append(c);
            }
        }

        return folded.ToString();
    }

    // ---- the built-in words ----

    private static bool HasBuiltInWord(List<Tok> tokens)
    {
        for (var i = 0; i < tokens.Count; i++)
        {
            foreach (var variant in tokens[i].Variants)
            {
                var folded = Collapse(variant);
                if (!IsBuiltInWord(variant, folded))
                {
                    if (IsBodySlangInContext(tokens, i, folded))
                    {
                        return true;
                    }

                    continue;
                }

                if (IsFollowExempt(tokens, i, folded))
                {
                    continue; // "pica-pau": the woodpecker
                }

                if (Exemptions.TryGetValue(folded, out var exemption) && IsExempt(tokens, i, exemption))
                {
                    continue; // "carro arrombado": the break-in, not the insult
                }

                return true;
            }
        }

        return false;
    }

    private static bool IsBuiltInWord(string variant, string folded)
    {
        if (ExactSet.Contains(folded) || RawSet.Contains(variant))
        {
            return true;
        }

        foreach (var prefix in Prefixes)
        {
            if (folded.StartsWith(prefix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        foreach (var fragment in Fragments)
        {
            if (folded.Contains(fragment, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return variant.Length >= 4 && variant.Count(c => c == '?') == 1 && MatchesWithOneLetterHidden(variant, folded);
    }

    // "p*rra": the star stands for one letter of a listed word of the same length.
    private static bool MatchesWithOneLetterHidden(string variant, string folded)
    {
        foreach (var word in ExactForWildcard)
        {
            if (FitsAround(variant, word))
            {
                return true;
            }
        }

        foreach (var word in CollapsedForWildcard)
        {
            if (FitsAround(folded, word))
            {
                return true;
            }
        }

        return false;
    }

    private static bool FitsAround(string pattern, string word)
    {
        if (pattern.Length != word.Length)
        {
            return false;
        }

        for (var i = 0; i < pattern.Length; i++)
        {
            if (pattern[i] != '?' && pattern[i] != word[i])
            {
                return false;
            }
        }

        return true;
    }

    private sealed record ContextRule(FrozenSet<string> Before, FrozenSet<string> NotNext);

    // "meu pinto", "chupa pau": slang that is ordinary alone is blocked next to a possessive or a verb of the list.
    private static bool IsBodySlangInContext(List<Tok> tokens, int index, string folded)
    {
        if (!ContextRules.TryGetValue(folded, out var rule) || index == 0 || tokens[index - 1].IsBoundary)
        {
            return false;
        }

        if (!rule.Before.Contains(Collapse(tokens[index - 1].Variants[0])))
        {
            return false;
        }

        return !(index + 1 < tokens.Count && !tokens[index + 1].IsBoundary && rule.NotNext.Contains(Collapse(tokens[index + 1].Variants[0])));
    }

    // The next word makes the listed word part of an ordinary compound.
    private static bool IsFollowExempt(List<Tok> tokens, int index, string folded)
    {
        return FollowExemptions.TryGetValue(folded, out var next)
            && index + 1 < tokens.Count
            && !tokens[index + 1].IsBoundary
            && next.Contains(Collapse(tokens[index + 1].Variants[0]));
    }

    private sealed record Exemption(FrozenSet<string> Words, FrozenSet<string> After);

    private static FrozenDictionary<string, Exemption> BuildExemptions()
    {
        Dictionary<string, Exemption> map = [];
        foreach (var (words, after) in ProfanityWords.Exemptions)
        {
            var exemption = new Exemption(
                words.Select(Collapse).ToFrozenSet(StringComparer.Ordinal),
                after.Select(Collapse).ToFrozenSet(StringComparer.Ordinal));
            foreach (var word in exemption.Words)
            {
                map[word] = exemption;
            }
        }

        return map.ToFrozenDictionary(StringComparer.Ordinal);
    }

    // Whether one of the words that make the listed word ordinary comes shortly before it.
    private static bool IsExempt(List<Tok> tokens, int index, Exemption exemption)
    {
        for (var i = index - 1; i >= 0 && i >= index - ContextWindow; i--)
        {
            if (tokens[i].IsBoundary)
            {
                return false;
            }

            var word = Collapse(tokens[i].Variants[0]);
            if (exemption.Words.Contains(word))
            {
                return false;
            }

            if (exemption.After.Contains(word))
            {
                return true;
            }
        }

        return false;
    }

    // ---- the extra words of the file ----

    private string[] CurrentExtraWords()
    {
        if (_extraFile is null)
        {
            return [];
        }

        var stamp = LastWrite(_extraFile);
        lock (_gate)
        {
            if (!_loaded || stamp != _stamp)
            {
                _extra = ReadExtraWords(_extraFile);
                _stamp = stamp;
                _loaded = true;
            }

            return _extra;
        }
    }

    private static DateTime LastWrite(string path)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path);
        }
        catch (Exception)
        {
            return DateTime.MinValue;
        }
    }

    private string[] ReadExtraWords(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return [];
            }

            List<string> words = [];
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
            while (reader.ReadLine() is { } line && words.Count < MaxExtraEntries)
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed[0] == '#' || trimmed.Length > MaxExtraLength)
                {
                    continue;
                }

                var phrase = string.Join(' ', ExtraSplit().Split(Fold(trimmed)).Where(part => part.Length > 0).Select(Collapse));
                if (phrase.Length > 0)
                {
                    words.Add(phrase);
                }
            }

            return [.. words.Distinct(StringComparer.Ordinal)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException or RegexMatchTimeoutException)
        {
            _logger?.LogWarning("Nao foi possivel ler {File}; o filtro usa so a lista padrao ({Reason})", ExtraWordsFileName, ex.GetType().Name);
            return [];
        }
    }

    // The text as two strings of words, with and without leetspeak, to look for the phrases of the file.
    private static bool HasExtraWord(List<Tok> tokens, string[] extra)
    {
        if (extra.Length == 0)
        {
            return false;
        }

        var leet = new StringBuilder(" ");
        var plain = new StringBuilder(" ");
        foreach (var token in tokens)
        {
            if (token.IsBoundary)
            {
                leet.Append("# ");
                plain.Append("# ");
                continue;
            }

            leet.Append(Collapse(token.Variants[0])).Append(' ');
            plain.Append(Collapse(token.Plain)).Append(' ');
        }

        var leetText = leet.ToString();
        var plainText = plain.ToString();
        foreach (var phrase in extra)
        {
            var wrapped = " " + phrase + " ";
            if (leetText.Contains(wrapped, StringComparison.Ordinal) || plainText.Contains(wrapped, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    // A plate (LLLNLNN, or the old LLLNNNN), with or without its hyphen: never read as words.
    [GeneratedRegex(@"(?<![a-z0-9])[a-z]{3}-?[0-9][a-z0-9][0-9]{2}(?![a-z0-9])", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex PlateLike();

    [GeneratedRegex(@"[^\p{L}\p{N}]+", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ExtraSplit();
}
