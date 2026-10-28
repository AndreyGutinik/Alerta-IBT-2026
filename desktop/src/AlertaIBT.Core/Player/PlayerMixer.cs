namespace AlertaIBT.Core.Player;

/// <summary>A decoded track, already converted to the format of the output (float samples, interleaved).</summary>
public interface IPcmTrack : IDisposable
{
    /// <summary>The length of the track.</summary>
    TimeSpan Duration { get; }

    /// <summary>How far the decoder has read.</summary>
    TimeSpan Position { get; }

    /// <summary>Reads up to <paramref name="count"/> samples; returns 0 at the end of the track.</summary>
    int Read(float[] buffer, int offset, int count);

    /// <summary>Moves the read position (the decoder and the resampler restart from there).</summary>
    void Seek(TimeSpan position);
}

/// <summary>The sound the output pulls: always fills the whole buffer (silence when nothing plays), so the output keeps running.</summary>
public interface IPcmSource
{
    /// <summary>Frames per second.</summary>
    int SampleRate { get; }

    /// <summary>Samples per frame.</summary>
    int Channels { get; }

    /// <summary>Fills <paramref name="count"/> samples; returns <paramref name="count"/>.</summary>
    int Read(float[] buffer, int offset, int count);
}

/// <summary>
/// The player's own sample pipeline, pulled by the audio output on its thread: the current track, the fade ramp (sample-accurate) and the
/// player volume (smoothed over 30 ms so a slider never clicks). The end of a track and the end of a fade are announced through
/// <paramref name="post"/>, never on the audio thread itself.
/// </summary>
public sealed class PlayerMixer : IPcmSource
{
    /// <summary>How long a change of volume takes to settle.</summary>
    public const double VolumeSmoothingSeconds = 0.03;

    /// <summary>How long the sound ducks to silence before a seek jumps, and rises again after it (so a jump never clicks).</summary>
    public const double SeekRampSeconds = 0.012;

    private readonly object _gate = new();
    private readonly FadeEnvelope _fade = new();
    private readonly FadeEnvelope _duck = new();
    private readonly int _seekFrames;
    private readonly Action<Action> _post;
    private readonly float _volumeStep;
    private IPcmTrack? _track;
    private bool _endRaised;
    private Action? _fadeDone;
    private float _volume = 1f;
    private float _volumeTarget = 1f;
    private TimeSpan? _pendingSeek;

    /// <param name="sampleRate">The output's frames per second.</param>
    /// <param name="channels">The output's channels.</param>
    /// <param name="post">Runs an action off the audio thread (the thread pool in the app; inline in tests).</param>
    public PlayerMixer(int sampleRate, int channels, Action<Action>? post = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);

        SampleRate = sampleRate;
        Channels = channels;
        _post = post ?? (action => ThreadPool.QueueUserWorkItem(_ => action()));
        _volumeStep = (float)(1.0 / (VolumeSmoothingSeconds * sampleRate));
        _seekFrames = Math.Max(1, (int)(SeekRampSeconds * sampleRate));
    }

    /// <summary>Raised (through <c>post</c>) once when the current track ran out.</summary>
    public event Action? TrackEnded;

    /// <inheritdoc />
    public int SampleRate { get; }

    /// <inheritdoc />
    public int Channels { get; }

    /// <summary>The current track, or <c>null</c>.</summary>
    public IPcmTrack? Track
    {
        get
        {
            lock (_gate)
            {
                return _track;
            }
        }
    }

    /// <summary>The fade gain now (0 to 1), for tests and the screen.</summary>
    public float FadeGain
    {
        get
        {
            lock (_gate)
            {
                return _fade.Gain;
            }
        }
    }

    /// <summary>Whether a fade is running.</summary>
    public bool IsFading
    {
        get
        {
            lock (_gate)
            {
                return _fade.IsFading;
            }
        }
    }

    /// <summary>How far into the current track, or zero.</summary>
    public TimeSpan Position
    {
        get
        {
            lock (_gate)
            {
                return _pendingSeek ?? _track?.Position ?? TimeSpan.Zero;
            }
        }
    }

    /// <summary>The length of the current track, or zero.</summary>
    public TimeSpan Duration
    {
        get
        {
            lock (_gate)
            {
                return _track?.Duration ?? TimeSpan.Zero;
            }
        }
    }

    /// <summary>Puts a track on (the previous one is disposed). The gain is not touched: use <see cref="FadeFromSilence"/> for a fade-in.</summary>
    public void SetTrack(IPcmTrack? track)
    {
        IPcmTrack? old;
        lock (_gate)
        {
            old = _track;
            _track = track;
            _endRaised = false;
            _pendingSeek = null;
            _duck.Set(1f);
        }

        old?.Dispose();
    }

    /// <summary>Sets the player volume (0 to 1, linear) and the mute; the change is smoothed.</summary>
    /// <param name="volume">0 to 1.</param>
    /// <param name="muted">Whether it is muted.</param>
    /// <param name="immediate">Jump to it without the smoothing (a new output that has not made a sound yet).</param>
    public void SetVolume(float volume, bool muted, bool immediate = false)
    {
        lock (_gate)
        {
            _volumeTarget = muted ? 0f : Math.Clamp(volume, 0f, 1f);
            if (immediate)
            {
                _volume = _volumeTarget;
            }
        }
    }

    /// <summary>Puts a track on and starts it at silence, ramping up to full over <paramref name="frames"/> frames, in one step: the output can never hear the new track at full gain in between.</summary>
    public void StartTrack(IPcmTrack track, int frames)
    {
        IPcmTrack? old;
        lock (_gate)
        {
            old = _track;
            _track = track;
            _endRaised = false;
            _pendingSeek = null;
            _duck.Set(1f);
            _fadeDone = null;
            _fade.Set(0f);
            _fade.FadeTo(1f, frames);
        }

        old?.Dispose();
    }

    /// <summary>
    /// Jumps to <paramref name="position"/> of the current track without a click: the sound ducks to silence over 12 ms, the track jumps while it
    /// is silent, and it rises again over 12 ms. The fade and the volume are untouched. Returns <c>false</c> when there is no track.
    /// </summary>
    public bool Seek(TimeSpan position)
    {
        lock (_gate)
        {
            if (_track is not { } track)
            {
                return false;
            }

            var last = track.Duration - TimeSpan.FromMilliseconds(200);
            var to = position > last ? last : position;
            _pendingSeek = to < TimeSpan.Zero ? TimeSpan.Zero : to;
            _duck.FadeTo(0f, _seekFrames);
            return true;
        }
    }

    /// <summary>Starts at silence and ramps up to full over <paramref name="frames"/> frames.</summary>
    public void FadeFromSilence(int frames)
    {
        lock (_gate)
        {
            _fadeDone = null;
            _fade.Set(0f);
            _fade.FadeTo(1f, frames);
        }
    }

    /// <summary>Ramps to <paramref name="target"/> over <paramref name="frames"/> frames from the gain it has now; <paramref name="done"/> (may be <c>null</c>) runs through <c>post</c> when the ramp ends.</summary>
    public void FadeTo(float target, int frames, Action? done = null)
    {
        lock (_gate)
        {
            _fadeDone = done;
            _fade.FadeTo(target, frames);
        }
    }

    /// <summary>Back to full gain at once (a cancelled fade-out should instead use <see cref="FadeTo"/>).</summary>
    public void ResetGain()
    {
        lock (_gate)
        {
            _fadeDone = null;
            _fade.Set(1f);
        }
    }

    /// <inheritdoc />
    public int Read(float[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        Action? done = null;
        var raiseEnd = false;
        lock (_gate)
        {
            var filled = 0;
            if (_track is { } track)
            {
                if (_pendingSeek is { } seekTo && _duck.Gain <= 0f && !_duck.IsFading)
                {
                    // Silent now: jump, and rise again from the new place.
                    track.Seek(seekTo);
                    _pendingSeek = null;
                    _endRaised = false;
                    _duck.FadeTo(1f, _seekFrames);
                }

                while (filled < count)
                {
                    var n = track.Read(buffer, offset + filled, count - filled);
                    if (n <= 0)
                    {
                        break;
                    }

                    filled += n;
                }

                if (filled < count && !_endRaised)
                {
                    _endRaised = true;
                    raiseEnd = true;
                }
            }

            if (filled < count)
            {
                Array.Clear(buffer, offset + filled, count - filled);
            }

            ApplyGain(buffer, offset, count);

            if (_fadeDone is not null && !_fade.IsFading)
            {
                done = _fadeDone;
                _fadeDone = null;
            }
        }

        if (raiseEnd)
        {
            _post(() => TrackEnded?.Invoke());
        }

        if (done is not null)
        {
            _post(done);
        }

        return count;
    }

    // Caller holds the lock. One gain per frame: the fade ramp times the smoothed volume.
    private void ApplyGain(float[] buffer, int offset, int count)
    {
        var channels = Channels;
        var end = offset + count;
        for (var i = offset; i + channels <= end; i += channels)
        {
            if (_volume != _volumeTarget)
            {
                var delta = _volumeTarget - _volume;
                _volume = Math.Abs(delta) <= _volumeStep ? _volumeTarget : _volume + Math.Sign(delta) * _volumeStep;
            }

            var gain = _fade.Next() * _volume * _duck.Next();
            for (var c = 0; c < channels; c++)
            {
                buffer[i + c] *= gain;
            }
        }
    }
}
