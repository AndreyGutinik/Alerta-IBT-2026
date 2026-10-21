using System.Text.RegularExpressions;

namespace AlertaIBT.Core.Alerts;

/// <summary>
/// Reads Brazilian license plates: the old format (ABC-1234) and Mercosul (ABC1D23, shown as ABC-1D23).
/// </summary>
public static partial class PlateParser
{
    private const int PlateLength = 7;

    /// <summary>
    /// Returns the plate in its display form (<c>ABC-1234</c> or <c>ABC-1D23</c>), or <c>null</c> when
    /// <paramref name="candidate"/> cannot be a plate.
    /// </summary>
    /// <remarks>
    /// Spaces, hyphens and punctuation are ignored; what is left must be exactly 7 letters and digits.
    /// The 1st to 3rd characters are letters (<c>0 1 5 8</c> are read as <c>O I S B</c>), the 4th, 6th and 7th
    /// are numbers (<c>O I S B</c> are read as <c>0 1 5 8</c>) and the 5th stays as typed, because it is a letter
    /// in Mercosul plates and a number in old ones. Letters and digits outside A-Z and 0-9 are never dropped:
    /// they make the text "not a plate".
    /// </remarks>
    public static string? Normalize(string candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        Span<char> typed = stackalloc char[PlateLength];
        var count = 0;
        foreach (var c in candidate)
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                if (count == PlateLength)
                {
                    return null;
                }

                typed[count++] = char.ToUpperInvariant(c);
            }
            else if (char.IsLetterOrDigit(c))
            {
                return null;
            }
        }

        if (count != PlateLength)
        {
            return null;
        }

        Span<char> plate = stackalloc char[PlateLength + 1];
        for (var i = 0; i < PlateLength; i++)
        {
            var slot = i switch
            {
                < 3 => AsLetter(typed[i]),
                4 => typed[i],
                _ => AsDigit(typed[i]),
            };
            if (slot is not { } character)
            {
                return null;
            }

            plate[i < 3 ? i : i + 1] = character;
        }

        plate[3] = '-';
        return new string(plate);
    }

    /// <summary>
    /// Looks for a plate inside free text, for example <c>gol branco placa ffo4745 farol aceso</c>.
    /// </summary>
    /// <param name="text">Free text typed by a person.</param>
    /// <param name="plate">The plate found, in display form, or an empty string when there is none.</param>
    /// <param name="rest">
    /// The text without the plate and without the word "placa" (or "placa:", "placa-") that labels it. A line break
    /// (<c>\n</c>) takes the place of the plate and its label, because the plate is the most reliable boundary
    /// between the vehicle and the reason: <c>hb20 vermelho abc1234 parado na garagem</c> gives
    /// <c>hb20 vermelho\nparado na garagem</c>. Line breaks already in the text are kept too, so callers can split on
    /// them; inside each line, every run of whitespace (spaces, tabs) becomes one space and the line is trimmed;
    /// lines left empty (a plate alone on its line, at either end of the text, blank lines) are dropped. When no
    /// plate is found it is the same tidied text.
    /// </param>
    /// <returns><c>true</c> when a plate was found.</returns>
    /// <remarks>
    /// A candidate is three letters, an optional separator and four more characters that follow the slot rules of
    /// <see cref="Normalize"/>, standing alone between spaces, punctuation or the ends of the text. It must contain
    /// a digit: plain words such as "motivos" fit the slots too.
    /// A candidate written in one piece (<c>abc1234</c>) or joined by a hyphen, with or without spaces around it
    /// (<c>abc-1234</c>, <c>gbr- 9003</c>), wins over any candidate separated only by spaces, wherever each one sits
    /// in the text. A spaced candidate (<c>QWE 1B23</c>, <c>abc 1234</c>) is accepted only when it is Mercosul-shaped
    /// (a letter in the 5th position) or comes right after the word "placa"; otherwise <c>gol 2015</c> would read as
    /// a plate. Among candidates of the same kind the first one wins.
    /// </remarks>
    public static bool TryExtract(string text, out string plate, out string rest)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (Find(text) is not { } found)
        {
            plate = string.Empty;
            rest = TidyLines(text);
            return false;
        }

        plate = found.Plate;

        // A line break takes the place of the plate and its label.
        var end = found.Match.Index + found.Match.Length;
        rest = TidyLines(string.Concat(text.AsSpan(0, found.Match.Index), "\n", text.AsSpan(end)));
        return true;
    }

    /// <summary>
    /// Finds the same plate as <see cref="TryExtract"/> and says where it sits in <paramref name="text"/>, for callers
    /// that must leave that part of the text alone.
    /// </summary>
    /// <param name="text">Free text typed by a person.</param>
    /// <param name="index">Where the plate starts, label (<c>placa:</c>) included; 0 when there is none.</param>
    /// <param name="length">How long it is, label included; 0 when there is none.</param>
    /// <returns><c>true</c> when a plate was found.</returns>
    internal static bool TryLocate(string text, out int index, out int length)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (Find(text) is not { } found)
        {
            index = length = 0;
            return false;
        }

        index = found.Match.Index;
        length = found.Match.Length;
        return true;
    }

    // The plate TryExtract and TryLocate agree on: a joined candidate wins over a spaced one, the first of its kind.
    private static Candidate? Find(string text)
    {
        Candidate? firstSpaced = null;
        foreach (Match match in CandidatePattern().Matches(text))
        {
            if (Evaluate(match) is not { } candidate)
            {
                continue;
            }

            if (!candidate.IsSpaced)
            {
                return candidate;
            }

            firstSpaced ??= candidate;
        }

        return firstSpaced;
    }

    // Three letters, an optional separator, then [0-9OISB][0-9A-Za-z][0-9OISB]{2} (O I S B in either case),
    // not glued to other letters or digits, with the word "placa" (and an optional ":" or "-") swallowed when it
    // sits right before the plate. Groups: label (the word "placa"), plate (the candidate itself) and separator
    // (what sits between its letters and its numbers).
    // The separator is written as (\s*-\s*|\s+)? instead of \s*-?\s*: it accepts the same text, but does not
    // backtrack quadratically over a long run of spaces.
    [GeneratedRegex(
        @"(?<![\p{L}\p{N}])(?<label>(?i:placa)\b\s*(?:[:\-]\s*)?)?(?<plate>[A-Za-z]{3}(?<separator>\s*-\s*|\s+)?[0-9OoIiSsBb][0-9A-Za-z][0-9OoIiSsBb]{2})(?![\p{L}\p{N}])",
        RegexOptions.CultureInvariant)]
    private static partial Regex CandidatePattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRun();

    // Turns a regex match into a plate, or null when it is not one: no digit at all, or separated only by spaces
    // without being Mercosul-shaped or labeled ("gol 2015" is a word and a number, not a plate).
    private static Candidate? Evaluate(Match match)
    {
        var typed = match.Groups["plate"].Value;
        if (!ContainsDigit(typed) || Normalize(typed) is not { } plate)
        {
            return null;
        }

        var separator = match.Groups["separator"].Value;
        var isSpaced = separator.Length > 0 && !separator.Contains('-');

        // plate[5] is the 5th character of the plate: a letter only on Mercosul plates.
        if (isSpaced && !match.Groups["label"].Success && !char.IsAsciiLetter(plate[5]))
        {
            return null;
        }

        return new Candidate(match, plate, isSpaced);
    }

    private static char? AsLetter(char c) => c switch
    {
        >= 'A' and <= 'Z' => c,
        '0' => 'O',
        '1' => 'I',
        '5' => 'S',
        '8' => 'B',
        _ => null,
    };

    private static char? AsDigit(char c) => c switch
    {
        >= '0' and <= '9' => c,
        'O' => '0',
        'I' => '1',
        'S' => '5',
        'B' => '8',
        _ => null,
    };

    private static bool ContainsDigit(string text) => text.AsSpan().ContainsAnyInRange('0', '9');

    // Keeps the line breaks (as \n); inside each line, runs of whitespace become one space and the line is
    // trimmed; lines left empty are dropped.
    private static string TidyLines(string text) =>
        string.Join(
            '\n',
            text.ReplaceLineEndings("\n")
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(line => WhitespaceRun().Replace(line, " ")));

    // IsSpaced: the plate's letters and numbers are separated only by spaces ("abc 1234"), not joined or hyphenated.
    private readonly record struct Candidate(Match Match, string Plate, bool IsSpaced);
}
