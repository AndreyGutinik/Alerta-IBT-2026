using System.Globalization;
using System.Text;

namespace AlertaIBT.Core.Alerts;

/// <summary>
/// Builds the text the LED bar shows: <c>MODELO COR | PLACA: ABC-1234 | MOTIVO</c>, or just the announcement, always
/// in upper case, on one line and without emoji (the LED font has no glyphs for them). Nothing is ever cut.
/// </summary>
public static class AlertFormatter
{
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    /// <summary>
    /// The text of a vehicle alert: model and color, <c>PLACA: </c> and the plate, then the reason, separated by
    /// <c>" | "</c>. A part that is empty is left out together with its separator.
    /// </summary>
    /// <remarks>
    /// Every field is cleaned again (see <see cref="FormatFree"/>), so an alert built elsewhere, such as the answer
    /// of the Gemini corrector, may come in any case.
    /// </remarks>
    public static string Format(ParsedAlert alert)
    {
        ArgumentNullException.ThrowIfNull(alert);

        var plate = Tidy(alert.Plate);
        string[] parts =
        [
            Tidy($"{alert.Model} {alert.Color}"),
            plate.Length > 0 ? $"PLACA: {plate}" : string.Empty,
            Tidy(alert.Reason),
        ];

        return string.Join(" | ", parts.Where(part => part.Length > 0));
    }

    /// <summary>
    /// The text of an announcement: no emoji, every run of whitespace (line breaks included) turned into one space,
    /// trimmed, upper case. Nothing else changes, and nothing is cut.
    /// </summary>
    public static string FormatFree(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        return Tidy(text);
    }

    /// <summary>
    /// Drops what the LED font cannot draw: every character whose Unicode category is Other Symbol, Modifier Symbol or
    /// Enclosing Mark (emoji, pictographs, skin tones, the keycap that encloses a digit), plus the invisible glue of
    /// emoji sequences (zero width joiner and the variation selectors). A lone surrogate cannot be encoded and goes
    /// too.
    /// </summary>
    internal static string RemovePictographs(string text)
    {
        var kept = new StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            if (!IsPictograph(rune))
            {
                kept.Append(rune);
            }
        }

        return kept.ToString();
    }

    /// <summary>Without pictographs, on one line, trimmed and in upper case (pt-BR); empty for <c>null</c>.</summary>
    internal static string Tidy(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var words = RemovePictographs(text).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', words).ToUpper(PtBr);
    }

    private static bool IsPictograph(Rune rune) =>
        rune.Value is 0x200D or 0xFE0E or 0xFE0F
        || Rune.GetUnicodeCategory(rune)
            is UnicodeCategory.OtherSymbol or UnicodeCategory.ModifierSymbol or UnicodeCategory.EnclosingMark;
}
