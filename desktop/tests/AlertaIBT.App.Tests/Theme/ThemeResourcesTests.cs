using System.Windows;
using System.Windows.Media;

namespace AlertaIBT.App.Tests.Theme;

public sealed class ThemeResourcesTests
{
    private static readonly string[] LucideKeys =
    [
        "LucideSend", "LucideGlobe", "LucideMonitor", "LucidePause", "LucidePlay", "LucidePower", "LucideZap", "LucideX",
        "LucideCheck", "LucideInfo", "LucideLock", "LucideArrowUp", "LucideArrowDown", "LucideRotateCcw", "LucidePanelTop",
        "LucideAppWindow", "LucideSiren", "LucideWandSparkles", "LucideWifiOff", "LucideCircleAlert", "LucideTriangleAlert",
        "LucideCircleCheck", "LucideCircleX", "LucideBan", "LucideClock", "LucideCircleStop", "LucideSkipForward", "LucideTrash2",
        "LucideEraser", "LucideHouse", "LucideSpellCheck", "LucideSettings", "LucideLockOpen", "LucideScrollText",
        "LucideChevronRight", "LucideCopy", "LucideDownload", "LucideSparkles", "LucideVolume2",
    ];

    [Fact]
    public void Every_lucide_icon_is_a_geometry_that_has_a_visible_shape_inside_the_24_grid()
    {
        WpfTestHost.Run(() =>
        {
            foreach (var key in LucideKeys)
            {
                var geometry = Assert.IsAssignableFrom<Geometry>(Application.Current.Resources[key]);
                var bounds = geometry.Bounds;
                Assert.False(bounds.IsEmpty, key);
                Assert.True(bounds.Width > 1 && bounds.Height > 1, key);
                Assert.True(bounds.Left >= -0.01 && bounds.Top >= -0.01 && bounds.Right <= 24.01 && bounds.Bottom <= 24.01, $"{key} {bounds}");
            }
        });
    }

    [Fact]
    public void The_icon_style_is_a_round_two_pixel_stroke_without_fill()
    {
        WpfTestHost.Run(() =>
        {
            var style = Assert.IsType<Style>(Application.Current.Resources["LucideIcon"]);
            var setters = style.Setters.OfType<Setter>().ToDictionary(s => s.Property.Name, s => s.Value);

            Assert.Equal(2.0, setters["StrokeThickness"]);
            Assert.Equal(PenLineCap.Round, setters["StrokeStartLineCap"]);
            Assert.Equal(PenLineCap.Round, setters["StrokeEndLineCap"]);
            Assert.Equal(PenLineJoin.Round, setters["StrokeLineJoin"]);
        });
    }

    [Fact]
    public void The_accent_is_the_church_green()
    {
        WpfTestHost.Run(() =>
        {
            var green = Assert.IsType<Color>(Application.Current.Resources["IbtGreen"]);

            Assert.Equal(Color.FromRgb(0x1D, 0xA3, 0x5F), green);
        });
    }

    [Fact]
    public void Marble_ships_inside_the_app_with_every_weight()
    {
        WpfTestHost.Run(() =>
        {
            var family = Assert.IsType<FontFamily>(Application.Current.Resources["MarbleFont"]);
            FontWeight[] weights = [FontWeights.Light, FontWeights.Normal, FontWeights.Medium, FontWeights.SemiBold, FontWeights.Bold, FontWeights.ExtraBold];

            foreach (var weight in weights)
            {
                var typeface = new Typeface(family, FontStyles.Normal, weight, FontStretches.Normal);
                Assert.True(typeface.TryGetGlyphTypeface(out var glyphs), $"weight {weight}");
                Assert.Contains(glyphs.FamilyNames.Values, name => name.StartsWith("Marble", StringComparison.Ordinal));
                Assert.Equal(weight.ToOpenTypeWeight(), glyphs.Weight.ToOpenTypeWeight());
            }
        });
    }

    [Fact]
    public void Marble_covers_the_characters_of_a_portuguese_alert()
    {
        WpfTestHost.Run(() =>
        {
            var family = (FontFamily)Application.Current.Resources["MarbleFont"];
            var typeface = new Typeface(family, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
            Assert.True(typeface.TryGetGlyphTypeface(out var glyphs));

            // Upper case with accents, digits, the separators of the alert format.
            foreach (var c in "COROLLA PRATA | PLACA: ABC-1D23 | VIDRO ABERTO ÁÃÂÉÊÍÓÕÔÚÇ 0123456789")
            {
                Assert.True(glyphs.CharacterToGlyphMap.ContainsKey(c), $"missing U+{(int)c:X4}");
            }
        });
    }
}
