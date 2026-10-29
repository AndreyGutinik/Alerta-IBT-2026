using AlertaIBT.Core.Ndi;
using AlertaIBT.Core.Settings;
using Microsoft.Extensions.Time.Testing;

namespace AlertaIBT.Core.Tests.Ndi;

/// <summary>A receiver the tests drive by hand: <see cref="Push"/> delivers a frame as if the capture thread did.</summary>
internal sealed class FakeCameraReceiver(FakeTimeProvider clock) : ICameraReceiver
{
    private int _state;
    private long _last;
    private long _received;

    public event NdiFrameHandler? Frame;

    public event Action? StateChanged;

    public List<NdiSourceRef?> Connects { get; } = [];

    public NdiConnectionState State => (NdiConnectionState)_state;

    public NdiSourceRef? Source { get; private set; }

    public bool HasFrameSinceConnect { get; private set; }

    public long LastFrameTimestamp => _last;

    public long FramesReceived => _received;

    public long FramesDropped => 0;

    public int Width { get; private set; }

    public int Height { get; private set; }

    public double Fps => 60;

    public string? LastError => null;

    public bool Disposed { get; private set; }

    public void Connect(NdiSourceRef? source)
    {
        Connects.Add(source);
        Source = source;
        HasFrameSinceConnect = false;
        _state = (int)(source is null ? NdiConnectionState.Idle : NdiConnectionState.Connecting);
        StateChanged?.Invoke();
    }

    /// <summary>Delivers one frame of the given size (filled with <paramref name="shade"/>).</summary>
    public void Push(int width = 64, int height = 36, byte shade = 128)
    {
        var data = new byte[width * height * 4];
        Array.Fill(data, shade);
        var now = clock.GetTimestamp();
        _last = now;
        _received++;
        Width = width;
        Height = height;
        HasFrameSinceConnect = true;
        _state = (int)NdiConnectionState.Connected;
        var view = new NdiFrameView(width, height, width * 4, data, now);
        Frame?.Invoke(view);
    }

    /// <summary>Delivers a prepared frame without allocating (the benchmark).</summary>
    public void Raise(in NdiFrameView view)
    {
        _last = clock.GetTimestamp();
        _received++;
        HasFrameSinceConnect = true;
        Frame?.Invoke(view);
    }

    public void Dispose() => Disposed = true;
}

internal static class NdiTestSettings
{
    /// <summary>Settings with the given source in slots 0..n-1 (names Cam1.., sources "PC (CamN)").</summary>
    public static NdiSettings With(int cameras = 3) => new()
    {
        Cameras = Enumerable.Range(1, NdiSettings.SlotCount)
            .Select(n => new NdiCameraSlot($"Cam{n}", n <= cameras ? $"PC (Cam{n})" : string.Empty, n * 10))
            .ToList(),
    };
}
