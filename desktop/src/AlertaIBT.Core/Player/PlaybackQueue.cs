namespace AlertaIBT.Core.Player;

/// <summary>
/// Which track comes next. Shuffle draws from a bag: every track plays once before any plays again (and the first of a new round is never
/// the one that just played). Repeat decides what the end of a track does. The history lets "previous" walk back. Holds only file paths.
/// <see cref="PeekAuto"/> tells which track the end of the current one will bring WITHOUT moving on (the player decodes it ahead of time for a
/// gapless change or a crossfade); the draw is remembered, so <see cref="NextAuto"/> then really goes to that same track.
/// Not thread-safe: the player calls it under its lock.
/// </summary>
public sealed class PlaybackQueue
{
    /// <summary>How many tracks "previous" can walk back through.</summary>
    public const int MaxHistory = 500;

    private readonly Random _random;
    private readonly HashSet<string> _played = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _history = [];
    private List<string> _tracks = [];
    private bool _shuffle = true;
    private PlayerRepeat _repeat = PlayerRepeat.All;
    private string? _peeked;
    private bool _peekedNewRound;

    /// <param name="random">The source of the draw; a seeded one in tests.</param>
    public PlaybackQueue(Random? random = null) => _random = random ?? Random.Shared;

    /// <summary>Whether the next track is drawn at random.</summary>
    public bool Shuffle
    {
        get => _shuffle;
        set
        {
            if (_shuffle != value)
            {
                _shuffle = value;
                ForgetPeek();
            }
        }
    }

    /// <summary>What the end of a track does.</summary>
    public PlayerRepeat Repeat
    {
        get => _repeat;
        set
        {
            if (_repeat != value)
            {
                _repeat = value;
                ForgetPeek();
            }
        }
    }

    /// <summary>The track that is playing (or was picked last), or <c>null</c>.</summary>
    public string? Current { get; private set; }

    /// <summary>The tracks, in list order.</summary>
    public IReadOnlyList<string> Tracks => _tracks;

    /// <summary>Whether "previous" has somewhere to go back to.</summary>
    public bool HasHistory => _history.Count > 0;

    /// <summary>Replaces the list (a rescan found new or removed files). The current track stays current and the round goes on: tracks that played stay played, new ones join the bag.</summary>
    public void SetTracks(IEnumerable<string> tracks)
    {
        ArgumentNullException.ThrowIfNull(tracks);

        _tracks = tracks.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        _played.IntersectWith(_tracks);
        ForgetPeek();
    }

    /// <summary>Forgets everything but the list: the playlist was changed.</summary>
    public void Reset()
    {
        _played.Clear();
        _history.Clear();
        Current = null;
        ForgetPeek();
    }

    /// <summary>The next track when the user asks (play, next, media key): it wraps around, whatever the repeat mode. <c>null</c> when the list is empty.</summary>
    public string? NextForPlay() => Advance(wrap: true);

    /// <summary>The track the end of the current one will bring (what <see cref="NextAuto"/> will return), without moving on; <c>null</c> when the music should stop. The same answer until something changes.</summary>
    public string? PeekAuto()
    {
        if (Repeat == PlayerRepeat.One && Current is not null)
        {
            return Current;
        }

        if (_peeked is not null)
        {
            return _peeked;
        }

        var (next, newRound) = Pick(wrap: Repeat == PlayerRepeat.All);
        _peeked = next;
        _peekedNewRound = newRound;
        return next;
    }

    /// <summary>The next track when the current one ended: honors repeat. <c>null</c> when the music should stop.</summary>
    public string? NextAuto()
    {
        if (Repeat == PlayerRepeat.One && Current is not null)
        {
            return Current;
        }

        if (PeekAuto() is { } peeked)
        {
            var newRound = _peekedNewRound;
            ForgetPeek();
            return Commit(peeked, newRound);
        }

        return null;
    }

    /// <summary>Moves on to <paramref name="path"/>, which the player already started (the track that <see cref="PeekAuto"/> announced, or, if the settings changed meanwhile, another one).</summary>
    public void AdvanceTo(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        if (Repeat == PlayerRepeat.One && string.Equals(Current, path, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (string.Equals(PeekAuto(), path, StringComparison.OrdinalIgnoreCase))
        {
            NextAuto();
            return;
        }

        ForgetPeek();
        Commit(path, newRound: false);
    }

    /// <summary>The track before the current one, or <c>null</c> when there is none (the caller then restarts the current track).</summary>
    public string? Previous()
    {
        ForgetPeek();
        while (_history.Count > 0)
        {
            var last = _history[^1];
            _history.RemoveAt(_history.Count - 1);
            if (_tracks.Contains(last, StringComparer.OrdinalIgnoreCase))
            {
                Current = last;
                _played.Add(last);
                return last;
            }
        }

        return null;
    }

    private void ForgetPeek()
    {
        _peeked = null;
        _peekedNewRound = false;
    }

    private string? Advance(bool wrap)
    {
        ForgetPeek();
        var (next, newRound) = Pick(wrap);
        return next is null ? null : Commit(next, newRound);
    }

    private string Commit(string next, bool newRound)
    {
        if (newRound)
        {
            _played.Clear();
        }

        if (Current is not null)
        {
            _history.Add(Current);
            if (_history.Count > MaxHistory)
            {
                _history.RemoveAt(0);
            }
        }

        Current = next;
        _played.Add(next);
        return next;
    }

    // Chooses without changing anything (except the random source): the track and whether it opens a new round of the shuffle bag.
    private (string? Track, bool NewRound) Pick(bool wrap)
    {
        if (_tracks.Count == 0)
        {
            return (null, false);
        }

        return Shuffle ? DrawRandom(wrap) : (NextInOrder(wrap), false);
    }

    private string? NextInOrder(bool wrap)
    {
        var index = Current is null ? -1 : _tracks.FindIndex(t => string.Equals(t, Current, StringComparison.OrdinalIgnoreCase));
        if (index + 1 < _tracks.Count)
        {
            return _tracks[index + 1];
        }

        return wrap ? _tracks[0] : null;
    }

    private (string? Track, bool NewRound) DrawRandom(bool wrap)
    {
        var bag = _tracks.Where(t => !_played.Contains(t)).ToList();
        var newRound = false;
        if (bag.Count == 0)
        {
            if (!wrap)
            {
                return (null, false);
            }

            // A new round: everything is back in the bag except the track that just played (unless it is the only one).
            newRound = true;
            bag = _tracks.Where(t => !string.Equals(t, Current, StringComparison.OrdinalIgnoreCase)).ToList();
            if (bag.Count == 0)
            {
                bag = [.. _tracks];
            }
        }

        return (bag[_random.Next(bag.Count)], newRound);
    }
}
