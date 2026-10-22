namespace AlertaIBT.Core.Queue;

/// <summary>
/// The alerts waiting to run on the screen. Urgent ones jump ahead, a plate shown a moment ago is a duplicate,
/// alerts that wait too long expire, and pausing (the sermon) cancels everything and refuses new alerts.
/// Safe to use from several threads.
/// </summary>
/// <param name="clock">The source of the current time.</param>
public sealed class AlertQueue(TimeProvider clock)
{
    private const int HistoryLimit = 50;
    private static readonly TimeSpan DuplicateWindow = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(30);

    private readonly object _gate = new();
    private readonly List<QueueItem> _pending = [];
    private readonly List<HistoryEntry> _history = [];

    // plate -> when it was last shown; entries older than DuplicateWindow are dropped lazily. It is kept apart from the
    // history, which has 50 lines: rejected and cancelled alerts must not push a shown plate out of the duplicate rule.
    private readonly Dictionary<string, DateTimeOffset> _recentShown = new(StringComparer.Ordinal);
    private QueueItem? _current;
    private bool _paused;

    /// <summary>Raised once after each call that changed the queue or the history, outside the internal lock.</summary>
    public event EventHandler? Changed;

    /// <summary>The alert on screen now, or <c>null</c>.</summary>
    public QueueItem? Current
    {
        get { lock (_gate) { return _current; } }
    }

    /// <summary>The alerts waiting, next to show first (a copy).</summary>
    public IReadOnlyList<QueueItem> Pending
    {
        get { lock (_gate) { return _pending.ToArray(); } }
    }

    /// <summary>The history, newest first, at most 50 entries (a copy).</summary>
    public IReadOnlyList<HistoryEntry> History
    {
        get { lock (_gate) { return _history.ToArray(); } }
    }

    /// <summary>Whether the queue is paused, refusing new alerts.</summary>
    public bool IsPaused
    {
        get { lock (_gate) { return _paused; } }
    }

    /// <summary>Asks whether a plate would be a duplicate now, without queuing anything (the PC preview uses it).</summary>
    /// <param name="plate">The plate in display form; <c>null</c> is never a duplicate.</param>
    /// <returns>The answer.</returns>
    public DuplicateCheck CheckDuplicate(string? plate)
    {
        lock (_gate)
        {
            var duplicate = FindDuplicate(plate, out var minutesAgo);
            return new DuplicateCheck(duplicate, minutesAgo);
        }
    }

    /// <summary>Adds an alert, or refuses it when the queue is paused or the plate is a blocked duplicate.</summary>
    /// <param name="item">The alert.</param>
    /// <param name="policy">What to do when the plate is a duplicate.</param>
    /// <returns>What happened and, when queued, the position.</returns>
    public EnqueueResult Enqueue(QueueItem item, DuplicatePolicy policy)
    {
        EnqueueResult result;
        lock (_gate)
        {
            if (_paused)
            {
                AddHistory(HistoryState.Rejected, HistoryReasons.Sermon, item);
                result = new EnqueueResult(EnqueueKind.PausedRefused, 0, null, false);
            }
            else
            {
                var duplicate = FindDuplicate(item.Plate, out var minutesAgo);
                if (duplicate && policy == DuplicatePolicy.Block)
                {
                    AddHistory(HistoryState.Rejected, HistoryReasons.Duplicate, item);
                    result = new EnqueueResult(EnqueueKind.DuplicateBlocked, 0, minutesAgo, true);
                }
                else
                {
                    var index = item.Urgent ? _pending.FindIndex(p => !p.Urgent) : -1;
                    if (index < 0)
                    {
                        index = _pending.Count;
                    }

                    _pending.Insert(index, item);
                    result = new EnqueueResult(EnqueueKind.Queued, index + 1, minutesAgo, duplicate);
                }
            }
        }

        OnChanged();
        return result;
    }

    /// <summary>Takes the first pending alert to the screen.</summary>
    /// <returns>The alert, or <c>null</c> when paused, when one is already on screen, or when nothing waits.</returns>
    public QueueItem? StartNext()
    {
        QueueItem next;
        lock (_gate)
        {
            if (_paused || _current is not null || _pending.Count == 0)
            {
                return null;
            }

            next = _pending[0];
            _pending.RemoveAt(0);
            _current = next;
        }

        OnChanged();
        return next;
    }

    /// <summary>The alert on screen finished: it goes to the history as shown. Does nothing without one.</summary>
    public void FinishCurrent()
    {
        lock (_gate)
        {
            if (_current is null)
            {
                return;
            }

            AddHistory(HistoryState.Shown, null, _current);
            _current = null;
        }

        OnChanged();
    }

    /// <summary>
    /// Like <see cref="FinishCurrent"/>, but only when the alert on screen is the one named: a pause or a skip that came
    /// first has already taken it away, and then nothing happens.
    /// </summary>
    /// <param name="id">The id of the alert the caller was showing.</param>
    /// <returns><c>true</c> when that alert was still on screen and went to the history as shown.</returns>
    public bool TryFinishCurrent(string id)
    {
        lock (_gate)
        {
            if (_current is null || _current.Id != id)
            {
                return false;
            }

            AddHistory(HistoryState.Shown, null, _current);
            _current = null;
        }

        OnChanged();
        return true;
    }

    /// <summary>
    /// Like <see cref="ReturnCurrentToFront"/>, but only when the alert on screen is the one named.
    /// </summary>
    /// <param name="id">The id of the alert the caller was showing.</param>
    /// <returns><c>true</c> when that alert was still on screen and went back to the front of the queue.</returns>
    public bool TryReturnCurrentToFront(string id)
    {
        lock (_gate)
        {
            if (_current is null || _current.Id != id)
            {
                return false;
            }

            _pending.Insert(0, _current);
            _current = null;
        }

        OnChanged();
        return true;
    }

    /// <summary>Puts the alert on screen back as the first pending one. Does nothing without one.</summary>
    public void ReturnCurrentToFront()
    {
        lock (_gate)
        {
            if (_current is null)
            {
                return;
            }

            _pending.Insert(0, _current);
            _current = null;
        }

        OnChanged();
    }

    /// <summary>Moves a pending alert up (negative) or down (positive), stopping at the ends.</summary>
    /// <param name="id">The alert id.</param>
    /// <param name="delta">How many places to move.</param>
    /// <returns><c>false</c> when the id is not pending.</returns>
    public bool Move(string id, int delta)
    {
        bool moved;
        lock (_gate)
        {
            var from = _pending.FindIndex(p => p.Id == id);
            if (from < 0)
            {
                return false;
            }

            var to = (int)Math.Clamp((long)from + delta, 0L, _pending.Count - 1L);
            moved = to != from;
            if (moved)
            {
                var item = _pending[from];
                _pending.RemoveAt(from);
                _pending.Insert(to, item);
            }
        }

        if (moved)
        {
            OnChanged();
        }

        return true;
    }

    /// <summary>The operator removed a pending alert at the PC.</summary>
    /// <param name="id">The alert id.</param>
    /// <returns><c>false</c> when the id is not pending.</returns>
    public bool Remove(string id) => CancelPending(id, HistoryReasons.RemovedOnPc);

    /// <summary>The sender cancelled a pending alert on the site or the app.</summary>
    /// <param name="id">The alert id.</param>
    /// <returns><c>false</c> when the id is not pending.</returns>
    public bool Cancel(string id) => CancelPending(id, HistoryReasons.CancelledOnSite);

    /// <summary>Cancels every pending alert. The one on screen stays.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            if (_pending.Count == 0)
            {
                return;
            }

            AddHistory(HistoryState.Cancelled, HistoryReasons.QueueCleared, _pending.ToArray());
            _pending.Clear();
        }

        OnChanged();
    }

    /// <summary>
    /// Empties the history ("Limpar histórico"). The alert on screen, the waiting ones, the pause and the memory of plates shown in
    /// the last 10 minutes are not touched: clearing the history must not let a repeated plate through.
    /// </summary>
    /// <returns>How many history lines were removed.</returns>
    public int ClearHistory()
    {
        int removed;
        lock (_gate)
        {
            removed = _history.Count;
            if (removed == 0)
            {
                return 0;
            }

            _history.Clear();
        }

        OnChanged();
        return removed;
    }

    /// <summary>The sermon started: cancels the alert on screen and every pending one, and refuses new alerts.</summary>
    /// <returns>The cancelled alerts, the one on screen first; empty when it was already paused.</returns>
    public IReadOnlyList<QueueItem> Pause()
    {
        TryPause(out var cancelled);
        return cancelled;
    }

    /// <summary>
    /// Like <see cref="Pause"/>, but tells whether this call is the one that paused the queue (it was not paused before),
    /// which is what decides whether the group is told about the sermon.
    /// </summary>
    /// <param name="cancelled">The cancelled alerts, the one on screen first; empty when it was already paused.</param>
    /// <returns><c>true</c> when the queue was not paused before this call.</returns>
    public bool TryPause(out IReadOnlyList<QueueItem> cancelled)
    {
        QueueItem[] items;
        lock (_gate)
        {
            if (_paused)
            {
                cancelled = [];
                return false;
            }

            items = _current is null ? _pending.ToArray() : [_current, .. _pending];
            AddHistory(HistoryState.Cancelled, HistoryReasons.Sermon, items);
            _pending.Clear();
            _current = null;
            _paused = true;
        }

        OnChanged();
        cancelled = items;
        return true;
    }

    /// <summary>The sermon ended: accepts alerts again. Does nothing when not paused.</summary>
    public void Resume()
    {
        lock (_gate)
        {
            if (!_paused)
            {
                return;
            }

            _paused = false;
        }

        OnChanged();
    }

    /// <summary>Drops every pending alert that has waited more than 30 minutes. The one on screen never expires.</summary>
    /// <returns>The expired alerts, in queue order.</returns>
    public IReadOnlyList<QueueItem> ExpireOld()
    {
        QueueItem[] expired;
        lock (_gate)
        {
            var now = clock.GetUtcNow();
            expired = _pending.Where(p => now - p.CreatedAt > MaxWait).ToArray();
            if (expired.Length == 0)
            {
                return [];
            }

            _pending.RemoveAll(p => now - p.CreatedAt > MaxWait);
            AddHistory(HistoryState.Expired, null, expired);
        }

        OnChanged();
        return expired;
    }

    /// <summary>Records an alert that was refused before reaching the queue.</summary>
    /// <param name="item">The alert.</param>
    /// <param name="reason">Why it was refused.</param>
    public void AddRejected(QueueItem item, string reason)
    {
        lock (_gate)
        {
            AddHistory(HistoryState.Rejected, reason, item);
        }

        OnChanged();
    }

    /// <summary>Captures what must survive a restart: the alert on screen (if any) and the pending ones, and the history.</summary>
    /// <returns>The snapshot.</returns>
    public QueueSnapshot ToSnapshot()
    {
        lock (_gate)
        {
            QueueItem[] pending = _current is null ? _pending.ToArray() : [_current, .. _pending];
            PruneRecentShown(clock.GetUtcNow());
            return new QueueSnapshot(pending, _history.ToArray(), new Dictionary<string, DateTimeOffset>(_recentShown, StringComparer.Ordinal));
        }
    }

    /// <summary>Rebuilds a queue from a snapshot. Nothing is on screen and it is not paused.</summary>
    /// <param name="s">The snapshot.</param>
    /// <param name="clock">The source of the current time.</param>
    /// <returns>The queue.</returns>
    public static AlertQueue FromSnapshot(QueueSnapshot s, TimeProvider clock)
    {
        var queue = new AlertQueue(clock);
        queue._pending.AddRange(s.Pending);
        queue._history.AddRange(s.History.Take(HistoryLimit));

        // Plates shown a moment ago: the saved memory, plus the shown lines of the history (a file from before the memory existed).
        foreach (var entry in s.History.Where(h => h.State == HistoryState.Shown && h.Item.Plate is not null))
        {
            queue.RememberShown(entry.Item.Plate!, entry.At);
        }

        foreach (var (plate, at) in s.RecentShown ?? new Dictionary<string, DateTimeOffset>())
        {
            queue.RememberShown(plate, at);
        }

        queue.PruneRecentShown(clock.GetUtcNow());
        return queue;
    }

    private bool CancelPending(string id, string reason)
    {
        lock (_gate)
        {
            var index = _pending.FindIndex(p => p.Id == id);
            if (index < 0)
            {
                return false;
            }

            AddHistory(HistoryState.Cancelled, reason, _pending[index]);
            _pending.RemoveAt(index);
        }

        OnChanged();
        return true;
    }

    // Caller holds the lock. A plate on screen or pending wins over one shown before.
    private bool FindDuplicate(string? plate, out int? minutesAgo)
    {
        minutesAgo = null;
        if (plate is null)
        {
            return false;
        }

        if (_current?.Plate == plate || _pending.Any(p => p.Plate == plate))
        {
            return true;
        }

        var now = clock.GetUtcNow();
        PruneRecentShown(now);
        if (!_recentShown.TryGetValue(plate, out var shownAt))
        {
            return false;
        }

        minutesAgo = (int)(now - shownAt).TotalMinutes;
        return true;
    }

    // Caller holds the lock (or owns the queue, as FromSnapshot does). Keeps the latest time of each plate.
    private void RememberShown(string plate, DateTimeOffset at)
    {
        if (!_recentShown.TryGetValue(plate, out var known) || at > known)
        {
            _recentShown[plate] = at;
        }
    }

    // Caller holds the lock (or owns the queue).
    private void PruneRecentShown(DateTimeOffset now)
    {
        if (_recentShown.Count == 0)
        {
            return;
        }

        foreach (var plate in _recentShown.Where(entry => now - entry.Value > DuplicateWindow).Select(entry => entry.Key).ToArray())
        {
            _recentShown.Remove(plate);
        }
    }

    // Caller holds the lock. The items go to the top of the history keeping the order they are given in.
    private void AddHistory(HistoryState state, string? reason, params QueueItem[] items)
    {
        var now = clock.GetUtcNow();
        if (state == HistoryState.Shown)
        {
            foreach (var item in items.Where(i => i.Plate is not null))
            {
                RememberShown(item.Plate!, now);
            }
        }

        _history.InsertRange(0, items.Select(i => new HistoryEntry(i, state, now, reason)));
        if (_history.Count > HistoryLimit)
        {
            _history.RemoveRange(HistoryLimit, _history.Count - HistoryLimit);
        }
    }

    private void OnChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
