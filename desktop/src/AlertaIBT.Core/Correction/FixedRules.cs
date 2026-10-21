using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AlertaIBT.Core.Correction;

/// <summary>What <see cref="FixedRules.Inspect"/> found in a rules text.</summary>
/// <param name="ValidCount">How many distinct rules the text holds (a repeat counts once).</param>
/// <param name="IgnoredLines">The 1-based numbers of the lines that are not a rule (no separator, or an empty side).</param>
/// <param name="RepeatedLines">The 1-based numbers of the lines that repeat the left side of an earlier rule.</param>
public sealed record FixedRulesReport(int ValidCount, IReadOnlyList<int> IgnoredLines, IReadOnlyList<int> RepeatedLines);

/// <summary>
/// The user's fixed correction rules: lines typed as <c>wrong => right</c> that rewrite the text. They match whole
/// words, ignoring case, accents and the amount of spacing around spaces and hyphens, and run in the order written.
/// </summary>
public sealed partial class FixedRules
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(1);

    // Tried in this order: the first one present in a line is the separator.
    private static readonly string[] Separators = ["=>", "->", "="];

    private static readonly Dictionary<char, string> AccentClasses = new()
    {
        ['a'] = "[a\U000000E1\U000000E0\U000000E2\U000000E3\U000000E4]",
        ['c'] = "[c\U000000E7]",
        ['e'] = "[e\U000000E9\U000000E8\U000000EA\U000000EB]",
        ['i'] = "[i\U000000ED\U000000EC\U000000EE\U000000EF]",
        ['o'] = "[o\U000000F3\U000000F2\U000000F4\U000000F5\U000000F6]",
        ['u'] = "[u\U000000FA\U000000F9\U000000FB\U000000FC]",
    };

    private readonly IReadOnlyList<Rule> _rules;

    private FixedRules(IReadOnlyList<Rule> rules)
    {
        _rules = rules;
    }

    /// <summary>No rules: <see cref="Apply"/> returns the text unchanged.</summary>
    public static FixedRules Empty { get; } = new([]);

    /// <summary>How many valid, distinct rules there are.</summary>
    public int Count => _rules.Count;

    /// <summary>
    /// Reads one rule per line. The separator is the first <c>=&gt;</c> of the line or, failing that, the first
    /// <c>-&gt;</c> or, failing that, the first <c>=</c>. Lines without a separator or with an empty side are ignored,
    /// and so are repeats of a rule already read (same left side, ignoring case and accents): the first one wins.
    /// </summary>
    public static FixedRules Parse(string lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        List<Rule> rules = [];
        HashSet<string> seen = [];
        foreach (string rawLine in lines.Split('\n'))
        {
            if (!TrySplit(rawLine.Trim(), out string left, out string right))
            {
                continue;
            }

            string source = Simplify(left);
            string pattern = BuildPattern(source);
            if (pattern.Length == 0 || !seen.Add(source))
            {
                continue;
            }

            Regex regex = new(
                $@"(?<![\p{{L}}\p{{N}}]){pattern}(?![\p{{L}}\p{{N}}])",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled,
                MatchTimeout);
            rules.Add(new Rule(regex, right));
        }

        return rules.Count == 0 ? Empty : new FixedRules(rules);
    }

    /// <summary>
    /// Reads the rules text the way <see cref="Parse"/> does, without building any rule, and says what it found: how many
    /// distinct rules there are, which lines were ignored (no separator or an empty side) and which repeat an earlier rule.
    /// Cheap enough to run on every settled edit of the rules box.
    /// </summary>
    public static FixedRulesReport Inspect(string lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        List<int> ignored = [];
        List<int> repeated = [];
        HashSet<string> seen = [];
        var valid = 0;
        var number = 0;
        foreach (string rawLine in lines.Split('\n'))
        {
            number++;
            string line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue; // a blank line is a gap, not a mistake
            }

            if (!TrySplit(line, out string left, out _))
            {
                ignored.Add(number);
                continue;
            }

            string source = Simplify(left);
            if (BuildPattern(source).Length == 0)
            {
                ignored.Add(number);
            }
            else if (seen.Add(source))
            {
                valid++;
            }
            else
            {
                repeated.Add(number);
            }
        }

        return new FixedRulesReport(valid, ignored, repeated);
    }

    /// <summary>
    /// Applies every rule, in the order written, to the text (normalized to NFC first). Each rule replaces every
    /// match with its right side, exactly as written.
    /// </summary>
    public string Apply(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (_rules.Count == 0)
        {
            return text;
        }

        string corrected = text.Normalize(NormalizationForm.FormC);
        foreach (Rule rule in _rules)
        {
            try
            {
                corrected = rule.Regex.Replace(corrected, _ => rule.Replacement);
            }
            catch (RegexMatchTimeoutException)
            {
                // A rule that cannot finish must not take the whole alert down: leave the text as it was.
            }
        }

        return corrected;
    }

    private static bool TrySplit(string line, out string left, out string right)
    {
        left = right = string.Empty;

        (string separator, int index) = FindSeparator(line);
        if (index < 0)
        {
            return false;
        }

        left = CollapseWhitespace(line[..index]);
        right = CollapseWhitespace(line[(index + separator.Length)..]);
        return left.Length > 0 && right.Length > 0;
    }

    private static (string Separator, int Index) FindSeparator(string line)
    {
        foreach (string separator in Separators)
        {
            int index = line.IndexOf(separator, StringComparison.Ordinal);
            if (index >= 0)
            {
                return (separator, index);
            }
        }

        return (string.Empty, -1);
    }

    private static string CollapseWhitespace(string text)
    {
        return WhitespaceRun().Replace(text.Trim(), " ");
    }

    /// <summary>Lowercase, accent-free left side with no spaces around its hyphens.</summary>
    private static string Simplify(string left)
    {
        StringBuilder withoutAccents = new(left.Length);
        foreach (char c in left.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                withoutAccents.Append(c);
            }
        }

        string lower = withoutAccents.ToString().ToLowerInvariant();
        return SpacedHyphen().Replace(lower, "-").Trim();
    }

    private static string BuildPattern(string source)
    {
        StringBuilder pattern = new();
        foreach (char c in source)
        {
            if (char.IsWhiteSpace(c))
            {
                pattern.Append(@"\s+");
            }
            else if (c == '-')
            {
                pattern.Append(@"\s*-\s*");
            }
            else if (AccentClasses.TryGetValue(c, out string? accentClass))
            {
                pattern.Append(accentClass);
            }
            else
            {
                pattern.Append(Regex.Escape(c.ToString()));
            }
        }

        return pattern.ToString();
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRun();

    [GeneratedRegex(@"\s*-\s*")]
    private static partial Regex SpacedHyphen();

    private sealed record Rule(Regex Regex, string Replacement);
}
