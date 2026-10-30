using System.Text;

namespace AlertaIBT.Core.Loudness;

/// <summary>One metadata block of a FLAC file: its type and its bytes exactly as they are in the file (without the 4-byte header).</summary>
/// <param name="Type">0 STREAMINFO, 1 PADDING, 2 APPLICATION, 3 SEEKTABLE, 4 VORBIS_COMMENT, 5 CUESHEET, 6 PICTURE.</param>
/// <param name="Data">The payload, or <c>null</c> when it was not kept (only its <paramref name="Length"/> is known).</param>
/// <param name="Length">The payload length.</param>
internal sealed record FlacBlock(int Type, byte[]? Data, int Length)
{
    public const int StreamInfo = 0;
    public const int Padding = 1;
    public const int VorbisComment = 4;
    public const int Picture = 6;
}

/// <summary>
/// The layout of a FLAC file: what comes before the first audio frame (an optional ID3v2 tag, the "fLaC" marker and the metadata blocks) and where the
/// audio frames start. Reading it never touches the frames. This is all the tag writer needs, and it keeps every block it does not change byte for byte.
/// </summary>
internal sealed class FlacLayout
{
    /// <summary>The bytes before the "fLaC" marker (an ID3v2 tag some files carry), kept as they are.</summary>
    public required byte[] Prefix { get; init; }

    /// <summary>The metadata blocks in file order.</summary>
    public required IReadOnlyList<FlacBlock> Blocks { get; init; }

    /// <summary>Where the first audio frame starts.</summary>
    public required long FramesOffset { get; init; }

    /// <summary>The length of the file.</summary>
    public required long FileLength { get; init; }

    /// <summary>The 16 bytes of the audio MD5 in STREAMINFO, as hex; all zeros when the encoder did not store it.</summary>
    public string? StreamInfoMd5
    {
        get
        {
            var info = Blocks.FirstOrDefault(b => b.Type == FlacBlock.StreamInfo);
            return info?.Data is { Length: 34 } data ? Convert.ToHexString(data.AsSpan(18, 16)).ToLowerInvariant() : null;
        }
    }

    /// <summary>Reads the layout of the FLAC at <paramref name="stream"/>; throws <see cref="InvalidDataException"/> when it is not a well formed FLAC header.</summary>
    /// <param name="stream">A seekable stream at any position.</param>
    /// <param name="keepData">Which block types keep their bytes in memory (the others are skipped); all by default.</param>
    public static FlacLayout Read(Stream stream, Func<int, bool>? keepData = null)
    {
        stream.Seek(0, SeekOrigin.Begin);
        var length = stream.Length;
        var prefix = Array.Empty<byte>();
        var head = new byte[10];
        var got = ReadAll(stream, head, 10);
        if (got == 10 && head[0] == 'I' && head[1] == 'D' && head[2] == '3')
        {
            var size = (head[6] << 21) | (head[7] << 14) | (head[8] << 7) | head[9];
            var footer = (head[5] & 0x10) != 0 ? 10 : 0;
            var total = 10 + size + footer;
            if (total > length - 4)
            {
                throw new InvalidDataException("ID3v2 maior que o arquivo");
            }

            stream.Seek(0, SeekOrigin.Begin);
            prefix = new byte[total];
            ReadExactly(stream, prefix);
        }
        else
        {
            stream.Seek(0, SeekOrigin.Begin);
        }

        var marker = new byte[4];
        ReadExactly(stream, marker);
        if (marker[0] != 'f' || marker[1] != 'L' || marker[2] != 'a' || marker[3] != 'C')
        {
            throw new InvalidDataException("nao e um FLAC");
        }

        var blocks = new List<FlacBlock>();
        var header = new byte[4];
        while (true)
        {
            ReadExactly(stream, header);
            var last = (header[0] & 0x80) != 0;
            var type = header[0] & 0x7F;
            var size = (header[1] << 16) | (header[2] << 8) | header[3];
            if (type == 127 || stream.Position + size > length)
            {
                throw new InvalidDataException("bloco de metadados invalido");
            }

            if (keepData is null || keepData(type))
            {
                var data = new byte[size];
                ReadExactly(stream, data);
                blocks.Add(new FlacBlock(type, data, size));
            }
            else
            {
                stream.Seek(size, SeekOrigin.Current);
                blocks.Add(new FlacBlock(type, null, size));
            }

            if (last)
            {
                break;
            }
        }

        if (blocks.Count == 0 || blocks[0].Type != FlacBlock.StreamInfo || blocks[0].Length != 34)
        {
            throw new InvalidDataException("STREAMINFO ausente");
        }

        return new FlacLayout { Prefix = prefix, Blocks = blocks, FramesOffset = stream.Position, FileLength = length };
    }

    internal static int ReadAll(Stream stream, byte[] buffer, int count)
    {
        var total = 0;
        while (total < count)
        {
            var n = stream.Read(buffer, total, count - total);
            if (n <= 0)
            {
                break;
            }

            total += n;
        }

        return total;
    }

    private static void ReadExactly(Stream stream, byte[] buffer)
    {
        if (ReadAll(stream, buffer, buffer.Length) != buffer.Length)
        {
            throw new InvalidDataException("arquivo cortado");
        }
    }
}

/// <summary>A Vorbis comment block taken apart without changing a byte: the vendor and each entry as raw bytes.</summary>
internal sealed class VorbisComments
{
    public required byte[] Vendor { get; init; }

    public required List<byte[]> Entries { get; init; }

    /// <summary>Parses the payload of a VORBIS_COMMENT block; throws <see cref="InvalidDataException"/> when it does not add up.</summary>
    public static VorbisComments Parse(byte[] data)
    {
        var pos = 0;
        var vendorLength = ReadInt(data, ref pos);
        if (vendorLength < 0 || pos + vendorLength > data.Length)
        {
            throw new InvalidDataException("fabricante invalido");
        }

        var vendor = data.AsSpan(pos, vendorLength).ToArray();
        pos += vendorLength;
        var count = ReadInt(data, ref pos);
        if (count < 0 || count > 1_000_000)
        {
            throw new InvalidDataException("contagem invalida");
        }

        var entries = new List<byte[]>(count);
        for (var i = 0; i < count; i++)
        {
            var length = ReadInt(data, ref pos);
            if (length < 0 || pos + length > data.Length)
            {
                throw new InvalidDataException("comentario invalido");
            }

            entries.Add(data.AsSpan(pos, length).ToArray());
            pos += length;
        }

        if (pos != data.Length)
        {
            throw new InvalidDataException("sobra de bytes no bloco de comentarios");
        }

        return new VorbisComments { Vendor = vendor, Entries = entries };
    }

    /// <summary>The bytes of the block payload.</summary>
    public byte[] Serialize()
    {
        using var ms = new MemoryStream();
        WriteInt(ms, Vendor.Length);
        ms.Write(Vendor);
        WriteInt(ms, Entries.Count);
        foreach (var entry in Entries)
        {
            WriteInt(ms, entry.Length);
            ms.Write(entry);
        }

        return ms.ToArray();
    }

    /// <summary>The field name of a raw entry (the text before the first "="), upper-cased; empty when there is none.</summary>
    public static string NameOf(byte[] entry)
    {
        var eq = Array.IndexOf(entry, (byte)'=');
        return eq < 0 ? string.Empty : Encoding.UTF8.GetString(entry, 0, eq).ToUpperInvariant();
    }

    /// <summary>The text of the first field called <paramref name="name"/> (case does not matter), or <c>null</c>.</summary>
    public string? Get(string name)
    {
        foreach (var entry in Entries)
        {
            if (NameOf(entry) == name.ToUpperInvariant())
            {
                var eq = Array.IndexOf(entry, (byte)'=');
                return Encoding.UTF8.GetString(entry, eq + 1, entry.Length - eq - 1);
            }
        }

        return null;
    }

    private static int ReadInt(byte[] data, ref int pos)
    {
        if (pos + 4 > data.Length)
        {
            throw new InvalidDataException("bloco de comentarios cortado");
        }

        var value = data[pos] | (data[pos + 1] << 8) | (data[pos + 2] << 16) | (data[pos + 3] << 24);
        pos += 4;
        return value;
    }

    private static void WriteInt(Stream stream, int value)
    {
        stream.WriteByte((byte)value);
        stream.WriteByte((byte)(value >> 8));
        stream.WriteByte((byte)(value >> 16));
        stream.WriteByte((byte)(value >> 24));
    }
}
