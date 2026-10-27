using System.Text;
using System.Text.RegularExpressions;

namespace AlertaIBT.Core.Player;

/// <summary>Finds the title and artist of a track.</summary>
public interface ITrackMetadataReader
{
    /// <summary>The info of a file; never throws (the file name stands in for missing tags).</summary>
    TrackInfo Read(string path);
}

/// <summary>Reads FLAC tags (Vorbis comments) and falls back to the file name, which in the church's folder is "Title - Artist.flac".</summary>
public sealed partial class FileTrackMetadataReader : ITrackMetadataReader
{
    private const int MaxCommentBlock = 1 << 20;

    /// <inheritdoc />
    public TrackInfo Read(string path)
    {
        string? title = null;
        string? artist = null;
        try
        {
            if (string.Equals(Path.GetExtension(path), ".flac", StringComparison.OrdinalIgnoreCase))
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                (title, artist) = ReadFlacTags(stream);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An unreadable file has no tags: the file name is used.
        }

        var fromName = FromFileName(path);
        return new TrackInfo(path, Path.GetFileName(path), title ?? fromName.Title, artist ?? fromName.Artist);
    }

    /// <summary>The title and artist from a name such as <c>01 - Amazing Grace - Josh Snodgrass.flac</c>: a leading track number is dropped and the text after the last " - " is the artist.</summary>
    public static (string Title, string? Artist) FromFileName(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path).Trim();
        name = TrackNumber().Replace(name, string.Empty).Trim();
        var cut = name.LastIndexOf(" - ", StringComparison.Ordinal);
        if (cut > 0 && cut + 3 < name.Length)
        {
            return (name[..cut].Trim(), name[(cut + 3)..].Trim());
        }

        return (name.Length > 0 ? name : Path.GetFileName(path), null);
    }

    /// <summary>The TITLE and ARTIST comments of a FLAC stream; <c>null</c> for what is missing or when it is not FLAC.</summary>
    public static (string? Title, string? Artist) ReadFlacTags(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        Span<byte> head = stackalloc byte[4];
        if (stream.Read(head) != 4 || head[0] != (byte)'f' || head[1] != (byte)'L' || head[2] != (byte)'a' || head[3] != (byte)'C')
        {
            return (null, null);
        }

        Span<byte> header = stackalloc byte[4];
        while (true)
        {
            if (stream.Read(header) != 4)
            {
                return (null, null);
            }

            var last = (header[0] & 0x80) != 0;
            var type = header[0] & 0x7F;
            var length = (header[1] << 16) | (header[2] << 8) | header[3];
            if (type == 4)
            {
                if (length > MaxCommentBlock)
                {
                    return (null, null);
                }

                var block = new byte[length];
                return stream.ReadAtLeast(block, length, throwOnEndOfStream: false) == length ? ParseComments(block) : (null, null);
            }

            if (last || stream.Seek(length, SeekOrigin.Current) > stream.Length)
            {
                return (null, null);
            }
        }
    }

    private static (string? Title, string? Artist) ParseComments(byte[] block)
    {
        string? title = null;
        string? artist = null;
        var position = 0;
        var vendor = ReadLength(block, ref position);
        if (vendor < 0 || position + vendor > block.Length)
        {
            return (null, null);
        }

        position += vendor;
        var count = ReadLength(block, ref position);
        for (var i = 0; i < count && position + 4 <= block.Length; i++)
        {
            var length = ReadLength(block, ref position);
            if (length < 0 || position + length > block.Length)
            {
                break;
            }

            var comment = Encoding.UTF8.GetString(block, position, length);
            position += length;
            var equals = comment.IndexOf('=', StringComparison.Ordinal);
            if (equals <= 0)
            {
                continue;
            }

            var key = comment[..equals];
            var value = comment[(equals + 1)..].Trim();
            if (value.Length == 0)
            {
                continue;
            }

            if (title is null && key.Equals("TITLE", StringComparison.OrdinalIgnoreCase))
            {
                title = value;
            }
            else if (artist is null && key.Equals("ARTIST", StringComparison.OrdinalIgnoreCase))
            {
                artist = value;
            }
        }

        return (title, artist);
    }

    // A 32-bit little-endian length; -1 when it does not fit in the block.
    private static int ReadLength(byte[] block, ref int position)
    {
        if (position + 4 > block.Length)
        {
            position = block.Length;
            return -1;
        }

        var value = BitConverter.ToInt32(block, position);
        position += 4;
        return value < 0 ? -1 : value;
    }

    [GeneratedRegex(@"^\d{1,3}\s*[-.]\s+")]
    private static partial Regex TrackNumber();
}
