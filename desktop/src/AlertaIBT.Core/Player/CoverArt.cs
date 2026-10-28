using TagLib;

namespace AlertaIBT.Core.Player;

/// <summary>Finds the cover picture of a track.</summary>
public interface ICoverArtReader
{
    /// <summary>The bytes of the cover (a JPEG or PNG file as it is stored), or <c>null</c> when the track has none. Never throws.</summary>
    byte[]? Read(string trackPath);
}

/// <summary>
/// The cover of a track: the picture embedded in the file (FLAC picture block, MP3 ID3 APIC, M4A covr; the "front cover" one when there are
/// several) and, when there is none, <c>cover</c>, <c>folder</c> or <c>front</c> (.jpg, .jpeg, .png) in the folder of the track. It reads only
/// the tags, never the audio, and never throws: a broken file just has no cover.
/// </summary>
public sealed class CoverArtReader : ICoverArtReader
{
    /// <summary>The biggest picture taken (a cover is a few hundred KB; this keeps a damaged tag from eating the memory).</summary>
    public const int MaxBytes = 16 * 1024 * 1024;

    private static readonly string[] FolderNames = ["cover", "folder", "front"];
    private static readonly string[] FolderExtensions = [".jpg", ".jpeg", ".png"];

    /// <inheritdoc />
    public byte[]? Read(string trackPath)
    {
        if (string.IsNullOrWhiteSpace(trackPath))
        {
            return null;
        }

        return Embedded(trackPath) ?? FromFolder(trackPath);
    }

    private static byte[]? Embedded(string path)
    {
        try
        {
            using var file = TagLib.File.Create(path, ReadStyle.None);
            var pictures = file.Tag.Pictures.Where(p => p.Data is { Count: > 0 and <= MaxBytes }).ToList();
            var chosen = pictures.FirstOrDefault(p => p.Type == PictureType.FrontCover)
                ?? pictures.FirstOrDefault(p => p.Type is not (PictureType.BackCover or PictureType.FileIcon or PictureType.OtherFileIcon))
                ?? pictures.FirstOrDefault();
            return chosen?.Data.Data;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or UnsupportedFormatException or CorruptFileException or ArgumentException or NotSupportedException or InvalidOperationException)
        {
            return null;
        }
    }

    private static byte[]? FromFolder(string trackPath)
    {
        try
        {
            var folder = Path.GetDirectoryName(trackPath);
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            {
                return null;
            }

            var files = Directory.EnumerateFiles(folder).ToList();
            foreach (var name in FolderNames)
            {
                foreach (var extension in FolderExtensions)
                {
                    var found = files.FirstOrDefault(f => string.Equals(Path.GetFileName(f), name + extension, StringComparison.OrdinalIgnoreCase));
                    if (found is not null && new FileInfo(found).Length is > 0 and <= MaxBytes)
                    {
                        return System.IO.File.ReadAllBytes(found);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An unreadable folder has no cover.
        }

        return null;
    }
}
