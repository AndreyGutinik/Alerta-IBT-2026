using System.Runtime.InteropServices;
using AlertaIBT.App.Platform;
using AlertaIBT.Core.Screens;
using Microsoft.Extensions.Logging;

namespace AlertaIBT.App.Ndi;

/// <summary>
/// The window the camera is drawn on. It is a plain Win32 popup (no WPF content; Direct3D owns its pixels), and its place in the stack is the whole point:
/// <list type="bullet">
/// <item>It is NOT topmost and sits at the bottom of the normal z-order (<c>HWND_BOTTOM</c>), so the owner's Holyrics projection window (a normal, transparent window)
/// draws over it and the alert bar (topmost) draws over both. Windows orders three bands: topmost windows (the bar) above everything normal; inside the normal band
/// the most recently raised/activated window is first, and nothing in the app ever activates this one.</item>
/// <item>No taskbar entry or Alt+Tab (tool window), never activated (<c>WS_EX_NOACTIVATE</c>, <c>WM_MOUSEACTIVATE</c> answers no-activate), and click-through
/// (<c>WS_EX_LAYERED | WS_EX_TRANSPARENT</c> plus <c>HTTRANSPARENT</c> hit tests).</item>
/// <item>It is excluded from screen capture only when "Esconder da live" is on (off by default: OBS and NDI may see the camera).</item>
/// </list>
/// Create it on the UI thread.
/// </summary>
public sealed class NdiVideoWindow : IDisposable
{
    private const int WsPopup = unchecked((int)0x80000000);
    private const string ClassName = "IBTControllerCameraWindow";

    private static readonly object ClassGate = new();
    private static WndProc? _classProc; // kept alive for the whole process: the window class points at it
    private static bool _classRegistered;

    private readonly IntPtr _hwnd;
    private readonly ILogger? _logger;
    private bool _shown;
    private bool _captureExcluded;

    private delegate IntPtr WndProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    /// <summary>Creates the window hidden, at the bottom of the z-order.</summary>
    public NdiVideoWindow(ILogger? logger = null)
    {
        _logger = logger;
        RegisterClass();

        // Made by hand (not with a WPF HwndSource, which does not keep the layered bit) so the styles are exactly these.
        _hwnd = CreateWindowEx(
            NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_LAYERED,
            ClassName,
            "IBT Controller camera",
            WsPopup,
            -32000,
            -32000,
            64,
            64,
            IntPtr.Zero,
            IntPtr.Zero,
            NativeMethods.GetModuleHandle(null),
            IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
        {
            throw new InvalidOperationException("N\U000000E3o foi poss\U000000EDvel criar a janela da c\U000000E2mera");
        }

        // A layered window draws nothing until it has its alpha; 255 is fully opaque.
        NativeMethods.SetLayeredWindowAttributes(_hwnd, 0, 255, NativeMethods.LWA_ALPHA);
        NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_BOTTOM, -32000, -32000, 64, 64, NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_HIDEWINDOW);
    }

    /// <summary>The window handle.</summary>
    public IntPtr Handle => _hwnd;

    /// <summary>Whether the window is shown now.</summary>
    public bool IsShown => _shown;

    /// <summary>Whether the window is currently excluded from screen capture.</summary>
    public bool CaptureExcluded => _captureExcluded;

    /// <summary>Puts the window over <paramref name="screen"/> (physical pixels), still at the bottom of the z-order; does not show or hide it.</summary>
    public void Place(ScreenInfo screen)
    {
        ArgumentNullException.ThrowIfNull(screen);

        var flags = NativeMethods.SWP_NOACTIVATE | (_shown ? NativeMethods.SWP_SHOWWINDOW : NativeMethods.SWP_HIDEWINDOW);
        NativeMethods.SetWindowPos(Handle, NativeMethods.HWND_BOTTOM, screen.X, screen.Y, screen.Width, screen.Height, flags);
    }

    /// <summary>Shows the window without activating it, at the bottom of the z-order.</summary>
    public void Show()
    {
        _shown = true;
        NativeMethods.SetWindowPos(
            Handle,
            NativeMethods.HWND_BOTTOM,
            0,
            0,
            0,
            0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
    }

    /// <summary>Hides the window.</summary>
    public void Hide()
    {
        _shown = false;
        NativeMethods.SetWindowPos(
            Handle,
            IntPtr.Zero,
            0,
            0,
            0,
            0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_HIDEWINDOW);
    }

    /// <summary>Pushes the window to the bottom again (cheap, harmless when it already is there): run it every couple of seconds while shown.</summary>
    public void KeepAtBottom()
    {
        if (_shown)
        {
            NativeMethods.SetWindowPos(
                Handle,
                NativeMethods.HWND_BOTTOM,
                0,
                0,
                0,
                0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
        }
    }

    /// <summary>"Esconder da live": excludes the window from screen capture (OBS, NDI screen capture, screenshots), or includes it again. Returns whether Windows took it.</summary>
    public bool SetHiddenFromCapture(bool hidden)
    {
        var ok = NativeMethods.SetWindowDisplayAffinity(Handle, hidden ? NativeMethods.WDA_EXCLUDEFROMCAPTURE : NativeMethods.WDA_NONE);
        _captureExcluded = ok && hidden && CaptureExclusion.IsExcluded(Handle);
        if (!ok)
        {
            _logger?.LogWarning("Nao foi possivel mudar a exclusao da captura da janela da camera");
        }

        return ok;
    }

    /// <summary>The z-order facts the tests and the diagnostics need.</summary>
    public WindowFacts Facts() => WindowFacts.Of(Handle);

    /// <inheritdoc />
    public void Dispose()
    {
        if (NativeMethods.IsWindow(_hwnd))
        {
            DestroyWindow(_hwnd);
        }
    }

    private static void RegisterClass()
    {
        lock (ClassGate)
        {
            if (_classRegistered)
            {
                return;
            }

            _classProc = Proc;
            var windowClass = new WndClassEx
            {
                Size = (uint)Marshal.SizeOf<WndClassEx>(),
                WndProc = Marshal.GetFunctionPointerForDelegate(_classProc),
                Instance = NativeMethods.GetModuleHandle(null),
                ClassName = ClassName,
            };
            if (RegisterClassEx(ref windowClass) == 0)
            {
                throw new InvalidOperationException("N\U000000E3o foi poss\U000000EDvel registrar a janela da c\U000000E2mera");
            }

            _classRegistered = true;
        }
    }

    private static IntPtr Proc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        switch ((int)message)
        {
            case NativeMethods.WM_MOUSEACTIVATE:
                return (IntPtr)NativeMethods.MA_NOACTIVATE;
            case NativeMethods.WM_NCHITTEST:
                return (IntPtr)NativeMethods.HTTRANSPARENT;
            case NativeMethods.WM_ERASEBKGND:
                return (IntPtr)1; // Direct3D paints every pixel
            default:
                return DefWindowProc(hwnd, message, wParam, lParam);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public uint Size;
        public uint Style;
        public IntPtr WndProc;
        public int ClassExtra;
        public int WindowExtra;
        public IntPtr Instance;
        public IntPtr Icon;
        public IntPtr Cursor;
        public IntPtr Background;
        public string? MenuName;
        public string ClassName;
        public IntPtr SmallIcon;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WndClassEx windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        int exStyle,
        string className,
        string windowName,
        int style,
        int x,
        int y,
        int width,
        int height,
        IntPtr parent,
        IntPtr menu,
        IntPtr instance,
        IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
}

/// <summary>What Win32 says about one window: its style bits and where it stands in the z-order.</summary>
/// <param name="ExStyle">The extended style bits.</param>
/// <param name="Visible">Whether the window is visible.</param>
public readonly record struct WindowFacts(int ExStyle, bool Visible)
{
    /// <summary>Whether the window is topmost.</summary>
    public bool Topmost => (ExStyle & NativeMethods.WS_EX_TOPMOST) != 0;

    /// <summary>Whether it has no taskbar button / Alt+Tab entry (tool window).</summary>
    public bool ToolWindow => (ExStyle & NativeMethods.WS_EX_TOOLWINDOW) != 0;

    /// <summary>Whether it never takes the focus.</summary>
    public bool NoActivate => (ExStyle & NativeMethods.WS_EX_NOACTIVATE) != 0;

    /// <summary>Whether mouse clicks go through it (layered and transparent).</summary>
    public bool ClickThrough => (ExStyle & NativeMethods.WS_EX_TRANSPARENT) != 0 && (ExStyle & NativeMethods.WS_EX_LAYERED) != 0;

    /// <summary>Reads the facts of <paramref name="hwnd"/>.</summary>
    public static WindowFacts Of(IntPtr hwnd) => new(NativeMethods.GetExStyle(hwnd), NativeMethods.IsWindowVisible(hwnd));

    /// <summary>Whether <paramref name="upper"/> is above <paramref name="lower"/> in the z-order (both top-level windows).</summary>
    public static bool IsAbove(IntPtr upper, IntPtr lower)
    {
        // Walk down from the upper window: if the lower one is reached, the upper one is above it.
        var current = NativeMethods.GetWindow(upper, NativeMethods.GW_HWNDNEXT);
        var guard = 0;
        while (current != IntPtr.Zero && guard++ < 100000)
        {
            if (current == lower)
            {
                return true;
            }

            current = NativeMethods.GetWindow(current, NativeMethods.GW_HWNDNEXT);
        }

        return false;
    }
}
