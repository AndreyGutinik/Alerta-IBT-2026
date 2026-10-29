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
/// The player's own sample pipeline, pulled by the audio output on its thread: the current track, the next one (decoded ahead, so the change is
/// gapless, or mixed in over a crossfade), the fade ramp (sample-accurate) and the player volume (smoothed over 30 ms so a slider never clicks).
/// The end of a track, the start of the next one and the end of a fade are announced through <paramref name="post"/>, never on the audio thread itself.
/// </summary>
/// <remarks>
/// <para>
/// A crossfade is made of the outgoing track times the cosine and the incoming one times the sine of the same angle (0 to a quarter turn over the
/// overlap): the squares add up to 1 at every sample, so the loudness of the mix stays level (a straight line down and up dips about 3 dB in the middle).
/// The fade, the seek duck and the volume act after the mix, so they act on both tracks.
/// </para>
/// <para>
/// <see cref="Track"/>, <see cref="Position"/> and <see cref="Duration"/> speak of the INCOMING track from the very first sample of the overlap: the
/// screen switches to the new track when the mix starts (the outgoing one is only a tail by then). <see cref="TrackAdvanced"/> announces that moment.
/// </para>
/// </remarks>
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
    private long _trackBase;
    private long _trackRead;
    private long _trackLength;
    private long _trackAnchor = -1;
    private IPcmTrack? _next;
    private long _nextLength;
    private TrackProfile? _nextProfile;
    private IPcmTrack? _outgoing;
    private long _overlapTotal;
    private long _overlapDone;
    private double _crossfadeSeconds;
    private float[] _scratch = [];
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

    /// <summary>Raised (through <c>post</c>) once when the current track ran out and there was no next one to go on with.</summary>
    public event Action? TrackEnded;

    /// <summary>Raised (through <c>post</c>) when the next track took over: at the first sample of the overlap of a crossfade, or at the exact end of the previous track when there is none. The argument is the track that is now current.</summary>
    public event Action<IPcmTrack>? TrackAdvanced;

    /// <inheritdoc />
    public int SampleRate { get; }

    /// <inheritdoc />
    public int Channels { get; }

    /// <summary>The current track (the incoming one during a crossfade), or <c>null</c>.</summary>
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

    /// <summary>Whether a next track is decoded and waiting.</summary>
    public bool HasNext
    {
        get
        {
            lock (_gate)
            {
                return _next is not null;
            }
        }
    }

    /// <summary>Whether two tracks are being mixed now.</summary>
    public bool IsCrossfading
    {
        get
        {
            lock (_gate)
            {
                return _outgoing is not null;
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

    /// <summary>
    /// The crossfade length that really applies to a track of <paramref name="duration"/> (and, when known, the next one): never more than half of
    /// either track, so a short track still plays its middle and the overlaps of two neighbours never collide.
    /// </summary>
    /// <param name="seconds">The wanted length.</param>
    /// <param name="duration">The length of the outgoing track (zero or less when unknown: no crossfade).</param>
    /// <param name="nextDuration">The length of the incoming track, or zero when unknown.</param>
    public static double ClampCrossfade(double seconds, TimeSpan duration, TimeSpan nextDuration)
    {
        if (!(seconds > 0) || duration <= TimeSpan.Zero)
        {
            return 0;
        }

        var clamped = Math.Min(seconds, duration.TotalSeconds / 2);
        if (nextDuration > TimeSpan.Zero)
        {
            clamped = Math.Min(clamped, nextDuration.TotalSeconds / 2);
        }

        return clamped;
    }

    /// <summary>The gains of a crossfade at <paramref name="progress"/> (0 to 1): the outgoing track is the cosine and the incoming one the sine of a quarter turn, so <c>out^2 + in^2 = 1</c>.</summary>
    public static (float Outgoing, float Incoming) CrossfadeGains(double progress)
    {
        var angle = Math.Clamp(progress, 0, 1) * (Math.PI / 2);
        return ((float)Math.Cos(angle), (float)Math.Sin(angle));
    }

    /// <summary>Puts a track on (the previous one, the waiting next one and any outgoing one are disposed). The gain is not touched: use <see cref="FadeFromSilence"/> for a fade-in.</summary>
    public void SetTrack(IPcmTrack? track)
    {
        List<IPcmTrack> old;
        lock (_gate)
        {
            old = ReplaceAllLocked(track);
            _duck.Set(1f);
        }

        DisposeAll(old);
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

    /// <summary>Sets the crossfade length in seconds (0 is none: the next track still follows with no gap). Takes effect from the next overlap that has not started.</summary>
    public void SetCrossfade(double seconds)
    {
        lock (_gate)
        {
            _crossfadeSeconds = double.IsFinite(seconds) ? Math.Max(0, seconds) : 0;
        }
    }

    /// <summary>
    /// Hands over the track that follows the current one, already decoded: it takes over with no gap when the current one ends (or is mixed in
    /// over the last seconds when a crossfade is set). A track that was waiting is disposed. Disposing is up to the mixer from here on.
    /// </summary>
    /// <param name="track">The decoded next track (already positioned at its effective start when a crossfade is wanted).</param>
    /// <param name="profile">Where its sound really starts and ends (null: the header lengths are used).</param>
    public void SetNext(IPcmTrack track, TrackProfile? profile = null)
    {
        ArgumentNullException.ThrowIfNull(track);

        IPcmTrack? old;
        lock (_gate)
        {
            old = _next;
            _next = track;
            _nextProfile = profile;
            var length = profile is null ? track.Duration : Max(track.Duration, profile.RealDuration);
            _nextLength = Math.Max(0, ToSamples(length) - ToSamples(track.Position));
        }

        old?.Dispose();
    }

    /// <summary>Tells the mixer where the sound of the current track really ends (found after it started playing); ignored when <paramref name="track"/> is no longer the current one.</summary>
    public void SetProfile(IPcmTrack track, TrackProfile? profile)
    {
        ArgumentNullException.ThrowIfNull(track);

        lock (_gate)
        {
            if (ReferenceEquals(_track, track) && profile is not null)
            {
                ApplyProfile(profile);
            }
        }
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    // Caller holds the lock.
    private void ApplyProfile(TrackProfile profile)
    {
        _trackLength = Math.Max(_trackLength, ToSamples(profile.RealDuration));
        _trackAnchor = ToSamples(Max(TimeSpan.Zero, profile.EffectiveEnd));
    }

    /// <summary>Drops the track that was waiting (it is disposed). A crossfade that already started is not touched.</summary>
    public void ClearNext()
    {
        IPcmTrack? old;
        lock (_gate)
        {
            old = _next;
            _next = null;
            _nextProfile = null;
        }

        old?.Dispose();
    }

    /// <summary>Puts a track on and starts it at silence, ramping up to full over <paramref name="frames"/> frames, in one step: the output can never hear the new track at full gain in between.</summary>
    public void StartTrack(IPcmTrack track, int frames)
    {
        List<IPcmTrack> old;
        lock (_gate)
        {
            old = ReplaceAllLocked(track);
            _duck.Set(1f);
            _fadeDone = null;
            _fade.Set(0f);
            _fade.FadeTo(1f, frames);
        }

        DisposeAll(old);
    }

    /// <summary>
    /// Jumps to <paramref name="position"/> of the current track without a click: the sound ducks to silence over 12 ms, the track jumps while it
    /// is silent, and it rises again over 12 ms. The fade and the volume are untouched. A crossfade in progress is cancelled by the jump: the
    /// track that was sought keeps playing alone and the one going out stops. Returns <c>false</c> when there is no track.
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
        List<IPcmTrack>? dropped = null;
        List<IPcmTrack>? advanced = null;
        lock (_gate)
        {
            if (_track is { } track && _pendingSeek is { } seekTo && _duck.Gain <= 0f && !_duck.IsFading)
            {
                // Silent now: jump, and rise again from the new place. A crossfade ends here: only the sought track goes on.
                track.Seek(seekTo);
                _trackBase = ToSamples(seekTo);
                _trackRead = 0;
                _pendingSeek = null;
                _endRaised = false;
                _duck.FadeTo(1f, _seekFrames);
                if (_outgoing is not null)
                {
                    (dropped ??= []).Add(_outgoing);
                    _outgoing = null;
                }
            }

            var filled = FillLocked(buffer, offset, count, ref dropped, ref advanced, ref raiseEnd);
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

        if (dropped is not null)
        {
            foreach (var old in dropped)
            {
                _post(old.Dispose);
            }
        }

        if (advanced is not null)
        {
            foreach (var now in advanced)
            {
                var track = now;
                _post(() => TrackAdvanced?.Invoke(track));
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

    private static void DisposeAll(List<IPcmTrack> tracks)
    {
        foreach (var track in tracks)
        {
            track.Dispose();
        }
    }

    // Caller holds the lock. Replaces the current track and returns every track that was held (to dispose outside the lock).
    private List<IPcmTrack> ReplaceAllLocked(IPcmTrack? track)
    {
        var old = new List<IPcmTrack>(3);
        if (_track is not null)
        {
            old.Add(_track);
        }

        if (_next is not null)
        {
            old.Add(_next);
        }

        if (_outgoing is not null)
        {
            old.Add(_outgoing);
        }

        _next = null;
        _nextProfile = null;
        _outgoing = null;
        _overlapTotal = 0;
        _overlapDone = 0;
        Attach(track);
        _endRaised = false;
        _pendingSeek = null;
        return old;
    }

    // Caller holds the lock. Makes `track` the current one: where it starts and how long it is, in samples.
    private void Attach(IPcmTrack? track, TrackProfile? profile = null)
    {
        _track = track;
        _trackRead = 0;
        _trackBase = track is null ? 0 : ToSamples(track.Position);
        _trackLength = track is null ? 0 : ToSamples(track.Duration);
        _trackAnchor = -1;
        if (track is not null && profile is not null)
        {
            ApplyProfile(profile);
        }
    }

    private long ToSamples(TimeSpan time) => (long)Math.Round(Math.Max(0, time.TotalSeconds) * SampleRate) * Channels;

    // Caller holds the lock. The samples of overlap that apply now (0 when there is no crossfade or the lengths are not known).
    private long OverlapSamples()
    {
        if (_crossfadeSeconds <= 0 || (_trackAnchor < 0 ? _trackLength : _trackAnchor) <= 0)
        {
            return 0;
        }

        var perSecond = (double)SampleRate * Channels;
        var outLength = _trackAnchor >= 0 ? _trackAnchor : _trackLength;
        var seconds = ClampCrossfade(_crossfadeSeconds, TimeSpan.FromSeconds(Math.Max(0, outLength) / perSecond), TimeSpan.FromSeconds(_nextLength / perSecond));
        return (long)Math.Round(seconds * SampleRate) * Channels;
    }

    // Caller holds the lock. Fills up to `count` samples of the mix before the gain; returns how many (less than count only when nothing plays).
    private int FillLocked(float[] buffer, int offset, int count, ref List<IPcmTrack>? dropped, ref List<IPcmTrack>? advanced, ref bool raiseEnd)
    {
        var channels = Channels;
        var usable = count - (count % channels);
        var filled = 0;
        while (filled < usable && _track is { } current)
        {
            var want = usable - filled;
            if (_outgoing is not null)
            {
                want = (int)Math.Min(want, _overlapTotal - _overlapDone);
                MixOverlap(current, _outgoing, buffer, offset + filled, want);
                filled += want;
                if (_overlapDone >= _overlapTotal)
                {
                    (dropped ??= []).Add(_outgoing);
                    _outgoing = null;
                }

                continue;
            }

            if (_next is not null)
            {
                var overlap = OverlapSamples();
                if (overlap > 0)
                {
                    var remaining = (_trackAnchor >= 0 ? _trackAnchor : _trackLength) - (_trackBase + _trackRead);
                    if (remaining <= overlap)
                    {
                        StartOverlap(remaining, overlap, ref advanced);
                        continue;
                    }

                    // Stop this read exactly where the overlap begins.
                    var until = remaining - overlap;
                    if (until < want)
                    {
                        want = (int)Math.Max(channels, until - (until % channels));
                    }
                }
            }

            var read = ReadFully(current, buffer, offset + filled, want);
            _trackRead += read;
            filled += read;
            if (read < want)
            {
                if (_next is not null)
                {
                    // Gapless: the rest of this very buffer already comes from the next track.
                    (dropped ??= []).Add(current);
                    Attach(_next, _nextProfile);
                    _next = null;
                    _nextProfile = null;
                    _endRaised = false;
                    (advanced ??= []).Add(_track!);
                    continue;
                }

                if (!_endRaised)
                {
                    _endRaised = true;
                    raiseEnd = true;
                }

                break;
            }
        }

        return filled;
    }

    // Caller holds the lock. The next track becomes current at once and the current one goes on as the outgoing tail of the mix.
    private void StartOverlap(long remaining, long overlap, ref List<IPcmTrack>? advanced)
    {
        var length = remaining > 0 ? Math.Min(remaining, overlap) : overlap;
        length -= length % Channels;
        _overlapTotal = Math.Max(Channels, length);
        _overlapDone = 0;
        _outgoing = _track;
        Attach(_next, _nextProfile);
        _next = null;
        _nextProfile = null;
        _endRaised = false;
        (advanced ??= []).Add(_track!);
    }

    // Caller holds the lock. Mixes `count` samples of the overlap into buffer: the incoming track times the sine, the outgoing one times the cosine.
    private void MixOverlap(IPcmTrack incoming, IPcmTrack outgoing, float[] buffer, int offset, int count)
    {
        var channels = Channels;
        if (_scratch.Length < count)
        {
            _scratch = new float[count];
        }

        var inRead = ReadFully(incoming, buffer, offset, count);
        _trackRead += inRead;
        var outRead = ReadFully(outgoing, _scratch, 0, count);
        var frames = _overlapTotal / channels;
        var first = _overlapDone / channels;
        for (var i = 0; i < count; i += channels)
        {
            var progress = (double)(first + (i / channels)) / frames;
            var (outGain, inGain) = CrossfadeGains(progress);
            for (var c = 0; c < channels; c++)
            {
                var wanted = i + c;
                var fresh = wanted < inRead ? buffer[offset + wanted] : 0f;
                var old = wanted < outRead ? _scratch[wanted] : 0f;
                buffer[offset + wanted] = (fresh * inGain) + (old * outGain);
            }
        }

        _overlapDone += count;
    }

    private static int ReadFully(IPcmTrack track, float[] buffer, int offset, int count)
    {
        var filled = 0;
        while (filled < count)
        {
            var n = track.Read(buffer, offset + filled, count - filled);
            if (n <= 0)
            {
                break;
            }

            filled += n;
        }

        return filled;
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
