using System.Text.RegularExpressions;

namespace AlertaIBT.Core.Alerts;

/// <summary>
/// Reads what a person typed (<c>#creta preto | placa:gbr- 9003 / farol aceso</c>) into model, color, plate and
/// reason. It is deterministic and has no dictionary: spelling is somebody else's job (the correctors).
/// </summary>
public static partial class AlertParser
{
    /// <summary>
    /// Reads <paramref name="raw"/>. Emoji are dropped, the leading <c>#</c> is removed and everything is upper case
    /// (pt-BR); nothing is cut.
    /// </summary>
    /// <returns>
    /// An alert whose <see cref="ParsedAlert.Model"/>, <see cref="ParsedAlert.Color"/> and
    /// <see cref="ParsedAlert.Reason"/> are empty when absent and whose <see cref="ParsedAlert.Plate"/> is
    /// <c>null</c> when there is none. <see cref="ParsedAlert.FreeText"/> is always the whole cleaned text.
    /// </returns>
    /// <remarks>
    /// <para>
    /// The text without the plate (<see cref="PlateParser.TryExtract"/>, which leaves a line break where the plate
    /// was) is split into segments at line breaks and at <c>| / ; ,</c>; a comma between two digits is part of a
    /// number (<c>1,0</c>) and does not split. Each segment is trimmed of whatever is not a letter or a digit at its
    /// ends, and words with no letter or digit (a lone <c>-</c>) are dropped.
    /// </para>
    /// <para>
    /// A segment whose first word is a trigger (<c>farol</c>, <c>vidro</c>, <c>parado</c>... with or without accents)
    /// is a reason. The first segment that is not is the vehicle, and only that one: if a trigger shows up later
    /// inside it, the words from the trigger on become a reason segment and the words before it stay the vehicle.
    /// Every other segment is a reason, and the reasons are joined with <c>", "</c>.
    /// </para>
    /// <para>
    /// In the vehicle, the first color word is the color (with the modifier that follows it, such as <c>marinho</c>)
    /// and the other words, in the order they were typed, are the model; the word <c>placa</c> is never part of a
    /// model. A vehicle without a color takes it from the start of the segment right after it
    /// (<c>gol abc1234 branco farol aceso</c>).
    /// </para>
    /// <para>
    /// Punctuation stuck to a word does not hide it: words are compared with the lists without the characters at
    /// their ends that are not letters or digits (<c>branco.</c>, <c>(farol</c>), and every word of the vehicle goes
    /// into the model and the color without them. A segment that is cut, such as the reason that starts at a trigger
    /// inside the vehicle or what is left of a segment after its color was taken, is trimmed at its new edge like any
    /// other segment. Inside a reason, punctuation stays as typed.
    /// </para>
    /// </remarks>
    public static ParsedAlert Parse(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);

        var text = AlertFormatter.RemovePictographs(raw).Trim().TrimStart('#');
        var found = PlateParser.TryExtract(text, out var plate, out var rest);

        var segments = ReadSegments(rest);
        var vehicle = TakeVehicle(segments, out var next);

        // The vehicle is out of the segments now: what is left in them are the reasons.
        var color = TakeColor(vehicle, segments, next);

        return new ParsedAlert(
            Model: AlertFormatter.Tidy(string.Join(' ', vehicle)),
            Color: AlertFormatter.Tidy(color),
            Plate: found ? plate : null,
            Reason: AlertFormatter.Tidy(string.Join(", ", segments.Select(words => string.Join(' ', words)))),
            FreeText: AlertFormatter.FormatFree(text));
    }

    // Where the text is cut into segments: line breaks, | / ; and commas, but not a comma with a digit on both sides
    // (1,0). A plate also leaves a line break behind (PlateParser.TryExtract).
    [GeneratedRegex(@"[\n|/;]|(?<!\p{Nd}),|,(?!\p{Nd})")]
    private static partial Regex SegmentSeparator();

    // The words of each segment, in order; segments with no word are left out.
    private static List<List<string>> ReadSegments(string rest)
    {
        var segments = new List<List<string>>();
        foreach (var segment in SegmentSeparator().Split(rest))
        {
            var words = Vocabulary.TrimEdges(segment)
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .Where(word => word.Any(char.IsLetterOrDigit))
                .ToList();
            if (words.Count > 0)
            {
                segments.Add(words);
            }
        }

        return segments;
    }

    // Takes the vehicle out of the segments and returns its words (trimmed at their ends, without "placa"): the first
    // segment that does not start with a trigger, cut at the first trigger inside it. What is left in `segments` are
    // the reasons, in order; `next` is where the segment right after the vehicle is (segments.Count when there is none).
    private static List<string> TakeVehicle(List<List<string>> segments, out int next)
    {
        var index = segments.FindIndex(words => !Vocabulary.IsReasonStart(words[0]));
        if (index < 0)
        {
            next = segments.Count;
            return [];
        }

        var vehicle = segments[index];
        var trigger = vehicle.FindIndex(1, Vocabulary.IsReasonStart);
        if (trigger >= 0)
        {
            segments.Insert(index + 1, TrimSegmentStart(vehicle.GetRange(trigger, vehicle.Count - trigger)));
            vehicle.RemoveRange(trigger, vehicle.Count - trigger);
        }

        segments.RemoveAt(index);
        next = index;
        return vehicle.Select(Vocabulary.TrimEdges).Where(word => Vocabulary.Fold(word) != "placa").ToList();
    }

    // The color is the first color word of the vehicle; failing that, the word that opens the segment right after the
    // vehicle. The modifier that follows the color word goes with it. Both are removed from where they were found.
    private static string TakeColor(List<string> vehicle, List<List<string>> reasons, int next)
    {
        var at = vehicle.FindIndex(Vocabulary.IsColor);
        if (at >= 0)
        {
            return Take(vehicle, at);
        }

        if (next < reasons.Count && Vocabulary.IsColor(reasons[next][0]))
        {
            var color = Take(reasons[next], 0);
            if (reasons[next].Count == 0)
            {
                reasons.RemoveAt(next);
            }
            else
            {
                TrimSegmentStart(reasons[next]);
            }

            return color;
        }

        return string.Empty;
    }

    // Removes the color word at `start` (and the modifier right after it, if there is one) and returns them, without
    // the punctuation stuck to their ends.
    private static string Take(List<string> words, int start)
    {
        var count = start + 1 < words.Count && Vocabulary.IsColorModifier(words[start + 1]) ? 2 : 1;
        var taken = string.Join(' ', words.GetRange(start, count).Select(Vocabulary.TrimEdges));
        words.RemoveRange(start, count);
        return taken;
    }

    // A segment cut at its start (the reason that begins at a trigger inside the vehicle, what is left of a reason after
    // its color) is trimmed there like any other segment. Its end is the end of the original segment, trimmed already,
    // and punctuation between its words stays.
    private static List<string> TrimSegmentStart(List<string> words)
    {
        words[0] = Vocabulary.TrimStart(words[0]);
        return words;
    }
}
