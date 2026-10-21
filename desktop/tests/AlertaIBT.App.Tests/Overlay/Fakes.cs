using AlertaIBT.App.Overlay;
using AlertaIBT.Core.Overlay;
using AlertaIBT.Core.Screens;
using AlertaIBT.Core.Settings;

namespace AlertaIBT.App.Tests.Overlay;

internal sealed class FakeSurface : IBarSurface
{
    public bool CaptureExcluded { get; set; } = true;

    public event Action? CaptureExcludedChanged;

    public double TextWidthPx { get; set; } = 1000;

    public List<(string Text, BarSettings Bar, ScreenInfo Screen)> Prepared { get; } = [];

    public List<BarFrame> Frames { get; } = [];

    public int HideCount { get; private set; }

    public bool Disposed { get; private set; }

    public Exception? ThrowOnPrepare { get; set; }

    public Exception? ThrowOnApply { get; set; }

    public PreparedBar Prepare(string text, BarSettings bar, ScreenInfo screen)
    {
        if (ThrowOnPrepare is not null)
        {
            throw ThrowOnPrepare;
        }

        Prepared.Add((text, bar, screen));
        return new PreparedBar(TextWidthPx);
    }

    public void Apply(BarFrame frame)
    {
        if (ThrowOnApply is not null)
        {
            throw ThrowOnApply;
        }

        Frames.Add(frame);
    }

    public void Hide() => HideCount++;

    public void Dispose() => Disposed = true;

    public void ChangeCapture(bool excluded)
    {
        CaptureExcluded = excluded;
        CaptureExcludedChanged?.Invoke();
    }
}

internal sealed class FakeFrames : IFrameSource
{
    public event Action<TimeSpan>? Frame;

    public bool Running { get; private set; }

    public int StartCount { get; private set; }

    public void Start()
    {
        Running = true;
        StartCount++;
    }

    public void Stop() => Running = false;

    public void Push(TimeSpan time) => Frame?.Invoke(time);
}

internal sealed class FakeLed(ScreenInfo? led) : ILedSource
{
    public ScreenInfo? Led { get; set; } = led;

    public event Action<ScreenInfo?>? LedChanged;

    public void Raise(ScreenInfo? now)
    {
        Led = now;
        LedChanged?.Invoke(now);
    }
}

// Queues what is posted so a test decides when it runs; work sent with InvokeAsync runs at once, like on the UI thread.
internal sealed class QueueUi : IUiDispatcher
{
    private readonly Queue<Action> _posted = new();

    public Task<T> InvokeAsync<T>(Func<Task<T>> work) => work();

    public void Post(Action work) => _posted.Enqueue(work);

    public void Drain()
    {
        while (_posted.TryDequeue(out var work))
        {
            work();
        }
    }
}
