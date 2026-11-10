namespace AlertaIBT.Core.Overlay;

/// <summary>A rectangle in physical pixels.</summary>
/// <param name="X">Left.</param>
/// <param name="Y">Top.</param>
/// <param name="Width">Width.</param>
/// <param name="Height">Height.</param>
public readonly record struct PixelRect(int X, int Y, int Width, int Height);

/// <summary>
/// "Do not move or resize a window when nothing changed". Every <c>SetWindowPos</c> that changes something makes WPF handle a <c>WM_WINDOWPOSCHANGED</c>
/// (<c>HwndTarget.UpdateWindowPos</c>), and that is the very call the window system sometimes refuses with error 1816; a call that changes nothing is a refusal waiting
/// to happen for no reason. The bar and the camera window ask this before placing themselves.
/// </summary>
public static class PlacementGuard
{
    /// <summary>Whether a window whose rectangle is <paramref name="actual"/> (null when it cannot be read) has to be moved to <paramref name="wanted"/>.</summary>
    public static bool NeedsMove(PixelRect? actual, PixelRect wanted) => actual is not { } current || current != wanted;
}
