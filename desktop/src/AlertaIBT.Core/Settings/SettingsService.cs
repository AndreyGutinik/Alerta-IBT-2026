namespace AlertaIBT.Core.Settings;

/// <summary>
/// The settings for the whole app: one current value that every part reads, changed only through <see cref="Update"/>.
/// Each change is held in range, announced at once through <see cref="Changed"/> and saved to disk after
/// <see cref="SaveDelay"/> without another change (so dragging a slider writes the file once, not a hundred times).
/// <see cref="Flush"/> (and <see cref="Dispose"/>) saves at once, so closing the app never loses the last change.
/// </summary>
/// <remarks>
/// A settings file that exists but could not be read or set aside is never overwritten: until a retry can read it, changes
/// live only in memory. When the retry succeeds, what the file holds wins over the memory-only changes. Failures are
/// reported through the error callback and never thrown to the caller of <see cref="Update"/>.
/// </remarks>
public sealed class SettingsService : IDisposable
{
    /// <summary>How long settings must stay unchanged before they are written (600 ms).</summary>
    public static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(600);

    private readonly SettingsStore _store;
    private readonly Action<Exception>? _onError;
    private readonly ITimer _timer;
    private readonly object _gate = new();
    private readonly object _saveGate = new();

    private AppSettings _current;
    private bool _dirty;
    private bool _fileUnreadable;
    private bool _disposed;

    /// <summary>Loads the settings from <paramref name="store"/> (never throws; see <see cref="SettingsStore.Load"/>).</summary>
    /// <param name="store">The settings file.</param>
    /// <param name="clock">Drives the save delay; the system clock by default.</param>
    /// <param name="onError">Told about a save that failed, a handler that threw, or a file that stays unreadable; it must not throw.</param>
    public SettingsService(SettingsStore store, TimeProvider? clock = null, Action<Exception>? onError = null)
    {
        ArgumentNullException.ThrowIfNull(store);

        _store = store;
        _onError = onError;
        LoadResult = store.Load();
        _current = LoadResult.Settings;
        _fileUnreadable = LoadResult is { WasCorrupt: true, BackupPath: null };
        _timer = (clock ?? TimeProvider.System).CreateTimer(_ => Flush(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Raised after every <see cref="Update"/>, on the caller's thread and outside every lock, with the new settings.</summary>
    /// <remarks>A handler must not call <see cref="Update"/> unconditionally: that would raise the event again forever.</remarks>
    public event Action<AppSettings>? Changed;

    /// <summary>What loading found at start: the file's settings or the defaults, and whether the file had to be set aside.</summary>
    public SettingsLoadResult LoadResult { get; }

    /// <summary>Whether the file could not be read at start and is being protected from being overwritten.</summary>
    public bool FileUnreadable
    {
        get
        {
            lock (_gate)
            {
                return _fileUnreadable;
            }
        }
    }

    /// <summary>
    /// Whether a change is not on disk yet: it is waiting for its delay, or the last save failed. After <see cref="Flush"/> this
    /// is how the caller knows the save worked.
    /// </summary>
    public bool HasUnsavedChanges
    {
        get
        {
            lock (_gate)
            {
                return _dirty;
            }
        }
    }

    /// <summary>The current settings (immutable; take it once and read from it).</summary>
    public AppSettings Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <summary>
    /// Applies <paramref name="change"/> to the current settings, holds the result in range, announces it and schedules the save.
    /// </summary>
    public void Update(Func<AppSettings, AppSettings> change)
    {
        ArgumentNullException.ThrowIfNull(change);

        AppSettings next;
        lock (_gate)
        {
            next = SettingsJson.Normalize(change(_current));
            _current = next;
            _dirty = true;
            if (!_disposed)
            {
                _timer.Change(SaveDelay, Timeout.InfiniteTimeSpan);
            }
        }

        RaiseChanged(next);
    }

    /// <summary>
    /// Writes the current settings now when there is no file yet, so a first run leaves a <c>settings.json</c> behind.
    /// Does nothing when the file exists (even a damaged one, which loading already set aside).
    /// </summary>
    public void SaveIfMissing()
    {
        lock (_gate)
        {
            if (_fileUnreadable || File.Exists(_store.FilePath))
            {
                return;
            }

            _dirty = true;
        }

        Flush();
    }

    /// <summary>Writes the pending change, if any, now. Never throws; a failure goes to the error callback and the change stays pending.</summary>
    public void Flush()
    {
        lock (_saveGate)
        {
            AppSettings snapshot;
            lock (_gate)
            {
                if (!_dirty)
                {
                    return;
                }

                snapshot = _current;
            }

            if (FileUnreadable && !TryUnblock())
            {
                return;
            }

            try
            {
                _store.Save(snapshot);
            }
            catch (Exception ex)
            {
                // Not only I/O errors: this also runs on a timer thread, where an escaped exception would end the process.
                Report(ex);
                return;
            }

            lock (_gate)
            {
                // A change made while the file was being written is a newer value: it keeps the flag and has its own timer.
                if (ReferenceEquals(_current, snapshot))
                {
                    _dirty = false;
                }
            }
        }
    }

    /// <summary>Saves the pending change and stops the delay timer.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _timer.Dispose();
        }

        Flush();
    }

    // The file was unreadable at start: tries once more. True when it may be written now.
    private bool TryUnblock()
    {
        var retry = _store.Load();
        if (retry is { WasCorrupt: true, BackupPath: null })
        {
            Report(new IOException($"The settings file '{_store.FilePath}' still cannot be read; the changes stay in memory so it is not overwritten."));
            return false;
        }

        AppSettings loaded = retry.Settings;
        lock (_gate)
        {
            _fileUnreadable = false;
            _current = loaded;
            _dirty = false;
        }

        RaiseChanged(loaded);
        return false;
    }

    private void RaiseChanged(AppSettings settings)
    {
        var handlers = Changed;
        if (handlers is null)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList().Cast<Action<AppSettings>>())
        {
            try
            {
                handler(settings);
            }
            catch (Exception ex)
            {
                Report(ex);
            }
        }
    }

    private void Report(Exception ex)
    {
        try
        {
            _onError?.Invoke(ex);
        }
        catch (Exception)
        {
            // The error callback is the last resort: nothing is left to tell.
        }
    }
}
