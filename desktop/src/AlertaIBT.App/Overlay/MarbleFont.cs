using System.Windows.Media;

namespace AlertaIBT.App.Overlay;

/// <summary>The Marble font that ships inside the app (<c>assets/fonts/*.ttf</c>, built in as resources).</summary>
public static class MarbleFont
{
    private static readonly Uri FolderUri = new("pack://application:,,,/AlertaIBT;component/Assets/Fonts/", UriKind.Absolute);

    /// <summary>
    /// The family "Marble" with its six weights. Built from the folder URI and a relative name: the single-string
    /// <see cref="FontFamily"/> constructor does not understand pack addresses.
    /// </summary>
    public static FontFamily Family() => new(FolderUri, "./#Marble");

    /// <summary>The bold face the bar uses.</summary>
    public static Typeface Bold() => new(Family(), System.Windows.FontStyles.Normal, System.Windows.FontWeights.Bold, System.Windows.FontStretches.Normal);

    /// <summary>A system face for characters Marble lacks.</summary>
    public static Typeface Fallback() => new(new FontFamily("Segoe UI"), System.Windows.FontStyles.Normal, System.Windows.FontWeights.Bold, System.Windows.FontStretches.Normal);
}
