using System.IO;
using System.Runtime.InteropServices;
using AlertaIBT.Core.Ndi;
using Microsoft.Extensions.Logging;

namespace AlertaIBT.App.Ndi;

/// <summary>
/// The NDI runtime, loaded dynamically at run time from the folder NDI Tools installed (<c>NDI_RUNTIME_DIR_V6</c>, then V5, then the default
/// folders): nothing of NDI is shipped with this app. Only the free standard SDK API is used, declared here by hand from the public documentation
/// (docs.ndi.video): initialize/destroy/version, the finder (<c>NDIlib_find_*</c>) and the receiver (<c>NDIlib_recv_*</c>, video only). This
/// class never sends. When the runtime is missing, <see cref="IsAvailable"/> is false and <see cref="UnavailableReason"/> carries the official
/// download link.
/// </summary>
public sealed unsafe class NdiLibrary : INdiRuntime
{
    private const int FourCcBgra = 0x41524742;
    private const int ColorFormatBgrxBgra = 0;
    private const int BandwidthHighest = 100;
    private const int FrameVideo = 1;
    private const int FrameError = 4;

    private readonly ILogger? _logger;
    private IntPtr _handle;
    private bool _initialized;
    private bool _disposed;

    private delegate* unmanaged[Cdecl]<byte> _initialize;
    private delegate* unmanaged[Cdecl]<void> _destroy;
    private delegate* unmanaged[Cdecl]<byte*> _version;
    private delegate* unmanaged[Cdecl]<FindCreate*, IntPtr> _findCreate;
    private delegate* unmanaged[Cdecl]<IntPtr, void> _findDestroy;
    private delegate* unmanaged[Cdecl]<IntPtr, uint, byte> _findWait;
    private delegate* unmanaged[Cdecl]<IntPtr, uint*, Source*> _findSources;
    private delegate* unmanaged[Cdecl]<RecvCreateV3*, IntPtr> _recvCreate;
    private delegate* unmanaged[Cdecl]<IntPtr, void> _recvDestroy;
    private delegate* unmanaged[Cdecl]<IntPtr, VideoFrameV2*, void*, void*, uint, int> _recvCapture;
    private delegate* unmanaged[Cdecl]<IntPtr, VideoFrameV2*, void> _recvFreeVideo;
    private delegate* unmanaged[Cdecl]<IntPtr, int> _recvConnections;
    private delegate* unmanaged[Cdecl]<IntPtr, RecvPerformance*, RecvPerformance*, void> _recvPerformance;

    private NdiLibrary(ILogger? logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public bool IsAvailable => _initialized && !_disposed;

    /// <inheritdoc />
    public string Version { get; private set; } = string.Empty;

    /// <inheritdoc />
    public string? UnavailableReason { get; private set; }

    /// <inheritdoc />
    public string? LoadedFrom { get; private set; }

    /// <summary>Loads the runtime found by <see cref="NdiRuntimeLocator"/>; never throws (the result says why it is unavailable).</summary>
    public static NdiLibrary Load(ILogger? logger = null) =>
        Load(Environment.GetEnvironmentVariable, File.Exists, logger);

    /// <summary>Loads the runtime with an injectable environment and file check (tests).</summary>
    public static NdiLibrary Load(Func<string, string?> environment, Func<string, bool> fileExists, ILogger? logger = null)
    {
        var library = new NdiLibrary(logger);
        var path = NdiRuntimeLocator.FindDll(environment, fileExists);
        if (path is null)
        {
            library.UnavailableReason = NdiConstants.RuntimeMissingText;
            logger?.LogInformation("Runtime do NDI nao encontrado (NDI_RUNTIME_DIR_V6 e as pastas padrao)");
            return library;
        }

        try
        {
            library._handle = NativeLibrary.Load(path);
            library.Bind();
            if (library._initialize() == 0)
            {
                library.UnavailableReason = "Este processador n\U000000E3o \U000000E9 compat\U000000EDvel com o NDI.";
                library.Free();
                return library;
            }

            library._initialized = true;
            library.LoadedFrom = path;
            var version = library._version();
            library.Version = version == null ? "NDI" : Marshal.PtrToStringUTF8((IntPtr)version) ?? "NDI";
            logger?.LogInformation("Runtime do NDI carregado de {Path}: {Version}", path, library.Version);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or InvalidOperationException)
        {
            library.UnavailableReason = $"N\U000000E3o foi poss\U000000EDvel carregar o runtime do NDI ({ex.Message}). Instale ou atualize o NDI Tools: {NdiConstants.DownloadUrl}";
            logger?.LogWarning(ex, "Falha ao carregar o runtime do NDI de {Path}", path);
            library.Free();
        }

        return library;
    }

    /// <summary>The address of a function exported by the loaded runtime, or zero (used only by tests, to create a test sender).</summary>
    public IntPtr GetExport(string name) =>
        _handle != IntPtr.Zero && NativeLibrary.TryGetExport(_handle, name, out var address) ? address : IntPtr.Zero;

    /// <inheritdoc />
    public INdiFinder CreateFinder(IReadOnlyList<string> extraIps)
    {
        ThrowIfUnavailable();

        var extra = string.Join(',', extraIps.Where(ip => !string.IsNullOrWhiteSpace(ip)).Select(ip => ip.Trim()));
        var extraPtr = extra.Length == 0 ? IntPtr.Zero : Marshal.StringToCoTaskMemUTF8(extra);
        try
        {
            var settings = new FindCreate { ShowLocalSources = 1, Groups = null, ExtraIps = (byte*)extraPtr };
            var instance = _findCreate(&settings);
            if (instance == IntPtr.Zero)
            {
                throw new InvalidOperationException("NDIlib_find_create_v2 falhou");
            }

            return new Finder(this, instance);
        }
        finally
        {
            Marshal.FreeCoTaskMem(extraPtr);
        }
    }

    /// <inheritdoc />
    public INdiNativeReceiver CreateReceiver(NdiSourceRef source, string receiverName)
    {
        ThrowIfUnavailable();

        var namePtr = source.Name is null ? IntPtr.Zero : Marshal.StringToCoTaskMemUTF8(source.Name);
        var urlPtr = source.Url is null ? IntPtr.Zero : Marshal.StringToCoTaskMemUTF8(source.Url);
        var recvNamePtr = Marshal.StringToCoTaskMemUTF8(receiverName);
        try
        {
            var settings = new RecvCreateV3
            {
                Source = new Source { Name = (byte*)namePtr, Url = (byte*)urlPtr },
                ColorFormat = ColorFormatBgrxBgra,
                Bandwidth = BandwidthHighest,
                AllowVideoFields = 0,
                ReceiverName = (byte*)recvNamePtr,
            };
            var instance = _recvCreate(&settings);
            if (instance == IntPtr.Zero)
            {
                throw new InvalidOperationException("NDIlib_recv_create_v3 falhou");
            }

            return new Receiver(this, instance);
        }
        finally
        {
            Marshal.FreeCoTaskMem(namePtr);
            Marshal.FreeCoTaskMem(urlPtr);
            Marshal.FreeCoTaskMem(recvNamePtr);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_initialized)
        {
            _destroy();
            _initialized = false;
        }

        Free();
    }

    private void ThrowIfUnavailable()
    {
        if (!IsAvailable)
        {
            throw new InvalidOperationException(UnavailableReason ?? NdiConstants.RuntimeMissingText);
        }
    }

    private void Free()
    {
        if (_handle != IntPtr.Zero)
        {
            NativeLibrary.Free(_handle);
            _handle = IntPtr.Zero;
        }
    }

    private void Bind()
    {
        _initialize = (delegate* unmanaged[Cdecl]<byte>)Export("NDIlib_initialize");
        _destroy = (delegate* unmanaged[Cdecl]<void>)Export("NDIlib_destroy");
        _version = (delegate* unmanaged[Cdecl]<byte*>)Export("NDIlib_version");
        _findCreate = (delegate* unmanaged[Cdecl]<FindCreate*, IntPtr>)Export("NDIlib_find_create_v2");
        _findDestroy = (delegate* unmanaged[Cdecl]<IntPtr, void>)Export("NDIlib_find_destroy");
        _findWait = (delegate* unmanaged[Cdecl]<IntPtr, uint, byte>)Export("NDIlib_find_wait_for_sources");
        _findSources = (delegate* unmanaged[Cdecl]<IntPtr, uint*, Source*>)Export("NDIlib_find_get_current_sources");
        _recvCreate = (delegate* unmanaged[Cdecl]<RecvCreateV3*, IntPtr>)Export("NDIlib_recv_create_v3");
        _recvDestroy = (delegate* unmanaged[Cdecl]<IntPtr, void>)Export("NDIlib_recv_destroy");
        _recvCapture = (delegate* unmanaged[Cdecl]<IntPtr, VideoFrameV2*, void*, void*, uint, int>)Export("NDIlib_recv_capture_v2");
        _recvFreeVideo = (delegate* unmanaged[Cdecl]<IntPtr, VideoFrameV2*, void>)Export("NDIlib_recv_free_video_v2");
        _recvConnections = (delegate* unmanaged[Cdecl]<IntPtr, int>)Export("NDIlib_recv_get_no_connections");
        _recvPerformance = (delegate* unmanaged[Cdecl]<IntPtr, RecvPerformance*, RecvPerformance*, void>)Export("NDIlib_recv_get_performance");
    }

    private void* Export(string name) => (void*)NativeLibrary.GetExport(_handle, name);

    // The structures of the public SDK (NDIlib_source_t, NDIlib_find_create_t, NDIlib_recv_create_v3_t, NDIlib_video_frame_v2_t, NDIlib_recv_performance_t).
    [StructLayout(LayoutKind.Sequential)]
    private struct Source
    {
        public byte* Name;
        public byte* Url;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FindCreate
    {
        public byte ShowLocalSources;
        public byte* Groups;
        public byte* ExtraIps;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RecvCreateV3
    {
        public Source Source;
        public int ColorFormat;
        public int Bandwidth;
        public byte AllowVideoFields;
        public byte* ReceiverName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VideoFrameV2
    {
        public int Xres;
        public int Yres;
        public int FourCc;
        public int FrameRateN;
        public int FrameRateD;
        public float PictureAspectRatio;
        public int FrameFormatType;
        public long Timecode;
        public byte* Data;
        public int LineStrideInBytes;
        public byte* Metadata;
        public long Timestamp;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RecvPerformance
    {
        public long VideoFrames;
        public long AudioFrames;
        public long MetadataFrames;
    }

    private sealed class Finder(NdiLibrary library, IntPtr instance) : INdiFinder
    {
        private IntPtr _instance = instance;

        public bool WaitForChange(int timeoutMs) => _instance != IntPtr.Zero && library._findWait(_instance, (uint)Math.Max(0, timeoutMs)) != 0;

        public IReadOnlyList<NdiSourceInfo> GetSources()
        {
            if (_instance == IntPtr.Zero)
            {
                return [];
            }

            uint count = 0;
            var sources = library._findSources(_instance, &count);
            var list = new List<NdiSourceInfo>((int)count);
            for (var i = 0; i < count && sources != null; i++)
            {
                var name = Marshal.PtrToStringUTF8((IntPtr)sources[i].Name) ?? string.Empty;
                var url = Marshal.PtrToStringUTF8((IntPtr)sources[i].Url) ?? string.Empty;
                list.Add(new NdiSourceInfo(name, url));
            }

            return list;
        }

        public void Dispose()
        {
            if (_instance != IntPtr.Zero)
            {
                library._findDestroy(_instance);
                _instance = IntPtr.Zero;
            }
        }
    }

    private sealed class Receiver : INdiNativeReceiver
    {
        private readonly NdiLibrary _library;
        private readonly VideoFrameV2* _frame;
        private IntPtr _instance;

        public Receiver(NdiLibrary library, IntPtr instance)
        {
            _library = library;
            _instance = instance;
            _frame = (VideoFrameV2*)NativeMemory.AllocZeroed((nuint)sizeof(VideoFrameV2));
        }

        public int ConnectionCount => _instance == IntPtr.Zero ? 0 : _library._recvConnections(_instance);

        public long DroppedByRuntime
        {
            get
            {
                if (_instance == IntPtr.Zero)
                {
                    return 0;
                }

                RecvPerformance total = default;
                RecvPerformance dropped = default;
                _library._recvPerformance(_instance, &total, &dropped);
                return dropped.VideoFrames;
            }
        }

        public NdiCaptureKind Capture(int timeoutMs, out NdiFrameHandle frame)
        {
            frame = default;
            if (_instance == IntPtr.Zero)
            {
                return NdiCaptureKind.Error;
            }

            // Video only: the audio and metadata pointers are null, so the runtime throws those away.
            var kind = _library._recvCapture(_instance, _frame, null, null, (uint)Math.Max(0, timeoutMs));
            switch (kind)
            {
                case FrameVideo:
                    frame = new NdiFrameHandle((IntPtr)_frame->Data, _frame->Xres, _frame->Yres, _frame->LineStrideInBytes, _frame->FourCc);
                    return NdiCaptureKind.Video;
                case FrameError:
                    return NdiCaptureKind.Error;
                case 0:
                    return NdiCaptureKind.None;
                default:
                    return NdiCaptureKind.Other;
            }
        }

        public void Release(in NdiFrameHandle frame)
        {
            if (_instance != IntPtr.Zero)
            {
                _library._recvFreeVideo(_instance, _frame);
            }
        }

        public NdiFrameView View(in NdiFrameHandle frame, long timestampTicks) =>
            new(frame.Width, frame.Height, frame.Stride, new ReadOnlySpan<byte>((void*)frame.Data, frame.Stride * frame.Height), timestampTicks);

        public void Dispose()
        {
            if (_instance != IntPtr.Zero)
            {
                _library._recvDestroy(_instance);
                _instance = IntPtr.Zero;
                NativeMemory.Free(_frame);
            }
        }
    }
}
