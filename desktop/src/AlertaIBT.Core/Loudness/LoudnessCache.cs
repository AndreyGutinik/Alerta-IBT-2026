using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AlertaIBT.Core.Loudness;

/// <summary>What was done to the tags of a measured file.</summary>
public enum TagState
{
    /// <summary>Not written yet (a FLAC waiting its turn).</summary>
    Pending,

    /// <summary>Written and checked, or the file already carried this app's own verified tags.</summary>
    Written,

    /// <summary>Nothing to write: not a FLAC, or the switch was off when it was measured.</summary>
    NotApplicable,

    /// <summary>A write failed or the check did not pass: never tried again for this version of the file.</summary>
    DoNotRetry,
}

/// <summary>How a measurement ended.</summary>
public enum MeasureStatus
{
    /// <summary>The loudness is known.</summary>
    Measured,

    /// <summary>The file is silent or too short to have a loudness: it plays at 0 dB.</summary>
    Silent,

    /// <summary>The file could not be decoded: it plays at 0 dB and is not measured again until it changes.</summary>
    Failed,
}

/// <summary>One file of the cache. It is only valid while the file still has this size and last-write time and the analysis version is the app's.</summary>
public sealed record LoudnessEntry
{
    /// <summary>The file path.</summary>
    public required string Path { get; init; }

    /// <summary>The file size when it was measured.</summary>
    public long Size { get; init; }

    /// <summary>The last-write time of the file (UTC ticks) when it was measured.</summary>
    public long WriteTicks { get; init; }

    /// <summary>The analysis version that measured it.</summary>
    public int Version { get; init; }

    /// <summary>How the measurement ended.</summary>
    public MeasureStatus Status { get; init; }

    /// <summary>Integrated loudness (LUFS) when <see cref="Status"/> is <see cref="MeasureStatus.Measured"/>.</summary>
    public double? Lufs { get; init; }

    /// <summary>The highest sample, linear.</summary>
    public double SamplePeak { get; init; }

    /// <summary>The 4x oversampled peak, linear.</summary>
    public double TruePeak { get; init; }

    /// <summary>The state of the tag writing.</summary>
    public TagState Tags { get; init; }

    /// <summary>Why the tags were not written (for the diagnostics), when it is known.</summary>
    public string? Note { get; init; }

    /// <summary>The loudness for the player, or <c>null</c> when the file has none.</summary>
    public TrackLoudness? ToLoudness() =>
        Status == MeasureStatus.Measured && Lufs is { } lufs ? new TrackLoudness(lufs, Math.Max(TruePeak, SamplePeak)) : null;
}

/// <summary>
/// The measurements, kept in <c>%AppData%\AlertaIBT\loudness-cache.json</c>. The key of a file is its path plus its size, its last-write time and the
/// analysis version: a file that changed, or an app that measures differently, is measured again. The file is written atomically (a copy, then a swap)
/// and a damaged one is set aside and rebuilt empty: the cost is only measuring again.
/// </summary>
public sealed class LoudnessCache
{
    /// <summary>The version of this app's analysis; raising it makes every file be measured again.</summary>
    public const int AnalysisVersion = 1;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    private readonly object _gate = new();
    private readonly Dictionary<string, LoudnessEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _file;
    private readonly TimeProvider _clock;
    private readonly ILogger _logger;
    private bool _dirty;
    private long _lastSave;

    /// <param name="file">The cache file.</param>
    /// <param name="clock">Times the saves.</param>
    /// <param name="logger">Receives what happens.</param>
    public LoudnessCache(string file, TimeProvider? clock = null, ILogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(file);

        _file = file;
        _clock = clock ?? TimeProvider.System;
        _logger = logger ?? NullLogger.Instance;
        _lastSave = _clock.GetTimestamp();
    }

    /// <summary><c>%AppData%\AlertaIBT\loudness-cache.json</c>.</summary>
    public static string DefaultPath { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AlertaIBT", "loudness-cache.json");

    /// <summary>The cache file path.</summary>
    public string FilePath => _file;

    /// <summary>How many entries are held.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>Reads the file. A missing file is an empty cache; a damaged one is moved aside (<c>.bad</c>) and the cache starts empty. Returns whether the file was damaged.</summary>
    public bool Load()
    {
        lock (_gate)
        {
            _entries.Clear();
            _dirty = false;
            if (!File.Exists(_file))
            {
                return false;
            }

            try
            {
                var text = File.ReadAllText(_file);
                var data = JsonSerializer.Deserialize<CacheFile>(text, Json) ?? throw new JsonException("vazio");
                foreach (var entry in data.Entries ?? [])
                {
                    if (!string.IsNullOrWhiteSpace(entry.Path))
                    {
                        _entries[entry.Path] = entry;
                    }
                }

                return false;
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
                _logger.LogWarning(ex, "O cache de volume estava danificado; vou medir de novo");
                _entries.Clear();
                try
                {
                    File.Move(_file, _file + ".bad", overwrite: true);
                }
                catch (Exception moveError) when (moveError is IOException or UnauthorizedAccessException)
                {
                    // Nothing to do: the next save overwrites it.
                }

                return true;
            }
        }
    }

    /// <summary>The entry of a file, but only when it is still valid for the file as it is now (same size, same last-write time, same analysis version). Reads the file's size and time.</summary>
    public LoudnessEntry? Get(string path)
    {
        LoudnessEntry? entry;
        lock (_gate)
        {
            if (!_entries.TryGetValue(path, out entry))
            {
                return null;
            }
        }

        return IsCurrent(entry) ? entry : null;
    }

    /// <summary>Whether <paramref name="entry"/> still describes the file on disk.</summary>
    public static bool IsCurrent(LoudnessEntry entry)
    {
        if (entry.Version != AnalysisVersion)
        {
            return false;
        }

        try
        {
            var info = new FileInfo(entry.Path);
            return info.Exists && info.Length == entry.Size && info.LastWriteTimeUtc.Ticks == entry.WriteTicks;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Stores the entry (it replaces the file's old one).</summary>
    public void Set(LoudnessEntry entry)
    {
        lock (_gate)
        {
            _entries[entry.Path] = entry;
            _dirty = true;
        }
    }

    /// <summary>The measurement of <paramref name="path"/> with the key of the file as it is now (call it right after the measurement; <c>null</c> when the file is gone).</summary>
    public static LoudnessEntry? Stamp(string path, LoudnessEntry template)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? template with { Path = path, Size = info.Length, WriteTicks = info.LastWriteTimeUtc.Ticks, Version = AnalysisVersion } : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Every entry (valid or not), for a screen of statistics and for the tests.</summary>
    public IReadOnlyList<LoudnessEntry> All()
    {
        lock (_gate)
        {
            return _entries.Values.ToList();
        }
    }

    /// <summary>Drops the entries of files that are no longer wanted.</summary>
    public void Prune(IReadOnlySet<string> keep)
    {
        lock (_gate)
        {
            foreach (var path in _entries.Keys.Where(p => !keep.Contains(p)).ToList())
            {
                _entries.Remove(path);
                _dirty = true;
            }
        }
    }

    /// <summary>Saves when something changed and at least <paramref name="minInterval"/> passed since the last save (zero saves now).</summary>
    public void SaveIfDirty(TimeSpan minInterval)
    {
        string text;
        lock (_gate)
        {
            if (!_dirty || _clock.GetElapsedTime(_lastSave) < minInterval)
            {
                return;
            }

            text = JsonSerializer.Serialize(new CacheFile { AnalysisVersion = AnalysisVersion, Entries = _entries.Values.OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase).ToList() }, Json);
            _dirty = false;
            _lastSave = _clock.GetTimestamp();
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            var temp = _file + ".tmp";
            File.WriteAllText(temp, text);
            if (File.Exists(_file))
            {
                File.Replace(temp, _file, null);
            }
            else
            {
                File.Move(temp, _file);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Nao foi possivel gravar o cache de volume");
            lock (_gate)
            {
                _dirty = true;
            }
        }
    }

    private sealed class CacheFile
    {
        public int AnalysisVersion { get; set; }

        public List<LoudnessEntry>? Entries { get; set; }
    }
}
