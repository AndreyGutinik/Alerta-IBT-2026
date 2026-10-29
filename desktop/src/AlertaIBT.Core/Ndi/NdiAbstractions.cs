namespace AlertaIBT.Core.Ndi;

/// <summary>Fixed texts about NDI that the app must show (About, diagnostics). One place, so the UI never retypes them.</summary>
public static class NdiConstants
{
    /// <summary>The trademark notice NDI asks for.</summary>
    public const string TrademarkNotice = "NDI\U000000AE is a registered trademark of Vizrt NDI AB";

    /// <summary>The ASCII form of <see cref="TrademarkNotice"/> (the notice as written by NDI: <c>NDI(R)</c>).</summary>
    public const string TrademarkNoticeAscii = "NDI(R) is a registered trademark of Vizrt NDI AB";

    /// <summary>The NDI site.</summary>
    public const string SiteUrl = "https://ndi.video";

    /// <summary>Where the owner downloads the free NDI Tools (they carry the runtime this app loads).</summary>
    public const string DownloadUrl = "https://ndi.video/tools/";

    /// <summary>The text of the "runtime not found" state, with the official download link.</summary>
    public const string RuntimeMissingText = "O runtime do NDI n\U000000E3o foi encontrado neste PC. Instale o NDI Tools (gratuito): " + DownloadUrl;

    /// <summary>The line the UI shows in About / diagnostics: the notice plus the link.</summary>
    public const string AboutText = TrademarkNoticeAscii + " - " + SiteUrl;

    /// <summary>How long the on-air picture stays frozen when its source stops sending, before the camera layer hides.</summary>
    public static readonly TimeSpan HoldTime = TimeSpan.FromSeconds(5);

    /// <summary>How long a Take waits for the preview's first frame before it gives up and reports an error.</summary>
    public static readonly TimeSpan TakeTimeout = TimeSpan.FromSeconds(8);
}

/// <summary>A source the finder reported.</summary>
/// <param name="Name">The NDI name, <c>MACHINE (Source)</c>.</param>
/// <param name="Address">The address the finder reported (<c>192.168.1.50:5961</c>), or empty.</param>
public sealed record NdiSourceInfo(string Name, string Address);

/// <summary>What a receiver connects to. At least one of the two is set. With both, the url wins (it needs no discovery).</summary>
/// <param name="Name">The exact NDI name, or <c>null</c>.</param>
/// <param name="Url">The address (<c>host</c> or <c>host:port</c>), or <c>null</c>.</param>
public sealed record NdiSourceRef(string? Name, string? Url)
{
    /// <summary>A short text for logs.</summary>
    public override string ToString() => Name ?? Url ?? "(nada)";
}

/// <summary>
/// One video frame, valid only during the call that receives it (it may point into native memory): copy what you need. Pixels are BGRA
/// (BGRX when the source has no alpha), top row first, <see cref="Stride"/> bytes per row.
/// </summary>
public readonly ref struct NdiFrameView
{
    /// <summary>Builds a view.</summary>
    public NdiFrameView(int width, int height, int stride, ReadOnlySpan<byte> data, long timestampTicks)
    {
        Width = width;
        Height = height;
        Stride = stride;
        Data = data;
        TimestampTicks = timestampTicks;
    }

    /// <summary>Width in pixels.</summary>
    public int Width { get; }

    /// <summary>Height in pixels.</summary>
    public int Height { get; }

    /// <summary>Bytes per row.</summary>
    public int Stride { get; }

    /// <summary>The pixels (<c>Stride * Height</c> bytes).</summary>
    public ReadOnlySpan<byte> Data { get; }

    /// <summary>When it arrived (a steady clock, <see cref="TimeProvider.GetTimestamp"/> ticks).</summary>
    public long TimestampTicks { get; }
}

/// <summary>Receives a frame. Called on the capture thread: it must be quick and must not keep the view.</summary>
public delegate void NdiFrameHandler(in NdiFrameView frame);

/// <summary>What a native capture returned.</summary>
public enum NdiCaptureKind
{
    /// <summary>Nothing within the timeout.</summary>
    None,

    /// <summary>A video frame (to be released with <see cref="INdiNativeReceiver.Release"/>).</summary>
    Video,

    /// <summary>Something else (audio, metadata, status change): ignored.</summary>
    Other,

    /// <summary>The connection failed hard: the receiver should be recreated.</summary>
    Error,
}

/// <summary>The NDI runtime as the app uses it. The real one loads the DLL dynamically; tests use fakes.</summary>
public interface INdiRuntime : IDisposable
{
    /// <summary>Whether the runtime was found and initialized.</summary>
    bool IsAvailable { get; }

    /// <summary>The runtime version text (<c>NDI SDK WIN64 ...</c>), or empty.</summary>
    string Version { get; }

    /// <summary>Why it is not available (with the download link), or <c>null</c> when it is.</summary>
    string? UnavailableReason { get; }

    /// <summary>Where the DLL was loaded from, or <c>null</c>.</summary>
    string? LoadedFrom { get; }

    /// <summary>Creates a finder that also asks <paramref name="extraIps"/>. Throws when the runtime is not available.</summary>
    INdiFinder CreateFinder(IReadOnlyList<string> extraIps);

    /// <summary>Creates a video-only receiver at the highest bandwidth, BGRX/BGRA, connected to <paramref name="source"/>. Throws when it cannot.</summary>
    INdiNativeReceiver CreateReceiver(NdiSourceRef source, string receiverName);
}

/// <summary>A running finder.</summary>
public interface INdiFinder : IDisposable
{
    /// <summary>Waits until the list changes or <paramref name="timeoutMs"/> passes.</summary>
    bool WaitForChange(int timeoutMs);

    /// <summary>The sources known now.</summary>
    IReadOnlyList<NdiSourceInfo> GetSources();
}

/// <summary>One native receiver connection, used from one capture thread.</summary>
public interface INdiNativeReceiver : IDisposable
{
    /// <summary>Waits up to <paramref name="timeoutMs"/> for a frame. For <see cref="NdiCaptureKind.Video"/> the frame is valid until <see cref="Release"/>.</summary>
    NdiCaptureKind Capture(int timeoutMs, out NdiFrameHandle frame);

    /// <summary>Gives the frame back to the runtime. Must be called for every <see cref="NdiCaptureKind.Video"/>.</summary>
    void Release(in NdiFrameHandle frame);

    /// <summary>Builds the view of a captured frame.</summary>
    NdiFrameView View(in NdiFrameHandle frame, long timestampTicks);

    /// <summary>How many senders this receiver is connected to (0 while it searches).</summary>
    int ConnectionCount { get; }

    /// <summary>Frames the runtime itself dropped (receiver too slow).</summary>
    long DroppedByRuntime { get; }
}

/// <summary>Opaque description of a captured native frame.</summary>
public readonly struct NdiFrameHandle
{
    /// <summary>Builds the handle.</summary>
    public NdiFrameHandle(IntPtr data, int width, int height, int stride, int fourCc, object? token = null)
    {
        Data = data;
        Width = width;
        Height = height;
        Stride = stride;
        FourCc = fourCc;
        Token = token;
    }

    /// <summary>Pointer to the pixels.</summary>
    public IntPtr Data { get; }

    /// <summary>Width.</summary>
    public int Width { get; }

    /// <summary>Height.</summary>
    public int Height { get; }

    /// <summary>Bytes per row.</summary>
    public int Stride { get; }

    /// <summary>The pixel format code (BGRA/BGRX expected).</summary>
    public int FourCc { get; }

    /// <summary>Anything the implementation needs to free it (fakes).</summary>
    public object? Token { get; }
}

/// <summary>Where one receiver is in its life.</summary>
public enum NdiConnectionState
{
    /// <summary>No source assigned.</summary>
    Idle,

    /// <summary>Creating the connection or waiting for the sender.</summary>
    Connecting,

    /// <summary>Frames are arriving.</summary>
    Connected,

    /// <summary>Frames stopped (the sender went away or the network dropped); retrying with backoff.</summary>
    Lost,

    /// <summary>The runtime is missing or refuses; nothing will happen until it changes.</summary>
    Failed,
}

/// <summary>A managed receiver the switcher drives (the real one is <see cref="NdiReceiver"/>; tests use fakes).</summary>
public interface ICameraReceiver : IDisposable
{
    /// <summary>Raised on the capture thread for every frame.</summary>
    event NdiFrameHandler? Frame;

    /// <summary>Raised (any thread) when <see cref="State"/> changes.</summary>
    event Action? StateChanged;

    /// <summary>The state now.</summary>
    NdiConnectionState State { get; }

    /// <summary>The source it is on, or <c>null</c>.</summary>
    NdiSourceRef? Source { get; }

    /// <summary>Whether a frame arrived since the last <see cref="Connect"/> (what "first frame" means for a Take).</summary>
    bool HasFrameSinceConnect { get; }

    /// <summary>The clock timestamp of the last frame, or 0 when none ever came.</summary>
    long LastFrameTimestamp { get; }

    /// <summary>Frames received since created.</summary>
    long FramesReceived { get; }

    /// <summary>Frames dropped (by the runtime, or by a slow handler).</summary>
    long FramesDropped { get; }

    /// <summary>Width of the last frame, or 0.</summary>
    int Width { get; }

    /// <summary>Height of the last frame, or 0.</summary>
    int Height { get; }

    /// <summary>The measured frames per second over the last second or so.</summary>
    double Fps { get; }

    /// <summary>The last error text, or <c>null</c>.</summary>
    string? LastError { get; }

    /// <summary>Connects to <paramref name="source"/> (or disconnects with <c>null</c>). Returns at once; the work is on the capture thread. The last picture is not cleared.</summary>
    void Connect(NdiSourceRef? source);
}
