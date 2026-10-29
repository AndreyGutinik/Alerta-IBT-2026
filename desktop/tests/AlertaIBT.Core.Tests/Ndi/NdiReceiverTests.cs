using AlertaIBT.Core.Ndi;

namespace AlertaIBT.Core.Tests.Ndi;

/// <summary>A runtime whose receivers deliver scripted captures.</summary>
internal sealed class FakeNdiRuntime : INdiRuntime
{
    public Queue<Func<FakeNativeReceiver>> Plans { get; } = new();

    public List<(NdiSourceRef Source, string Name)> Created { get; } = [];

    public bool IsAvailable { get; set; } = true;

    public string Version => "fake";

    public string? UnavailableReason => IsAvailable ? null : NdiConstants.RuntimeMissingText;

    public string? LoadedFrom => null;

    public List<FakeNativeReceiver> Receivers { get; } = [];

    public INdiFinder CreateFinder(IReadOnlyList<string> extraIps) => throw new NotSupportedException();

    public INdiNativeReceiver CreateReceiver(NdiSourceRef source, string receiverName)
    {
        lock (Created)
        {
            Created.Add((source, receiverName));
        }

        if (Plans.Count == 0)
        {
            throw new InvalidOperationException("sem plano");
        }

        var receiver = Plans.Dequeue()();
        Receivers.Add(receiver);
        return receiver;
    }

    public void Dispose()
    {
    }
}

internal sealed class FakeNativeReceiver : INdiNativeReceiver
{
    private int _released;
    private int _disposed;

    /// <summary>Each call returns what the script says; after the script ends it returns None (after a short sleep).</summary>
    public Queue<NdiCaptureKind> Script { get; } = new();

    public int Width { get; init; } = 16;

    public int Height { get; init; } = 9;

    public int FourCc { get; init; } = 0x41524742;

    public int Released => Volatile.Read(ref _released);

    public bool IsDisposed => Volatile.Read(ref _disposed) == 1;

    public int ConnectionCount => 1;

    public long DroppedByRuntime { get; set; }

    public NdiCaptureKind Capture(int timeoutMs, out NdiFrameHandle frame)
    {
        NdiCaptureKind kind;
        lock (Script)
        {
            kind = Script.Count > 0 ? Script.Dequeue() : NdiCaptureKind.None;
        }

        if (kind == NdiCaptureKind.None)
        {
            Thread.Sleep(Math.Min(timeoutMs, 5));
        }

        frame = new NdiFrameHandle(IntPtr.Zero, Width, Height, Width * 4, FourCc, new byte[Width * Height * 4]);
        return kind;
    }

    public void Release(in NdiFrameHandle frame) => Interlocked.Increment(ref _released);

    public NdiFrameView View(in NdiFrameHandle frame, long timestampTicks) =>
        new(frame.Width, frame.Height, frame.Stride, (byte[])frame.Token!, timestampTicks);

    public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
}

public sealed class NdiReceiverTests
{
    private static readonly NdiReceiverOptions Fast = new(
        5,
        TimeSpan.FromMilliseconds(60),
        TimeSpan.FromMilliseconds(250),
        [TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(40)]);

    private static bool WaitFor(Func<bool> condition, int ms = 3000)
    {
        var end = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < end)
        {
            if (condition())
            {
                return true;
            }

            Thread.Sleep(5);
        }

        return condition();
    }

    private static FakeNativeReceiver Frames(int count)
    {
        var native = new FakeNativeReceiver();
        for (var i = 0; i < count; i++)
        {
            native.Script.Enqueue(NdiCaptureKind.Video);
        }

        return native;
    }

    [Fact]
    public void Delivers_frames_frees_each_one_and_counts_them()
    {
        var runtime = new FakeNdiRuntime();
        var native = Frames(10);
        runtime.Plans.Enqueue(() => native);
        using var receiver = new NdiReceiver(runtime, "t", options: Fast);
        var seen = 0;
        receiver.Frame += (in NdiFrameView frame) =>
        {
            Assert.Equal(16, frame.Width);
            Assert.Equal(9, frame.Height);
            Interlocked.Increment(ref seen);
        };

        receiver.Connect(new NdiSourceRef("PC (Cam)", null));

        Assert.True(WaitFor(() => receiver.FramesReceived == 10));
        Assert.True(WaitFor(() => native.Released == 10));
        Assert.Equal(10, seen);
        Assert.True(receiver.HasFrameSinceConnect);
        Assert.NotEqual(0, receiver.LastFrameTimestamp);
        Assert.Equal(16, receiver.Width);
    }

    [Fact]
    public void A_frame_is_freed_even_when_the_handler_throws()
    {
        var runtime = new FakeNdiRuntime();
        var native = Frames(3);
        runtime.Plans.Enqueue(() => native);
        using var receiver = new NdiReceiver(runtime, "t", options: Fast);
        receiver.Frame += (in NdiFrameView _) => throw new InvalidOperationException("boom");

        receiver.Connect(new NdiSourceRef(null, "10.0.0.5"));

        Assert.True(WaitFor(() => native.Released == 3));
        Assert.Equal(3, receiver.FramesDropped);
    }

    [Fact]
    public void Connects_by_name_and_by_direct_address()
    {
        var runtime = new FakeNdiRuntime();
        runtime.Plans.Enqueue(() => Frames(1));
        runtime.Plans.Enqueue(() => Frames(1));
        using var receiver = new NdiReceiver(runtime, "rx", options: Fast);

        receiver.Connect(new NdiSourceRef("PC (Cam)", null));
        Assert.True(WaitFor(() => receiver.FramesReceived == 1));
        receiver.Connect(new NdiSourceRef(null, "192.168.1.50:5961"));
        Assert.True(WaitFor(() => receiver.FramesReceived == 2));

        Assert.Equal(new NdiSourceRef("PC (Cam)", null), runtime.Created[0].Source);
        Assert.Equal(new NdiSourceRef(null, "192.168.1.50:5961"), runtime.Created[1].Source);
        Assert.Equal("rx", runtime.Created[0].Name);
        Assert.True(runtime.Receivers[0].IsDisposed); // the old connection was closed
    }

    [Fact]
    public void Connect_resets_the_first_frame_flag()
    {
        var runtime = new FakeNdiRuntime();
        runtime.Plans.Enqueue(() => Frames(1));
        runtime.Plans.Enqueue(() => new FakeNativeReceiver());
        using var receiver = new NdiReceiver(runtime, "t", options: Fast);
        receiver.Connect(new NdiSourceRef("a", null));
        Assert.True(WaitFor(() => receiver.HasFrameSinceConnect));

        receiver.Connect(new NdiSourceRef("b", null));

        Assert.False(receiver.HasFrameSinceConnect);
    }

    [Fact]
    public void A_failed_creation_retries_with_backoff_until_it_works()
    {
        var runtime = new FakeNdiRuntime();
        runtime.Plans.Enqueue(() => throw new InvalidOperationException("sem rede"));
        runtime.Plans.Enqueue(() => throw new InvalidOperationException("sem rede"));
        runtime.Plans.Enqueue(() => Frames(2));
        using var receiver = new NdiReceiver(runtime, "t", options: Fast);

        receiver.Connect(new NdiSourceRef("a", null));

        Assert.True(WaitFor(() => receiver.FramesReceived == 2));
        Assert.Equal(3, runtime.Created.Count);
        Assert.Equal(NdiConnectionState.Connected, receiver.State);
    }

    [Fact]
    public void Silence_turns_the_state_to_lost_then_recreates_the_connection()
    {
        var runtime = new FakeNdiRuntime();
        runtime.Plans.Enqueue(() => Frames(2)); // two frames then silence
        runtime.Plans.Enqueue(() => Frames(1)); // the recreated connection delivers again
        using var receiver = new NdiReceiver(runtime, "t", options: Fast);

        receiver.Connect(new NdiSourceRef("a", null));

        Assert.True(WaitFor(() => receiver.State == NdiConnectionState.Lost));
        Assert.True(WaitFor(() => receiver.FramesReceived == 3));
        Assert.Equal(2, runtime.Created.Count);
        Assert.True(runtime.Receivers[0].IsDisposed);
    }

    [Fact]
    public void A_capture_error_recreates_the_connection()
    {
        var runtime = new FakeNdiRuntime();
        var broken = new FakeNativeReceiver();
        broken.Script.Enqueue(NdiCaptureKind.Error);
        runtime.Plans.Enqueue(() => broken);
        runtime.Plans.Enqueue(() => Frames(1));
        using var receiver = new NdiReceiver(runtime, "t", options: Fast);

        receiver.Connect(new NdiSourceRef("a", null));

        Assert.True(WaitFor(() => receiver.FramesReceived == 1));
        Assert.True(broken.IsDisposed);
    }

    [Fact]
    public void A_missing_runtime_is_the_failed_state_with_the_reason()
    {
        var runtime = new FakeNdiRuntime { IsAvailable = false };
        using var receiver = new NdiReceiver(runtime, "t", options: Fast);

        receiver.Connect(new NdiSourceRef("a", null));

        Assert.True(WaitFor(() => receiver.State == NdiConnectionState.Failed));
        Assert.NotNull(receiver.LastError);
    }

    [Fact]
    public void Other_frame_types_and_unknown_pixel_formats_are_not_delivered()
    {
        var runtime = new FakeNdiRuntime();
        var native = new FakeNativeReceiver { FourCc = 0x59565955 }; // UYVY: not what we asked for
        native.Script.Enqueue(NdiCaptureKind.Other);
        native.Script.Enqueue(NdiCaptureKind.Video);
        runtime.Plans.Enqueue(() => native);
        using var receiver = new NdiReceiver(runtime, "t", options: Fast);
        var seen = 0;
        receiver.Frame += (in NdiFrameView _) => Interlocked.Increment(ref seen);

        receiver.Connect(new NdiSourceRef("a", null));

        Assert.True(WaitFor(() => native.Released == 1));
        Assert.Equal(0, seen);
    }

    [Fact]
    public void Connecting_to_null_goes_idle_and_dispose_stops_the_thread()
    {
        var runtime = new FakeNdiRuntime();
        var native = Frames(1);
        runtime.Plans.Enqueue(() => native);
        var receiver = new NdiReceiver(runtime, "t", options: Fast);
        receiver.Connect(new NdiSourceRef("a", null));
        Assert.True(WaitFor(() => receiver.FramesReceived == 1));

        receiver.Connect(null);
        Assert.Equal(NdiConnectionState.Idle, receiver.State);
        Assert.True(WaitFor(() => native.IsDisposed));

        receiver.Dispose();
        Assert.Throws<ObjectDisposedException>(() => receiver.Connect(new NdiSourceRef("a", null)));
    }
}
