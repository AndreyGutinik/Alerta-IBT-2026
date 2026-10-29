using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace AlertaIBT.App.Views;

/// <summary>
/// The timeline of the player: a slider that you click or drag to a place (the thumb and the time label follow while you hold it, the music
/// is not touched) and that asks to seek only when you let go. Left and Right move 5 seconds when it has the focus. It never changes its
/// own value: the owner binds <see cref="RangeBase.Value"/> one way from the player's position and answers the events.
/// </summary>
public sealed class SeekSlider : Slider
{
    /// <summary>How far an arrow key moves the timeline, in seconds.</summary>
    public const double KeyStepSeconds = 5;

    private bool _dragging;

    /// <summary>The timeline's thumb was taken (a press anywhere on it).</summary>
    public event EventHandler<SeekEventArgs>? SeekStarted;

    /// <summary>The thumb moved to a new place while held.</summary>
    public event EventHandler<SeekEventArgs>? SeekMoved;

    /// <summary>The thumb was let go: seek to this place.</summary>
    public event EventHandler<SeekEventArgs>? SeekCommitted;

    /// <summary>The drag ended without a seek (the mouse capture was lost).</summary>
    public event EventHandler? SeekCancelled;

    /// <summary>An arrow key asked to move by this many seconds.</summary>
    public event EventHandler<SeekEventArgs>? SeekStepped;

    /// <summary>Whether the thumb is being held.</summary>
    public bool IsDragging => _dragging;

    /// <summary>Starts a drag at <paramref name="seconds"/> (a press). Used by the mouse handlers and by tests.</summary>
    internal void BeginDrag(double seconds)
    {
        if (!IsEnabled || _dragging)
        {
            return;
        }

        _dragging = true;
        SeekStarted?.Invoke(this, new SeekEventArgs(seconds));
        SeekMoved?.Invoke(this, new SeekEventArgs(seconds));
    }

    /// <summary>Moves the held thumb to <paramref name="seconds"/>.</summary>
    internal void DragTo(double seconds)
    {
        if (_dragging)
        {
            SeekMoved?.Invoke(this, new SeekEventArgs(Math.Clamp(seconds, Minimum, Maximum)));
        }
    }

    /// <summary>Lets go at <paramref name="seconds"/>: the seek is asked for.</summary>
    internal void EndDrag(double seconds)
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        SeekCommitted?.Invoke(this, new SeekEventArgs(Math.Clamp(seconds, Minimum, Maximum)));
    }

    /// <summary>The key handling: Left and Right move 5 s. Returns whether the key was used.</summary>
    internal bool HandleKey(Key key)
    {
        if (!IsEnabled || _dragging)
        {
            return false;
        }

        switch (key)
        {
            case Key.Left:
                SeekStepped?.Invoke(this, new SeekEventArgs(-KeyStepSeconds));
                return true;
            case Key.Right:
                SeekStepped?.Invoke(this, new SeekEventArgs(KeyStepSeconds));
                return true;
            default:
                return false;
        }
    }

    /// <inheritdoc />
    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (IsEnabled && Track is { } track)
        {
            Focus();
            CaptureMouse();
            BeginDrag(ValueAt(track, e));
            e.Handled = true; // the stock slider (and its thumb) must not move the value on its own
            return;
        }

        base.OnPreviewMouseLeftButtonDown(e);
    }

    /// <inheritdoc />
    protected override void OnPreviewMouseMove(MouseEventArgs e)
    {
        if (_dragging && Track is { } track)
        {
            DragTo(ValueAt(track, e));
        }

        base.OnPreviewMouseMove(e);
    }

    /// <inheritdoc />
    protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_dragging && Track is { } track)
        {
            // The seek is asked for BEFORE the capture is let go: releasing it raises LostMouseCapture at once, and a drag still marked as held
            // there would be taken for a cancelled one (the thumb went back and the music never jumped).
            EndDrag(ValueAt(track, e));
            ReleaseMouseCapture();
            e.Handled = true;
            return;
        }

        base.OnPreviewMouseLeftButtonUp(e);
    }

    /// <inheritdoc />
    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        if (_dragging)
        {
            _dragging = false;
            SeekCancelled?.Invoke(this, EventArgs.Empty);
        }

        base.OnLostMouseCapture(e);
    }

    /// <inheritdoc />
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (HandleKey(e.Key))
        {
            e.Handled = true;
            return;
        }

        base.OnPreviewKeyDown(e);
    }

    private Track? Track => Template?.FindName("PART_Track", this) as Track;

    // The place under the mouse, worked out from the track alone: the thumb's centre is at (value - minimum) / range of the room the thumb has
    // (the track minus the thumb's own width). Track.ValueFromPoint is relative to where the thumb was last laid out, which drifts when the
    // value changes between two layout passes (a burst of moves), so it is not used.
    private double ValueAt(Track track, MouseEventArgs e)
    {
        var x = e.GetPosition(track).X;
        var thumb = track.Thumb?.ActualWidth ?? 0;
        var room = track.ActualWidth - thumb;
        if (room <= 0 || !double.IsFinite(x))
        {
            return Value;
        }

        var fraction = Math.Clamp((x - (thumb / 2)) / room, 0, 1);
        return Math.Clamp(Minimum + (fraction * (Maximum - Minimum)), Minimum, Maximum);
    }
}

/// <summary>A place on the timeline, or a step, in seconds.</summary>
public sealed class SeekEventArgs(double seconds) : EventArgs
{
    /// <summary>The place (or, for a step, the distance and direction) in seconds.</summary>
    public double Seconds { get; } = seconds;
}
