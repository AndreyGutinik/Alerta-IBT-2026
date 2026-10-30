using System.Globalization;
using System.Text.RegularExpressions;

namespace AlertaIBT.Core.Backup;

/// <summary>Files of the backup: the atomic write, the names of the automatic copies and their rotation. Touches only files of its own pattern.</summary>
public static partial class BackupFiles
{
    /// <summary>How many automatic copies are kept.</summary>
    public const int KeepCopies = 5;

    /// <summary>Writes <paramref name="bytes"/> to a temporary file in the same folder and moves it over <paramref name="path"/> (never a half file).</summary>
    /// <exception cref="IOException">The folder or the file cannot be written.</exception>
    public static void WriteAtomic(string path, byte[] bytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(bytes);

        var folder = Path.GetDirectoryName(Path.GetFullPath(path)) ?? throw new IOException("Pasta inv\U000000E1lida.");
        var temp = Path.Combine(folder, ".ibt-backup-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            throw new IOException(ex.Message, ex);
        }
        finally
        {
            TryDelete(temp);
        }
    }

    /// <summary>The name of an automatic copy: <c>ibt-backup-yyyyMMdd-HHmmss.ibtbackup</c> (local time).</summary>
    public static string AutoFileName(DateTime localTime) =>
        "ibt-backup-" + localTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + BackupCrypto.Extension;

    /// <summary>Whether <paramref name="fileName"/> (no folder) is exactly the name of an automatic copy.</summary>
    public static bool IsAutoCopyName(string fileName) => AutoName().IsMatch(fileName);

    /// <summary>The automatic copies in <paramref name="folder"/>, newest first (their names carry the time). Other files are never listed.</summary>
    public static IReadOnlyList<string> ListAutoCopies(string folder) =>
        Directory.EnumerateFiles(folder)
            .Where(path => IsAutoCopyName(Path.GetFileName(path)))
            .OrderByDescending(path => Path.GetFileName(path), StringComparer.Ordinal)
            .ToList();

    /// <summary>Deletes the automatic copies beyond the newest <paramref name="keep"/>. A file that cannot be deleted is skipped.</summary>
    /// <returns>How many were deleted.</returns>
    public static int Rotate(string folder, int keep = KeepCopies)
    {
        var deleted = 0;
        foreach (var path in ListAutoCopies(folder).Skip(Math.Max(1, keep)))
        {
            if (TryDelete(path))
            {
                deleted++;
            }
        }

        return deleted;
    }

    private static bool TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left behind: the next rotation tries again.
        }

        return false;
    }

    [GeneratedRegex(@"^ibt-backup-\d{8}-\d{6}\.ibtbackup$", RegexOptions.CultureInvariant)]
    private static partial Regex AutoName();
}
