using AlertaIBT.Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AlertaIBT.Core.Player;

/// <summary>The folders on disk, as thin as it can be (a fake in tests).</summary>
public interface IMediaFileSystem
{
    /// <summary>Whether the folder exists.</summary>
    bool DirectoryExists(string folder);

    /// <summary>Every file under the folder, recursively (any extension); empty when the folder is missing or unreadable.</summary>
    IEnumerable<string> EnumerateFiles(string folder);
}

/// <summary>Tells when something changed in a folder (it may fire many times for one copy: the library debounces).</summary>
public interface IFolderWatcher : IDisposable
{
    /// <summary>Raised on any thread when a file or folder under the watched one was created, deleted or renamed.</summary>
    event Action? Changed;
}

/// <summary>The real disk.</summary>
public sealed class PhysicalMediaFileSystem : IMediaFileSystem
{
    private static readonly EnumerationOptions Options = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
    };

    /// <inheritdoc />
    public bool DirectoryExists(string folder) => Directory.Exists(folder);

    /// <inheritdoc />
    public IEnumerable<string> EnumerateFiles(string folder)
    {
        try
        {
            return Directory.Exists(folder) ? Directory.EnumerateFiles(folder, "*", Options).ToList() : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return [];
        }
    }

    /// <summary>A watcher of <paramref name="folder"/>, or <c>null</c> when it cannot be watched (missing folder, network share that refuses).</summary>
    public static IFolderWatcher? Watch(string folder)
    {
        try
        {
            return Directory.Exists(folder) ? new FileSystemFolderWatcher(folder) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or PlatformNotSupportedException)
        {
            return null;
        }
    }

    private sealed class FileSystemFolderWatcher : IFolderWatcher
    {
        private readonly FileSystemWatcher _watcher;

        public FileSystemFolderWatcher(string folder)
        {
            _watcher = new FileSystemWatcher(folder)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
            };
            _watcher.Created += (_, e) => Raise(e.Name);
            _watcher.Deleted += (_, e) => Raise(e.Name);
            _watcher.Renamed += (_, e) => Raise(e.Name);
            _watcher.EnableRaisingEvents = true;
        }

        public event Action? Changed;

        public void Dispose() => _watcher.Dispose();

        // The temporary copy and the backup that the tag writer makes beside a FLAC (every few seconds while it works) are not music: they must
        // not wake a rescan of the whole folder.
        private void Raise(string? name)
        {
            if (name is not null && (name.EndsWith(".ibt-tmp", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".ibt-bak", StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            Changed?.Invoke();
        }
    }
}

/// <summary>Finds the music of a folder.</summary>
public static class PlaylistScanner
{
    /// <summary>The extensions the player accepts (lower case, with the dot).</summary>
    public static readonly IReadOnlyList<string> Extensions = [".flac", ".mp3", ".wav", ".m4a"];

    /// <summary>Whether <paramref name="path"/> is a file the player can play.</summary>
    public static bool IsMusic(string path) =>
        Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>The music files under <paramref name="folder"/> (recursive), in natural order of their paths.</summary>
    public static IReadOnlyList<string> Scan(IMediaFileSystem fileSystem, string folder)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);

        return fileSystem.EnumerateFiles(folder)
            .Where(IsMusic)
            .Order(NaturalStringComparer.Instance)
            .ToList();
    }
}

/// <summary>
/// The playlists as folders: scans each one in the background, keeps its files, and rescans by itself (after
/// <see cref="Debounce"/>) when files appear or go away, or when asked. A playlist whose folder is missing is simply empty.
/// </summary>
public sealed class PlaylistLibrary : IDisposable
{
    /// <summary>How long changes in a folder are gathered before one rescan (2 seconds: a copy of many files fires many events).</summary>
    public static readonly TimeSpan Debounce = TimeSpan.FromSeconds(2);

    private readonly IMediaFileSystem _fileSystem;
    private readonly Func<string, IFolderWatcher?> _watch;
    private readonly TimeProvider _clock;
    private readonly Action<Action> _background;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    /// <param name="fileSystem">The disk.</param>
    /// <param name="watch">Starts watching a folder; may return <c>null</c>.</param>
    /// <param name="clock">Times the debounce.</param>
    /// <param name="background">Runs a scan off the caller's thread (the thread pool in the app; inline in tests).</param>
    /// <param name="logger">Receives the counts.</param>
    public PlaylistLibrary(
        IMediaFileSystem fileSystem,
        Func<string, IFolderWatcher?> watch,
        TimeProvider? clock = null,
        Action<Action>? background = null,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(watch);

        _fileSystem = fileSystem;
        _watch = watch;
        _clock = clock ?? TimeProvider.System;
        _background = background ?? (action => ThreadPool.QueueUserWorkItem(_ => action()));
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>Raised (on any thread) after a playlist was scanned, with its name. Not raised when the files are the same as before.</summary>
    public event Action<string>? Changed;

    /// <summary>Makes the library match the settings' list: new playlists are scanned and watched, removed ones dropped, a changed folder rescanned.</summary>
    public void Configure(IReadOnlyList<PlaylistEntry> playlists)
    {
        ArgumentNullException.ThrowIfNull(playlists);

        var toScan = new List<string>();
        var toDispose = new List<IDisposable>();
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            foreach (var gone in _entries.Keys.Where(k => !playlists.Any(p => string.Equals(p.Name, k, StringComparison.OrdinalIgnoreCase))).ToList())
            {
                toDispose.AddRange(Release(_entries[gone]));
                _entries.Remove(gone);
            }

            foreach (var list in playlists)
            {
                if (_entries.TryGetValue(list.Name, out var known) && string.Equals(known.Folder, list.Folder, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (known is not null)
                {
                    toDispose.AddRange(Release(known));
                }

                _entries[list.Name] = new Entry(list.Folder);
                toScan.Add(list.Name);
            }
        }

        foreach (var item in toDispose)
        {
            item.Dispose();
        }

        foreach (var name in toScan)
        {
            Watch(name);
            var captured = name;
            _background(() => Scan(captured));
        }
    }

    /// <summary>The music files of a playlist, in natural order; empty when unknown or not scanned yet.</summary>
    public IReadOnlyList<string> Tracks(string? playlist)
    {
        lock (_gate)
        {
            return playlist is not null && _entries.TryGetValue(playlist, out var entry) ? entry.Tracks : [];
        }
    }

    /// <summary>The folder of a playlist, or <c>null</c>.</summary>
    public string? Folder(string? playlist)
    {
        lock (_gate)
        {
            return playlist is not null && _entries.TryGetValue(playlist, out var entry) ? entry.Folder : null;
        }
    }

    /// <summary>Scans a playlist now, on the caller's thread ("Atualizar"). Also tries again to watch a folder that could not be watched.</summary>
    public void Refresh(string playlist)
    {
        Watch(playlist);
        Scan(playlist);
    }

    /// <summary>Scans every playlist now (on the caller's thread).</summary>
    public void RefreshAll()
    {
        string[] names;
        lock (_gate)
        {
            names = [.. _entries.Keys];
        }

        foreach (var name in names)
        {
            Refresh(name);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        List<IDisposable> all = [];
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (var entry in _entries.Values)
            {
                all.AddRange(Release(entry));
            }

            _entries.Clear();
        }

        foreach (var item in all)
        {
            item.Dispose();
        }
    }

    private static List<IDisposable> Release(Entry entry)
    {
        var items = new List<IDisposable>();
        if (entry.Watcher is not null)
        {
            items.Add(entry.Watcher);
        }

        if (entry.Timer is not null)
        {
            items.Add(entry.Timer);
        }

        entry.Watcher = null;
        entry.Timer = null;
        return items;
    }

    private void Watch(string name)
    {
        string? folder;
        lock (_gate)
        {
            if (_disposed || !_entries.TryGetValue(name, out var entry) || entry.Watcher is not null)
            {
                return;
            }

            folder = entry.Folder;
        }

        var watcher = _watch(folder);
        if (watcher is null)
        {
            return;
        }

        lock (_gate)
        {
            if (!_disposed && _entries.TryGetValue(name, out var entry) && entry.Watcher is null && entry.Folder == folder)
            {
                entry.Watcher = watcher;
                watcher.Changed += () => OnFolderChanged(name);
                return;
            }
        }

        watcher.Dispose();
    }

    private void OnFolderChanged(string name)
    {
        lock (_gate)
        {
            if (_disposed || !_entries.TryGetValue(name, out var entry))
            {
                return;
            }

            // Every event pushes the rescan back: one scan, Debounce after the last change.
            entry.Timer ??= _clock.CreateTimer(_ => _background(() => Scan(name)), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            entry.Timer.Change(Debounce, Timeout.InfiniteTimeSpan);
        }
    }

    private void Scan(string name)
    {
        string? folder;
        lock (_gate)
        {
            if (_disposed || !_entries.TryGetValue(name, out var entry))
            {
                return;
            }

            folder = entry.Folder;
        }

        IReadOnlyList<string> found;
        try
        {
            found = PlaylistScanner.Scan(_fileSystem, folder);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Nao foi possivel ler a pasta da lista {Playlist}", name);
            return;
        }

        var changed = false;
        lock (_gate)
        {
            if (_disposed || !_entries.TryGetValue(name, out var entry) || entry.Folder != folder)
            {
                return;
            }

            if (!entry.Tracks.SequenceEqual(found, StringComparer.Ordinal))
            {
                entry.Tracks = found;
                changed = true;
            }
        }

        if (changed)
        {
            _logger.LogInformation("Lista {Playlist}: {Count} musicas", name, found.Count);
            Changed?.Invoke(name);
        }
    }

    private sealed class Entry(string folder)
    {
        public string Folder { get; } = folder;

        public IReadOnlyList<string> Tracks { get; set; } = [];

        public IFolderWatcher? Watcher { get; set; }

        public ITimer? Timer { get; set; }
    }
}
