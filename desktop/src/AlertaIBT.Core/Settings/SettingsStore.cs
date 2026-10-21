using System.Globalization;
using System.Text;

namespace AlertaIBT.Core.Settings;

/// <summary>What <see cref="SettingsStore.Load"/> found.</summary>
/// <param name="Settings">The settings to use: what the file held, or the defaults.</param>
/// <param name="WasCorrupt">Whether the file existed but could not be used.</param>
/// <param name="BackupPath">Where the unusable file was kept (<c>settings.bad-*.json</c>), when it could be.</param>
public sealed record SettingsLoadResult(AppSettings Settings, bool WasCorrupt, string? BackupPath);

/// <summary>
/// <c>settings.json</c> on disk. Loading never throws and never loses a damaged file: it is moved aside as
/// <c>settings.bad-yyyyMMdd-HHmmss-fff.json</c> (the five newest are kept) and the defaults are used. Saving is atomic:
/// the text goes to a temporary file that then replaces the real one, so a crash or a power cut leaves the old file or the
/// new one, never half of each.
/// </summary>
public sealed class SettingsStore
{
    private const int BadCopiesKept = 5;
    private const int ReadAttempts = 3;

    private readonly object _gate = new();
    private readonly string _path;
    private readonly TimeProvider _clock;

    /// <param name="path">The settings file; its folder is created on the first save.</param>
    /// <param name="clock">Dates the bad copies; the system clock by default.</param>
    public SettingsStore(string path, TimeProvider? clock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        _path = path;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary><c>%AppData%\AlertaIBT\settings.json</c>.</summary>
    public static string DefaultPath { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AlertaIBT", "settings.json");

    /// <summary>The settings file path.</summary>
    public string FilePath => _path;

    /// <summary>
    /// Reads the settings. No file gives the defaults. A file that is not JSON (empty, cut, zero bytes) or cannot be
    /// read gives the defaults with <see cref="SettingsLoadResult.WasCorrupt"/> set, and is moved aside first when it can be.
    /// </summary>
    public SettingsLoadResult Load()
    {
        lock (_gate)
        {
            if (!File.Exists(_path))
            {
                return new SettingsLoadResult(new AppSettings(), false, null);
            }

            if (!TryRead(out var text))
            {
                return new SettingsLoadResult(new AppSettings(), true, null);
            }

            var (settings, wasCorrupt) = SettingsJson.Load(text);
            return wasCorrupt
                ? new SettingsLoadResult(settings, true, MoveAside())
                : new SettingsLoadResult(settings, false, null);
        }
    }

    /// <summary>Writes the settings (held in range) atomically.</summary>
    /// <exception cref="IOException">The file could not be written; the old file is intact.</exception>
    /// <exception cref="UnauthorizedAccessException">The folder or file is not writable; the old file is intact.</exception>
    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(SettingsJson.Save(settings));
        lock (_gate)
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(_path))!;
            Directory.CreateDirectory(directory);

            var temp = _path + ".tmp";
            try
            {
                using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    stream.Write(bytes);
                    stream.Flush(flushToDisk: true);
                }

                if (File.Exists(_path))
                {
                    File.Replace(temp, _path, destinationBackupFileName: null, ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(temp, _path);
                }
            }
            catch
            {
                TryDelete(temp);
                throw;
            }
        }
    }

    // The file's text. The file may be held for a moment by a scanner or a backup tool, so a few tries are made.
    private bool TryRead(out string text)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
                text = reader.ReadToEnd();
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt >= ReadAttempts)
                {
                    text = string.Empty;
                    return false;
                }

                Thread.Sleep(30);
            }
        }
    }

    // Moves the unusable file to a dated name, so nothing the user typed is lost and the next start is clean.
    private string? MoveAside()
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(_path))!;
        var stem = Path.GetFileNameWithoutExtension(_path);
        var stamp = _clock.GetLocalNow().ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);

        var target = Path.Combine(directory, $"{stem}.bad-{stamp}.json");
        for (var n = 1; File.Exists(target); n++)
        {
            target = Path.Combine(directory, $"{stem}.bad-{stamp}-{n}.json");
        }

        try
        {
            File.Move(_path, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try
            {
                File.Copy(_path, target);
            }
            catch (Exception copyFailure) when (copyFailure is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        PruneBadCopies(directory, stem);
        return target;
    }

    private static void PruneBadCopies(string directory, string stem)
    {
        try
        {
            var old = Directory.GetFiles(directory, $"{stem}.bad-*.json")
                .OrderByDescending(file => file, StringComparer.Ordinal)
                .Skip(BadCopiesKept);
            foreach (var file in old)
            {
                TryDelete(file);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Pruning is housekeeping: a leftover copy is harmless.
        }
    }

    private static void TryDelete(string file)
    {
        try
        {
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A temporary file that cannot be removed is overwritten by the next save.
        }
    }
}
