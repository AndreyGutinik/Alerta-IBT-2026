using AlertaIBT.Core.Control;

namespace AlertaIBT.Core.Tests.Control;

// ArtDmx parsing: only a well formed ArtDmx is a packet; everything else is "no", never an exception.
public sealed class ArtNetParserTests
{
    private static byte[] Valid(int universe = 99, int length = 512, byte sequence = 7)
    {
        var data = new byte[length];
        for (var i = 0; i < length; i++)
        {
            data[i] = (byte)(i % 256);
        }

        return ArtNetParser.BuildDmx(universe, data, sequence);
    }

    [Fact]
    public void A_valid_packet_is_read_with_its_fields()
    {
        var ok = ArtNetParser.TryParseDmx(Valid(universe: 99, length: 24, sequence: 9), out var p);

        Assert.True(ok);
        Assert.Equal(99, p!.Universe);
        Assert.Equal(9, p.Sequence);
        Assert.Equal(24, p.Data.Length);
        Assert.Equal(5, p.Data[5]);
    }

    [Fact]
    public void The_universe_is_net_shifted_by_eight_plus_subuni()
    {
        var packet = Valid(universe: (3 << 8) | 0x2A);

        Assert.True(ArtNetParser.TryParseDmx(packet, out var p));
        Assert.Equal(0x32A, p!.Universe);
        Assert.Equal(0x2A, packet[14]);
        Assert.Equal(3, packet[15]);
    }

    [Fact]
    public void The_largest_universe_uses_fifteen_bits()
    {
        Assert.True(ArtNetParser.TryParseDmx(Valid(universe: 32767), out var p));
        Assert.Equal(32767, p!.Universe);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(9)]
    [InlineData(17)]
    public void A_truncated_header_is_not_a_packet(int size)
    {
        Assert.False(ArtNetParser.TryParseDmx(Valid().AsSpan(0, size), out var p));
        Assert.Null(p);
    }

    [Fact]
    public void A_payload_shorter_than_its_declared_length_is_not_a_packet()
    {
        var packet = Valid(length: 100);

        Assert.False(ArtNetParser.TryParseDmx(packet.AsSpan(0, packet.Length - 2), out _));
    }

    [Fact]
    public void A_wrong_header_is_not_a_packet()
    {
        var packet = Valid();
        packet[0] = (byte)'X';

        Assert.False(ArtNetParser.TryParseDmx(packet, out _));
        Assert.Null(ArtNetParser.ReadOpCode(packet));
    }

    [Fact]
    public void Another_opcode_is_ignored()
    {
        var packet = Valid();
        packet[8] = 0x00;
        packet[9] = 0x20; // ArtPoll

        Assert.False(ArtNetParser.TryParseDmx(packet, out _));
        Assert.Equal(ArtNetParser.OpPoll, ArtNetParser.ReadOpCode(packet));
    }

    [Theory]
    [InlineData(0, 3)]
    [InlineData(2, 2)]
    [InlineData(0, 0)]
    [InlineData(0, 1)]
    public void An_odd_or_out_of_range_length_is_rejected(int hi, int lo)
    {
        var packet = Valid(length: 512);
        packet[16] = (byte)hi;
        packet[17] = (byte)lo;

        Assert.False(ArtNetParser.TryParseDmx(packet, out _));
    }

    [Fact]
    public void An_even_length_inside_the_range_is_accepted()
    {
        var packet = Valid(length: 512);
        packet[16] = 1;
        packet[17] = 0; // 256

        Assert.True(ArtNetParser.TryParseDmx(packet, out var p));
        Assert.Equal(256, p!.Data.Length);
    }

    [Fact]
    public void An_old_protocol_version_is_rejected()
    {
        var packet = Valid();
        packet[11] = 13;

        Assert.False(ArtNetParser.TryParseDmx(packet, out _));
    }

    [Fact]
    public void Garbage_never_throws()
    {
        var random = new Random(1234);
        for (var i = 0; i < 2000; i++)
        {
            var bytes = new byte[random.Next(0, 700)];
            random.NextBytes(bytes);
            if (bytes.Length > 9 && i % 2 == 0)
            {
                ArtNetParser.Header.CopyTo(bytes); // a real header over random content
                bytes[8] = 0x00;
                bytes[9] = 0x50;
            }

            var exception = Record.Exception(() => ArtNetParser.TryParseDmx(bytes, out _));
            Assert.Null(exception);
        }
    }

    [Fact]
    public void Build_pads_to_an_even_length_of_at_least_two()
    {
        Assert.Equal(2, ParsedLength([5]));
        Assert.Equal(4, ParsedLength([1, 2, 3]));
        Assert.Equal(512, ParsedLength(new byte[600]));
    }

    private static int ParsedLength(byte[] data)
    {
        Assert.True(ArtNetParser.TryParseDmx(ArtNetParser.BuildDmx(1, data), out var p));
        return p!.Data.Length;
    }
}
