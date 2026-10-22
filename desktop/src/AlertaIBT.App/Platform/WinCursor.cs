using System.Runtime.InteropServices;
using AlertaIBT.Core.Input;
using AlertaIBT.Core.Screens;

namespace AlertaIBT.App.Platform;

/// <summary>
/// The real cursor for <see cref="MouseLockService"/>: <c>ClipCursor</c>, <c>GetClipCursor</c>, <c>GetCursorPos</c>, <c>SetCursorPos</c> and the
/// primary monitor from <c>GetMonitorInfo(MonitorFromPoint(0, 0, MONITOR_DEFAULTTOPRIMARY))</c>. The app manifest makes the process
/// per-monitor aware (v2), so every number here is a physical pixel of the virtual desktop and matches the real monitor on a
/// mixed-DPI setup. Only the running app uses this class: tests use a fake, because the real one grabs the real mouse.
/// </summary>
public sealed class WinCursor : INativeCursor
{
    private static readonly int MonitorInfoSize = Marshal.SizeOf<NativeMethods.MONITORINFO>();

    /// <summary>One line for the log: whether the process sees physical pixels (per-monitor aware v2) or Windows scales the numbers.</summary>
    public static string DescribeDpiAwareness()
    {
        try
        {
            return NativeMethods.AreDpiAwarenessContextsEqual(
                NativeMethods.GetThreadDpiAwarenessContext(),
                NativeMethods.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2)
                ? "DPI por monitor v2 (pixels fisicos)"
                : "DPI NAO e por monitor v2 (as coordenadas podem ser escalonadas)";
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            return "DPI desconhecido";
        }
    }

    /// <inheritdoc />
    public bool TryGetPrimaryRect(out ScreenRect rect)
    {
        var info = new NativeMethods.MONITORINFO { cbSize = MonitorInfoSize };
        var monitor = NativeMethods.MonitorFromPoint(default, NativeMethods.MONITOR_DEFAULTTOPRIMARY);
        if (monitor != IntPtr.Zero && NativeMethods.GetMonitorInfo(monitor, ref info))
        {
            rect = From(info.rcMonitor);
            return true;
        }

        // Rare: ask the list of monitors instead (allocates, so only on this path).
        if (PrimaryMonitorRect.From(MonitorService.Enumerate()) is { } fallback)
        {
            rect = fallback;
            return true;
        }

        rect = default;
        return false;
    }

    /// <inheritdoc />
    public bool TryGetClip(out ScreenRect rect)
    {
        if (NativeMethods.GetClipCursor(out var native))
        {
            rect = From(native);
            return true;
        }

        rect = default;
        return false;
    }

    /// <inheritdoc />
    public bool TryClip(ScreenRect rect)
    {
        var native = new NativeMethods.RECT { Left = rect.Left, Top = rect.Top, Right = rect.Right, Bottom = rect.Bottom };
        return NativeMethods.ClipCursor(ref native);
    }

    /// <inheritdoc />
    public void Unclip() => NativeMethods.ClipCursorRelease(IntPtr.Zero);

    /// <inheritdoc />
    public bool TryGetPosition(out int x, out int y)
    {
        if (NativeMethods.GetCursorPos(out var point))
        {
            x = point.X;
            y = point.Y;
            return true;
        }

        x = 0;
        y = 0;
        return false;
    }

    /// <inheritdoc />
    public bool TrySetPosition(int x, int y) => NativeMethods.SetCursorPos(x, y);

    private static ScreenRect From(NativeMethods.RECT rect) => new(rect.Left, rect.Top, rect.Right, rect.Bottom);
}
