using AlertaIBT.Core.Control;
using AlertaIBT.Core.Settings;
using AlertaIBT.Core.Tests.Settings;

namespace AlertaIBT.Core.Tests.Holyrics;

public sealed class HolyricsSettingsTests
{
    private static readonly FakeProtector Protector = new();

    [Fact]
    public void The_defaults_are_this_pc_on_8091_with_the_arrows_on_and_no_token()
    {
        var h = new AppSettings().Holyrics;

        Assert.Equal("127.0.0.1", h.Host);
        Assert.Equal(8091, h.Port);
        Assert.Null(h.TokenProtected);
        Assert.True(h.ArrowsEnabled);
    }

    [Fact]
    public void An_older_file_without_the_section_loads_with_the_defaults()
    {
        var (settings, _) = SettingsJson.Load("""{ "bar": { "height": 80 } }""");

        Assert.Equal(new HolyricsSettings(), settings.Holyrics);
    }

    [Fact]
    public void The_section_round_trips()
    {
        var original = new AppSettings
        {
            Holyrics = new HolyricsSettings { Host = "192.168.1.20", Port = 9000, TokenProtected = "AAAA", ArrowsEnabled = false },
        };

        var (loaded, _) = SettingsJson.Load(SettingsJson.Save(original));

        Assert.Equal(original.Holyrics, loaded.Holyrics);
    }

    [Theory]
    [InlineData("http://evil/", "127.0.0.1")]
    [InlineData("a b", "127.0.0.1")]
    [InlineData("host/path", "127.0.0.1")]
    [InlineData("", "127.0.0.1")]
    [InlineData("  10.0.0.5  ", "10.0.0.5")]
    [InlineData("pc-da-igreja", "pc-da-igreja")]
    [InlineData("::1", "::1")]
    public void The_host_must_be_a_name_or_an_ip_address(string typed, string kept)
    {
        var normalized = SettingsJson.Normalize(new AppSettings { Holyrics = new HolyricsSettings { Host = typed } });

        Assert.Equal(kept, normalized.Holyrics.Host);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(70000)]
    public void A_port_out_of_range_goes_back_to_the_default(int port)
    {
        Assert.Equal(8091, SettingsJson.Normalize(new AppSettings { Holyrics = new HolyricsSettings { Port = port } }).Holyrics.Port);
    }

    [Fact]
    public void The_token_is_stored_protected_only_and_never_in_clear_in_the_file()
    {
        const string Token = "CLEAR-TOKEN-1234567";
        var settings = new AppSettings().WithHolyricsToken("  " + Token + "  ", Protector);

        var json = SettingsJson.Save(settings);

        Assert.DoesNotContain(Token, json, StringComparison.Ordinal);
        Assert.Equal(Token, settings.GetHolyricsToken(Protector));
        Assert.Equal(Token, SettingsJson.Load(json).Settings.GetHolyricsToken(Protector));
        Assert.DoesNotContain(Token, settings.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_blank_token_clears_it()
    {
        var settings = new AppSettings().WithHolyricsToken("abc12345", Protector).WithHolyricsToken("   ", Protector);

        Assert.Null(settings.Holyrics.TokenProtected);
        Assert.Null(settings.GetHolyricsToken(Protector));
    }

    // Before this batch the four Holyrics rows were saved off (they could not run). The first load of such a file turns them on; the file then says version 2.
    [Fact]
    public void An_old_map_gets_the_four_holyrics_rows_turned_on_once()
    {
        const string Old = """
            { "control": { "enabled": true, "rows": [
              { "channel": 8, "action": "take", "mode": "button", "enabled": false },
              { "channel": 13, "action": "slideNext", "mode": "button", "enabled": false },
              { "channel": 14, "action": "slidePrevious", "mode": "button", "enabled": false },
              { "channel": 15, "action": "videoPlayPause", "mode": "button", "enabled": false },
              { "channel": 16, "action": "logoToggle", "mode": "button", "enabled": false } ] } }
            """;

        var (settings, _) = SettingsJson.Load(Old);
        var rows = settings.Control.Rows;

        Assert.False(rows[0].Enabled); // a row the owner turned off by choice stays off
        Assert.All(rows.Skip(1), r => Assert.True(r.Enabled));

        // After the save the file is version 2: an owner who turns a Holyrics row off keeps it off.
        var off = settings with { Control = settings.Control with { Rows = rows.Select(r => r.Action == ControlAction.SlideNext ? r with { Enabled = false } : r).ToList() } };
        var again = SettingsJson.Load(SettingsJson.Save(off)).Settings;

        Assert.False(again.Control.Rows.First(r => r.Action == ControlAction.SlideNext).Enabled);
        Assert.Contains("\"version\": 2", SettingsJson.Save(off).Replace("\r", string.Empty), StringComparison.Ordinal);
    }

    [Fact]
    public void The_holyrics_connection_is_not_part_of_the_backup()
    {
        var payload = typeof(AlertaIBT.Core.Backup.BackupPayload).GetProperties().Select(p => p.Name);

        Assert.DoesNotContain(payload, name => name.Contains("Holyrics", StringComparison.OrdinalIgnoreCase));
    }
}
