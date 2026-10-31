using AlertaIBT.Core.Settings;

namespace AlertaIBT.Core.Tests.Holyrics;

// What the owner types in the address box is cleaned before it is used.
public sealed class HolyricsHostEntryTests
{
    [Theory]
    [InlineData("127.0.0.1", "127.0.0.1", null)]
    [InlineData("  127.0.0.1  ", "127.0.0.1", null)]
    [InlineData("http://127.0.0.1", "127.0.0.1", null)]
    [InlineData("HTTP://127.0.0.1/", "127.0.0.1", null)]
    [InlineData("https://192.168.0.20:8091/api/", "192.168.0.20", 8091)]
    [InlineData("127.0.0.1:8091", "127.0.0.1", 8091)]
    [InlineData("localhost:9000", "localhost", 9000)]
    [InlineData("localhost.", "localhost", null)]
    [InlineData("pc-igreja.local/", "pc-igreja.local", null)]
    [InlineData("[::1]:8091", "::1", 8091)]
    [InlineData("[::1]", "::1", null)]
    [InlineData("::1", "::1", null)]
    public void A_usable_address_is_cleaned_and_a_port_inside_it_is_taken(string typed, string host, int? port)
    {
        var entry = HolyricsSettings.ParseHostEntry(typed);

        Assert.True(entry.Valid, entry.Hint);
        Assert.Equal(host, entry.Host);
        Assert.Equal(port, entry.Port);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("127.0.0.1 8091")]
    [InlineData("my pc")]
    [InlineData("127.0.0.1:")]
    [InlineData("127.0.0.1:99999")]
    [InlineData("127.0.0.1:80a")]
    [InlineData("a:b:c")]
    [InlineData("http://")]
    [InlineData("[::1")]
    [InlineData("user@host")]
    public void Anything_ambiguous_is_refused_with_a_reason(string typed)
    {
        var entry = HolyricsSettings.ParseHostEntry(typed);

        Assert.False(entry.Valid);
        Assert.False(string.IsNullOrEmpty(entry.Hint));
    }

    [Fact]
    public void A_clean_address_has_no_note_and_a_fixed_one_says_what_was_done()
    {
        Assert.Null(HolyricsSettings.ParseHostEntry("127.0.0.1").Hint);
        Assert.Contains("8091", HolyricsSettings.ParseHostEntry("127.0.0.1:8091").Hint, StringComparison.Ordinal);
        Assert.NotNull(HolyricsSettings.ParseHostEntry("http://127.0.0.1").Hint);
        Assert.NotNull(HolyricsSettings.ParseHostEntry("localhost.").Hint);
    }
}
