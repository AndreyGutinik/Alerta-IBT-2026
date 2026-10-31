namespace AlertaIBT.Core.Control;

/// <summary>One ArtDmx packet: the levels of up to 512 channels of one universe.</summary>
/// <param name="Sequence">The sequence number (1 to 255, wrapping; 0 means the sender does not number its packets).</param>
/// <param name="Physical">The physical input port the data came from (informative).</param>
/// <param name="Universe">The 15-bit port-address: <c>Net &lt;&lt; 8 | SubUni</c>, 0 to 32767 (what the wire carries; consoles that count from 1 show it plus one).</param>
/// <param name="Data">The channel values; <c>Data[0]</c> is channel 1. An even count from 2 to 512.</param>
public sealed record ArtDmxPacket(byte Sequence, byte Physical, int Universe, byte[] Data);

/// <summary>Reads Art-Net packets. It only understands ArtDmx and never throws: a packet that is not a valid ArtDmx is just "no".</summary>
public static class ArtNetParser
{
    /// <summary>The first 8 bytes of every Art-Net packet: <c>Art-Net</c> and a zero.</summary>
    public static ReadOnlySpan<byte> Header => "Art-Net\0"u8;

    /// <summary>The OpCode of ArtDmx (<c>0x5000</c>, sent little-endian on the wire).</summary>
    public const ushort OpDmx = 0x5000;

    /// <summary>The OpCode of ArtPoll (recognized only to be ignored).</summary>
    public const ushort OpPoll = 0x2000;

    /// <summary>The lowest protocol version accepted (14, the current one).</summary>
    public const int MinProtocolVersion = 14;

    private const int HeaderSize = 18;

    /// <summary>The OpCode of a packet, or <c>null</c> when it is not Art-Net at all (too short, wrong header).</summary>
    public static ushort? ReadOpCode(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 10 || !packet[..8].SequenceEqual(Header))
        {
            return null;
        }

        return (ushort)(packet[8] | (packet[9] << 8));
    }

    /// <summary>Parses an ArtDmx packet. Returns false (and a <c>null</c> packet) for anything else: garbage, another OpCode, an old protocol, a truncated or odd-length payload.</summary>
    public static bool TryParseDmx(ReadOnlySpan<byte> packet, out ArtDmxPacket? parsed)
    {
        parsed = null;
        try
        {
            if (ReadOpCode(packet) != OpDmx || packet.Length < HeaderSize + 2)
            {
                return false;
            }

            var version = (packet[10] << 8) | packet[11];
            if (version < MinProtocolVersion)
            {
                return false;
            }

            var length = (packet[16] << 8) | packet[17];
            if (length is < 2 or > 512 || (length & 1) != 0 || packet.Length < HeaderSize + length)
            {
                return false;
            }

            var universe = ((packet[15] & 0x7F) << 8) | packet[14];
            parsed = new ArtDmxPacket(packet[12], packet[13], universe, packet.Slice(HeaderSize, length).ToArray());
            return true;
        }
        catch (Exception)
        {
            parsed = null;
            return false;
        }
    }

    /// <summary>Builds an ArtDmx packet (used by tests and the simulator; the app itself never sends one).</summary>
    /// <param name="universe">The 15-bit port-address.</param>
    /// <param name="data">The channel values (padded to an even count of at least 2).</param>
    /// <param name="sequence">The sequence byte.</param>
    public static byte[] BuildDmx(int universe, ReadOnlySpan<byte> data, byte sequence = 0)
    {
        var length = Math.Clamp(data.Length, 2, 512);
        if ((length & 1) != 0)
        {
            length++;
        }

        var packet = new byte[HeaderSize + length];
        Header.CopyTo(packet);
        packet[8] = (byte)(OpDmx & 0xFF);
        packet[9] = (byte)(OpDmx >> 8);
        packet[10] = 0;
        packet[11] = MinProtocolVersion;
        packet[12] = sequence;
        packet[14] = (byte)(universe & 0xFF);
        packet[15] = (byte)((universe >> 8) & 0x7F);
        packet[16] = (byte)(length >> 8);
        packet[17] = (byte)(length & 0xFF);
        data[..Math.Min(data.Length, length)].CopyTo(packet.AsSpan(HeaderSize));
        return packet;
    }
}
