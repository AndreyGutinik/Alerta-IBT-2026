using AlertaIBT.Core.Player;
using AlertaIBT.Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AlertaIBT.Core.Loudness;

/// <summary>
/// Puts the loudness pieces together for the app: the cache file, the background scanner and the tag writer, fed by the playlists. It scans the files
/// of the playlist in use first and then the others, pauses while the user edits the playlists, and stops (writing the cache) when the app closes.
/// </summary>
public sealed class LoudnessService : IDisposable
{
    private readonly SettingsService _settings;
    private readonly PlaylistLibrary _library;
    private readonly LoudnessCache _cache;
    private readonly ILogger _logger;
    private bool _started;
    private bool _disposed;
    private IReadOnlyList<PlaylistEntry>? _lastPlaylists;

    /// <param name="settings">The settings (the playlists and the switch "Gravar etiquetas nos arquivos").</param>
    /// <param name="library">The playlists.</param>
    /// <param name="backend">Decodes the files for the measurement.</param>
    /// <param name="cachePath">The cache file; <c>%AppData%\AlertaIBT\loudness-cache.json</c> by default.</param>
    /// <param name="writer">Writes the tags; the FLAC writer by default.</param>
    /// <param name="clock">Times the scan.</param>
    /// <param name="logger">Receives what happens.</param>
    /// <param name="options">The scanner tuning.</param>
    public LoudnessService(
        SettingsService settings,
        PlaylistLibrary library,
        IPlayerBackend backend,
        string? cachePath = null,
        ILoudnessTagWriter? writer = null,
        TimeProvider? clock = null,
        ILogger? logger = null,
        ScannerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(backend);

        _settings = settings;
        _library = library;
        _logger = logger ?? NullLogger.Instance;
        _cache = new LoudnessCache(cachePath ?? LoudnessCache.DefaultPath, clock, _logger);
        Scanner = new LoudnessScanner(
            _cache,
            new BackendLoudnessMeasurer(backend),
            writer ?? new FlacLoudnessTagWriter(),
            () => _settings.Current.Player.WriteLoudnessTags,
            path => IsInUse(path),
            clock,
            _logger,
            options);
    }

    /// <summary>The scanner: the player reads the measurements from it and the screen shows its progress.</summary>
    public LoudnessScanner Scanner { get; }

    /// <summary>Tells whether the player holds a file now (set once the player exists); a file in use never gets its tags written.</summary>
    public Func<string, bool> IsInUse { get; set; } = _ => false;

    /// <summary>Reads the cache and starts scanning the playlists in the background.</summary>
    public void Start()
    {
        lock (this)
        {
            if (_started || _disposed)
            {
                return;
            }

            _started = true;
        }

        _cache.Load();
        _library.Changed += OnLibraryChanged;
        _settings.Changed += OnSettingsChanged;
        Reconfigure();
        Scanner.Start();
    }

    /// <summary>Stops the background work and writes the cache.</summary>
    public void Dispose()
    {
        lock (this)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _library.Changed -= OnLibraryChanged;
        _settings.Changed -= OnSettingsChanged;
        Scanner.Dispose();
    }

    private void OnLibraryChanged(string name) => Reconfigure();

    private void OnSettingsChanged(AppSettings settings)
    {
        // The playlists were edited (added, renamed, another folder, another list in use): wait for the user to finish before scanning.
        var playlists = settings.Player.Playlists;
        if (_lastPlaylists is null || !playlists.SequenceEqual(_lastPlaylists) || !string.Equals(_current, settings.Player.CurrentPlaylist, StringComparison.Ordinal))
        {
            Scanner.NotifyUserActivity();
            Reconfigure();
        }
        else if (settings.Player.WriteLoudnessTags)
        {
            Scanner.Wake();
        }
    }

    private string? _current;

    private void Reconfigure()
    {
        try
        {
            var player = _settings.Current.Player;
            _lastPlaylists = player.Playlists;
            _current = player.CurrentPlaylist;
            var primary = _library.Tracks(player.CurrentPlaylist);
            var others = player.Playlists
                .Where(p => !string.Equals(p.Name, player.CurrentPlaylist, StringComparison.OrdinalIgnoreCase))
                .SelectMany(p => _library.Tracks(p.Name))
                .ToList();
            Scanner.Configure(primary, others);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Nao foi possivel preparar a medicao de volume");
        }
    }
}
