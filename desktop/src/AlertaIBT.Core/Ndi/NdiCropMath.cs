namespace AlertaIBT.Core.Ndi;

/// <summary>
/// "Fill and crop": a source picture covers the whole target without distortion. The scale is <c>max(tw/sw, th/sh)</c>; what overflows is
/// cut. For 1920x1080 on the 2560x768 LED: scale 1.3333, scaled picture 2560x1440, 768 rows visible (53.3%), 672 rows hidden, and the
/// vertical position (0 top .. 100 bottom) picks which band shows. All sizes are in scaled (target) pixels unless said otherwise.
/// </summary>
/// <param name="Scale">The factor applied to the source.</param>
/// <param name="ScaledWidth">Source width times the scale.</param>
/// <param name="ScaledHeight">Source height times the scale.</param>
/// <param name="HiddenRows">Scaled rows that fall outside the target (0 when the picture only overflows sideways).</param>
/// <param name="HiddenColumns">Scaled columns that fall outside the target.</param>
/// <param name="TopOffset">How many scaled rows are cut off the top (0 to <paramref name="HiddenRows"/>).</param>
/// <param name="LeftOffset">How many scaled columns are cut off the left (centered).</param>
/// <param name="VisibleFractionY">The share of the picture's height that shows (0.533 on the LED).</param>
/// <param name="U0">Left edge of the visible part in source texture coordinates (0..1).</param>
/// <param name="V0">Top edge of the visible part in source texture coordinates (0..1).</param>
/// <param name="U1">Right edge.</param>
/// <param name="V1">Bottom edge.</param>
public readonly record struct NdiCrop(
    double Scale,
    double ScaledWidth,
    double ScaledHeight,
    double HiddenRows,
    double HiddenColumns,
    double TopOffset,
    double LeftOffset,
    double VisibleFractionY,
    double U0,
    double V0,
    double U1,
    double V1)
{
    /// <summary>The source rectangle that shows, in source pixels: <c>(x, y, width, height)</c>.</summary>
    public (double X, double Y, double Width, double Height) SourceRect(int sourceWidth, int sourceHeight) =>
        (U0 * sourceWidth, V0 * sourceHeight, (U1 - U0) * sourceWidth, (V1 - V0) * sourceHeight);
}

/// <summary>The pure crop/stretch math, unit-tested.</summary>
public static class NdiCropMath
{
    /// <summary>
    /// Computes the crop of a <paramref name="sourceWidth"/>x<paramref name="sourceHeight"/> picture on a
    /// <paramref name="targetWidth"/>x<paramref name="targetHeight"/> target.
    /// </summary>
    /// <param name="sourceWidth">Source width, above 0.</param>
    /// <param name="sourceHeight">Source height, above 0.</param>
    /// <param name="targetWidth">Target width, above 0.</param>
    /// <param name="targetHeight">Target height, above 0.</param>
    /// <param name="verticalPosition">0 (top of the picture) to 100 (bottom); held in that range. When the picture overflows sideways instead, it is always centered and this is ignored.</param>
    public static NdiCrop Compute(int sourceWidth, int sourceHeight, int targetWidth, int targetHeight, double verticalPosition)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(sourceWidth, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(sourceHeight, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(targetWidth, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(targetHeight, 0);

        var position = double.IsNaN(verticalPosition) ? 50 : Math.Clamp(verticalPosition, 0, 100);
        var scale = Math.Max((double)targetWidth / sourceWidth, (double)targetHeight / sourceHeight);
        var scaledWidth = sourceWidth * scale;
        var scaledHeight = sourceHeight * scale;

        // One of the two overflows by (almost) exactly zero: tiny float noise is not a hidden band.
        var hiddenRows = Math.Max(0, scaledHeight - targetHeight);
        var hiddenColumns = Math.Max(0, scaledWidth - targetWidth);
        if (hiddenRows < 1e-6)
        {
            hiddenRows = 0;
        }

        if (hiddenColumns < 1e-6)
        {
            hiddenColumns = 0;
        }

        var top = hiddenRows * position / 100.0;
        var left = hiddenColumns / 2.0;

        var u0 = left / scaledWidth;
        var u1 = (left + Math.Min(targetWidth, scaledWidth)) / scaledWidth;
        var v0 = top / scaledHeight;
        var v1 = (top + Math.Min(targetHeight, scaledHeight)) / scaledHeight;

        return new NdiCrop(
            scale,
            scaledWidth,
            scaledHeight,
            hiddenRows,
            hiddenColumns,
            top,
            left,
            Math.Min(targetHeight, scaledHeight) / scaledHeight,
            u0,
            v0,
            Math.Min(1, u1),
            Math.Min(1, v1));
    }
}
