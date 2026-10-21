namespace AlertaIBT.Core.Overlay;

/// <summary>Where the bar is in its one pass over the screen.</summary>
public enum BarPhase
{
    /// <summary>The bar slides in and fades in; the text waits off screen on the right.</summary>
    In,

    /// <summary>The bar is fully shown and the text crosses from the right edge to beyond the left edge.</summary>
    Scroll,

    /// <summary>The text is gone and the bar slides out and fades out.</summary>
    Out,

    /// <summary>Over: the bar is hidden and stays so.</summary>
    Done,
}

/// <summary>What the bar looks like at one instant.</summary>
/// <param name="Phase">Where in the pass the instant falls.</param>
/// <param name="Offset">How far the bar is pushed out of the screen: 0 fully visible, 1 fully hidden (multiply by the bar height).</param>
/// <param name="Opacity">The bar's opacity, 0 to 1.</param>
/// <param name="TextX">The left edge of the text in screen pixels: the screen width at the start, minus the text width at the end.</param>
public readonly record struct BarFrame(BarPhase Phase, double Offset, double Opacity, double TextX);

/// <summary>
/// The whole life of one alert on the bar, as a pure function of the elapsed time: the bar slides and fades in, the text
/// crosses the screen once at a constant speed, the bar slides and fades out. Because every value comes from the time
/// (never from a count of frames) a dropped frame does not slow the text, and a run that is restarted, such as when the
/// LED comes back after vanishing mid-scroll, is just a new timeline starting at zero.
/// </summary>
public sealed class BarTimeline
{
    private const double LongestScrollSeconds = 24 * 60 * 60;

    private readonly double _screenWidth;
    private readonly double _textWidth;
    private readonly double _speed;
    private readonly TimeSpan _slide;
    private readonly TimeSpan _scrollEnd;

    /// <param name="screenWidth">The screen width in pixels (2560 for the LED); more than 0.</param>
    /// <param name="textWidth">The width of the rendered text in pixels; 0 or more.</param>
    /// <param name="speed">The scroll speed in pixels per second; more than 0.</param>
    /// <param name="slide">How long the bar takes to slide in, and again to slide out; 0 or more.</param>
    /// <exception cref="ArgumentOutOfRangeException">A number is not finite or out of range, or the pass would last over a day.</exception>
    public BarTimeline(double screenWidth, double textWidth, double speed, TimeSpan slide)
    {
        if (!double.IsFinite(screenWidth) || screenWidth <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(screenWidth), screenWidth, "The screen width must be a positive number.");
        }

        if (!double.IsFinite(textWidth) || textWidth < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(textWidth), textWidth, "The text width cannot be negative.");
        }

        if (!double.IsFinite(speed) || speed <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(speed), speed, "The speed must be a positive number.");
        }

        if (slide < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(slide), slide, "The slide time cannot be negative.");
        }

        var scrollSeconds = (screenWidth + textWidth) / speed;
        if (!(scrollSeconds <= LongestScrollSeconds))
        {
            throw new ArgumentOutOfRangeException(nameof(speed), speed, "The text would take over a day to cross the screen.");
        }

        _screenWidth = screenWidth;
        _textWidth = textWidth;
        _speed = speed;
        _slide = slide;
        _scrollEnd = slide + TimeSpan.FromSeconds(scrollSeconds);
        Total = _scrollEnd + slide;
    }

    /// <summary>The length of the whole pass: slide in, scroll, slide out.</summary>
    public TimeSpan Total { get; }

    /// <summary>
    /// The frame <paramref name="t"/> after the start. Before the start it is the first frame; from <see cref="Total"/> on
    /// it is <see cref="BarPhase.Done"/>. Entering and leaving are ease-out cubic (fast at first, settling softly).
    /// </summary>
    public BarFrame At(TimeSpan t)
    {
        if (t < TimeSpan.Zero)
        {
            t = TimeSpan.Zero;
        }

        if (t >= Total)
        {
            return new BarFrame(BarPhase.Done, 1, 0, -_textWidth);
        }

        if (t >= _scrollEnd)
        {
            var eased = EaseOutCubic((t - _scrollEnd) / _slide);
            return new BarFrame(BarPhase.Out, eased, 1 - eased, -_textWidth);
        }

        if (t >= _slide)
        {
            // Never past the end even by a rounding error: the text is fully out only when the slide out begins.
            var x = Math.Max(_screenWidth - (_speed * (t - _slide).TotalSeconds), -_textWidth);
            return new BarFrame(BarPhase.Scroll, 0, 1, x);
        }

        var entered = EaseOutCubic(t / _slide);
        return new BarFrame(BarPhase.In, 1 - entered, entered, _screenWidth);
    }

    private static double EaseOutCubic(double progress)
    {
        var rest = 1 - Math.Clamp(progress, 0, 1);
        return 1 - (rest * rest * rest);
    }
}
