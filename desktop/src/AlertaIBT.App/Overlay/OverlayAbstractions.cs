using System.Windows.Media;
using System.Windows.Threading;
using AlertaIBT.Core.Overlay;
using AlertaIBT.Core.Screens;
using AlertaIBT.Core.Settings;

namespace AlertaIBT.App.Overlay;

/// <summary>What the controller needs to know about the text once the surface has laid it out.</summary>
/// <param name="TextWidthPx">The width of the moving text element in physical pixels, margins included.</param>
public readonly record struct PreparedBar(double TextWidthPx);

/// <summary>
/// The thing the bar is drawn on: in the app a transparent WPF window over the LED. The controller drives it frame by
/// frame; a fake stands in for it in tests.
/// </summary>
public interface IBarSurface : IDisposable
{
    /// <summary>Whether the window is excluded from screen capture (OBS, NDI, screenshots). False if Windows refused.</summary>
    bool CaptureExcluded { get; }

    /// <summary>Raised when <see cref="CaptureExcluded"/> changes.</summary>
    event Action? CaptureExcludedChanged;

    /// <summary>
    /// Puts the (still invisible) bar on <paramref name="screen"/> with the look of <paramref name="bar"/>, lays the text out
    /// once and shows the window with the bar hidden, ready for the first frame.
    /// </summary>
    PreparedBar Prepare(string text, BarSettings bar, ScreenInfo screen);

    /// <summary>Draws one frame of the pass: the slide of the bar, its opacity and the position of the text.</summary>
    void Apply(BarFrame frame);

    /// <summary>Hides the window at once.</summary>
    void Hide();
}

/// <summary>A clock that ticks once per rendered frame, with the frame's own time.</summary>
public interface IFrameSource
{
    /// <summary>Raised on the UI thread for each rendered frame with that frame's render time (a steady clock).</summary>
    event Action<TimeSpan>? Frame;

    /// <summary>Starts ticking.</summary>
    void Start();

    /// <summary>Stops ticking.</summary>
    void Stop();
}

/// <summary>Where the LED is, as far as the app knows.</summary>
public interface ILedSource
{
    /// <summary>The LED screen now, or null when it is off.</summary>
    ScreenInfo? Led { get; }

    /// <summary>Raised when the LED appears, vanishes, changes resolution or moves. May be raised on any thread.</summary>
    event Action<ScreenInfo?>? LedChanged;
}

/// <summary>Runs work on the UI thread.</summary>
public interface IUiDispatcher
{
    /// <summary>Runs <paramref name="work"/> on the UI thread (now when already there) and returns its result.</summary>
    Task<T> InvokeAsync<T>(Func<Task<T>> work);

    /// <summary>Queues <paramref name="work"/> to run on the UI thread.</summary>
    void Post(Action work);
}

/// <summary>The WPF dispatcher as an <see cref="IUiDispatcher"/>.</summary>
public sealed class WpfUiDispatcher(Dispatcher dispatcher) : IUiDispatcher
{
    /// <inheritdoc />
    public Task<T> InvokeAsync<T>(Func<Task<T>> work)
    {
        ArgumentNullException.ThrowIfNull(work);

        return dispatcher.CheckAccess() ? work() : dispatcher.InvokeAsync(work).Task.Unwrap();
    }

    /// <inheritdoc />
    public void Post(Action work)
    {
        ArgumentNullException.ThrowIfNull(work);

        dispatcher.BeginInvoke(work);
    }
}

/// <summary>
/// <see cref="CompositionTarget.Rendering"/> as an <see cref="IFrameSource"/>. WPF can raise the event more than once for
/// one frame with the same time; the repeats are dropped, so the controller sees each frame once.
/// </summary>
public sealed class CompositionFrameSource : IFrameSource
{
    private TimeSpan _last = TimeSpan.MinValue;
    private bool _running;

    /// <inheritdoc />
    public event Action<TimeSpan>? Frame;

    /// <inheritdoc />
    public void Start()
    {
        if (_running)
        {
            return;
        }

        _running = true;
        _last = TimeSpan.MinValue;
        CompositionTarget.Rendering += OnRendering;
    }

    /// <inheritdoc />
    public void Stop()
    {
        if (!_running)
        {
            return;
        }

        _running = false;
        CompositionTarget.Rendering -= OnRendering;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (e is not RenderingEventArgs args || args.RenderingTime == _last)
        {
            return;
        }

        _last = args.RenderingTime;
        Frame?.Invoke(args.RenderingTime);
    }
}
