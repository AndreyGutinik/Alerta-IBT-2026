using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;

namespace AlertaIBT.App.Overlay;

/// <summary>
/// The text of the bar, laid out and turned into glyph runs once. After that it is only moved (a render transform on this
/// element), never measured or drawn again, which is what keeps the scroll smooth. Letters are spaced by a fixed share of
/// the font size (WPF has no letter-spacing property, so the glyphs are placed by hand). A character the main font lacks is
/// drawn with the fallback font instead of showing an empty box.
/// </summary>
internal sealed class MarqueeText : FrameworkElement
{
    private readonly List<(GlyphRun Run, double X)> _runs = [];
    private Brush _foreground = Brushes.White;
    private double _padding;
    private double _baseline;

    /// <summary>The width of the element: the glyphs plus the margin on both sides, in device-independent units.</summary>
    public double ContentWidth { get; private set; }

    /// <summary>How many glyphs were laid out (characters with no glyph in any font are skipped).</summary>
    public int GlyphCount { get; private set; }

    /// <summary>
    /// Lays the text out. Call it once per alert, before the first frame.
    /// </summary>
    /// <param name="text">The text, one line.</param>
    /// <param name="primary">The main typeface (Marble Bold).</param>
    /// <param name="fallback">Used for characters the main typeface lacks.</param>
    /// <param name="fontSize">The font size in device-independent units.</param>
    /// <param name="letterSpacingEm">The extra space after each letter, in em.</param>
    /// <param name="padding">The blank margin on each side, in device-independent units.</param>
    /// <param name="barHeight">The height the text is centered in, in device-independent units.</param>
    /// <param name="foreground">The text color.</param>
    /// <param name="pixelsPerDip">The monitor scale, for crisp glyph placement.</param>
    public void Layout(
        string text,
        Typeface primary,
        Typeface fallback,
        double fontSize,
        double letterSpacingEm,
        double padding,
        double barHeight,
        Brush foreground,
        double pixelsPerDip)
    {
        ArgumentNullException.ThrowIfNull(text);

        _runs.Clear();
        _foreground = foreground;
        _padding = padding;
        GlyphCount = 0;

        var mainGlyphs = Glyphs(primary);
        var fallbackGlyphs = Glyphs(fallback);
        var spacing = fontSize * letterSpacingEm;

        // The line is centered in the bar by the main font's own line metrics.
        var lineHeight = mainGlyphs.Height * fontSize;
        _baseline = ((barHeight - lineHeight) / 2) + (mainGlyphs.Baseline * fontSize);

        var x = padding;
        var segment = new Segment();
        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.IsControl(rune))
            {
                continue;
            }

            var glyphs = mainGlyphs;
            if (!glyphs.CharacterToGlyphMap.TryGetValue(rune.Value, out var index))
            {
                glyphs = fallbackGlyphs;
                if (!glyphs.CharacterToGlyphMap.TryGetValue(rune.Value, out index))
                {
                    continue;
                }
            }

            if (segment.Typeface is not null && !ReferenceEquals(segment.Typeface, glyphs))
            {
                x = Flush(segment, fontSize, x, pixelsPerDip);
                segment = new Segment();
            }

            segment.Typeface = glyphs;
            segment.Indices.Add(index);
            segment.Advances.Add((glyphs.AdvanceWidths[index] * fontSize) + spacing);
            GlyphCount++;
        }

        x = Flush(segment, fontSize, x, pixelsPerDip);
        ContentWidth = x + padding;
        Width = ContentWidth;
        Height = barHeight;
        InvalidateVisual();
    }

    /// <inheritdoc />
    protected override void OnRender(DrawingContext drawingContext)
    {
        foreach (var (run, _) in _runs)
        {
            drawingContext.DrawGlyphRun(_foreground, run);
        }
    }

    private static GlyphTypeface Glyphs(Typeface typeface) =>
        typeface.TryGetGlyphTypeface(out var glyphs)
            ? glyphs
            : throw new InvalidOperationException($"The font '{typeface.FontFamily}' could not be loaded.");

    private double Flush(Segment segment, double fontSize, double x, double pixelsPerDip)
    {
        if (segment.Typeface is null || segment.Indices.Count == 0)
        {
            return x;
        }

        var run = new GlyphRun(
            segment.Typeface,
            bidiLevel: 0,
            isSideways: false,
            renderingEmSize: fontSize,
            pixelsPerDip: (float)pixelsPerDip,
            glyphIndices: segment.Indices,
            baselineOrigin: new Point(x, _baseline),
            advanceWidths: segment.Advances,
            glyphOffsets: null,
            characters: null,
            deviceFontName: null,
            clusterMap: null,
            caretStops: null,
            language: XmlLanguage.GetLanguage(CultureInfo.GetCultureInfo("pt-BR").IetfLanguageTag));
        _runs.Add((run, x));
        return x + segment.Advances.Sum();
    }

    private sealed class Segment
    {
        public GlyphTypeface? Typeface { get; set; }

        public List<ushort> Indices { get; } = [];

        public List<double> Advances { get; } = [];
    }
}
