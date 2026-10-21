using System.Windows;
using System.Windows.Interop;
using Microsoft.Extensions.Logging;

namespace AlertaIBT.App.Platform;

/// <summary>
/// Hides a window from every kind of screen capture (<c>SetWindowDisplayAffinity</c> with <c>WDA_EXCLUDEFROMCAPTURE</c>): OBS
/// "Captura de tela", NDI and screenshots show the window as if it were not there. The bar of the LED and every other window
/// of the app (quick panel, main window, dialogs) go through here, so there is one way to do it and one way to check it.
/// </summary>
internal static class CaptureExclusion
{
    /// <summary>Excludes the window with this handle and checks that Windows really took it.</summary>
    public static bool Apply(IntPtr hwnd) =>
        NativeMethods.SetWindowDisplayAffinity(hwnd, NativeMethods.WDA_EXCLUDEFROMCAPTURE) && IsExcluded(hwnd);

    /// <summary>Whether Windows reports the window as excluded from capture.</summary>
    public static bool IsExcluded(IntPtr hwnd) =>
        NativeMethods.GetWindowDisplayAffinity(hwnd, out var affinity) && affinity == NativeMethods.WDA_EXCLUDEFROMCAPTURE;

    /// <summary>
    /// Makes the handle of <paramref name="window"/> now (hidden) and excludes it, so no pixel of it is ever shown to a capture,
    /// even the first one. Call it right after <c>InitializeComponent</c>, before the window is shown. The exclusion belongs to
    /// the handle: it stays while the window is hidden and shown again.
    /// </summary>
    /// <param name="window">The window.</param>
    /// <param name="logger">Receives an error line when Windows refuses (a Windows older than 10 2004).</param>
    /// <returns>Whether the window is excluded.</returns>
    public static bool Apply(Window window, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(window);

        var applied = Apply(new WindowInteropHelper(window).EnsureHandle());
        if (!applied)
        {
            logger?.LogError(
                "A janela {Window} NAO esta excluida da captura de tela (SetWindowDisplayAffinity falhou): o OBS poderia mostra-la",
                window.GetType().Name);
        }

        return applied;
    }
}
