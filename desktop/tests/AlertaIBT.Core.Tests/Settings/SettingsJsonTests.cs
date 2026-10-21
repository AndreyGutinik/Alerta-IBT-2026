using System.Text.Json;
using AlertaIBT.Core.Correction;
using AlertaIBT.Core.Settings;

namespace AlertaIBT.Core.Tests.Settings;

public class SettingsJsonTests
{
    [Fact]
    public void The_defaults_are_the_ones_of_the_spec()
    {
        var s = new AppSettings();

        Assert.Equal("top", s.Bar.Position);
        Assert.Equal(56, s.Bar.Height);
        Assert.Null(s.Bar.FontSize);
        Assert.Equal(90, s.Bar.Speed);
        Assert.Equal("#000000", s.Bar.Background);
        Assert.Equal("#FFF7E8", s.Bar.Foreground);
        Assert.Equal(320, s.Bar.SlideMs);
        Assert.Equal(24, s.Bar.PaddingX);
        Assert.Equal(0, s.Telegram.ChatId);
        Assert.Null(s.Telegram.TokenProtected);
        Assert.Equal(0, s.Telegram.Offset);
        Assert.True(s.Correction.Enabled);
        Assert.Equal("gemini", s.Correction.Mode);
        Assert.Null(s.Correction.GeminiKeyProtected);
        Assert.Equal(GeminiPrompt.DefaultModel, s.Correction.GeminiModel);
        Assert.Equal(string.Empty, s.Correction.FixedRules);
        Assert.Equal("Ctrl+Alt+Space", s.System.Hotkey);
        Assert.Null(s.System.LastLedDevicePath);
    }

    [Fact]
    public void No_file_is_the_defaults_and_not_corrupt()
    {
        var (settings, wasCorrupt) = SettingsJson.Load(null);

        Assert.Equal(new AppSettings(), settings);
        Assert.False(wasCorrupt);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n ")]
    [InlineData("{")]
    [InlineData("{\"bar\": {\"height\": 8")]
    [InlineData("not json")]
    [InlineData("\0\0\0\0\0\0")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("\"text\"")]
    public void Json_that_is_invalid_or_not_an_object_gives_the_defaults_and_flags_it(string json)
    {
        var (settings, wasCorrupt) = SettingsJson.Load(json);

        Assert.Equal(new AppSettings(), settings);
        Assert.True(wasCorrupt);
    }

    [Fact]
    public void A_file_cut_in_the_middle_of_a_write_is_corrupt()
    {
        var full = SettingsJson.Save(new AppSettings { Bar = new BarSettings { Height = 80 } });

        var (settings, wasCorrupt) = SettingsJson.Load(full[..(full.Length / 2)]);

        Assert.True(wasCorrupt);
        Assert.Equal(new AppSettings(), settings);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"bar\":{}}")]
    [InlineData("{\"bar\":null,\"telegram\":null,\"correction\":null,\"system\":null}")]
    [InlineData("{\"bar\":[],\"telegram\":5,\"correction\":\"x\",\"system\":true}")]
    public void Missing_keys_and_sections_are_the_defaults_and_not_corrupt(string json)
    {
        var (settings, wasCorrupt) = SettingsJson.Load(json);

        Assert.Equal(new AppSettings(), settings);
        Assert.False(wasCorrupt);
    }

    [Fact]
    public void A_partial_file_keeps_what_it_has_and_defaults_the_rest()
    {
        var (settings, wasCorrupt) = SettingsJson.Load("""{"bar":{"height":80,"position":"bottom"},"telegram":{"chatId":-100123}}""");

        Assert.False(wasCorrupt);
        Assert.Equal(80, settings.Bar.Height);
        Assert.Equal("bottom", settings.Bar.Position);
        Assert.Equal(90, settings.Bar.Speed);
        Assert.Equal(-100123, settings.Telegram.ChatId);
        Assert.Equal("gemini", settings.Correction.Mode);
    }

    [Fact]
    public void Unknown_keys_comments_and_trailing_commas_are_tolerated()
    {
        var (settings, wasCorrupt) = SettingsJson.Load("""
            {
              // hand edited
              "future": {"a": 1},
              "bar": {"height": 70, "color": "red",},
            }
            """);

        Assert.False(wasCorrupt);
        Assert.Equal(70, settings.Bar.Height);
    }

    [Fact]
    public void Key_names_are_found_whatever_their_case()
    {
        var (settings, _) = SettingsJson.Load("""{"Bar":{"Height":77,"PaddingX":10}}""");

        Assert.Equal(77, settings.Bar.Height);
        Assert.Equal(10, settings.Bar.PaddingX);
    }

    [Theory]
    [InlineData(500, 120)]
    [InlineData(121, 120)]
    [InlineData(120, 120)]
    [InlineData(56, 56)]
    [InlineData(28, 28)]
    [InlineData(27, 28)]
    [InlineData(0, 28)]
    [InlineData(-10, 28)]
    public void Height_is_held_between_28_and_120(int given, int expected)
    {
        var (settings, _) = SettingsJson.Load($$$"""{"bar":{"height":{{{given}}}}}""");

        Assert.Equal(expected, settings.Bar.Height);
    }

    [Theory]
    [InlineData(5, 30)]
    [InlineData(29, 30)]
    [InlineData(30, 30)]
    [InlineData(90, 90)]
    [InlineData(300, 300)]
    [InlineData(301, 300)]
    [InlineData(99999, 300)]
    [InlineData(-1, 30)]
    public void Speed_is_held_between_30_and_300(int given, int expected)
    {
        var (settings, _) = SettingsJson.Load($$$"""{"bar":{"speed":{{{given}}}}}""");

        Assert.Equal(expected, settings.Bar.Speed);
    }

    [Theory]
    [InlineData(-5, 0)]
    [InlineData(0, 0)]
    [InlineData(24, 24)]
    [InlineData(5000, 200)]
    public void Padding_is_held_between_0_and_200(int given, int expected)
    {
        var (settings, _) = SettingsJson.Load($$$"""{"bar":{"paddingX":{{{given}}}}}""");

        Assert.Equal(expected, settings.Bar.PaddingX);
    }

    [Theory]
    [InlineData(10, 100)]
    [InlineData(320, 320)]
    [InlineData(99999, 1500)]
    public void Slide_is_held_between_100_and_1500_ms(int given, int expected)
    {
        var (settings, _) = SettingsJson.Load($$$"""{"bar":{"slideMs":{{{given}}}}}""");

        Assert.Equal(expected, settings.Bar.SlideMs);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void A_font_size_that_makes_no_sense_means_automatic(int given)
    {
        var (settings, _) = SettingsJson.Load($$$"""{"bar":{"fontSize":{{{given}}}}}""");

        Assert.Null(settings.Bar.FontSize);
    }

    [Theory]
    [InlineData(3, 8)]
    [InlineData(36, 36)]
    [InlineData(9000, 200)]
    public void A_font_size_is_held_between_8_and_200(int given, int expected)
    {
        var (settings, _) = SettingsJson.Load($$$"""{"bar":{"fontSize":{{{given}}}}}""");

        Assert.Equal(expected, settings.Bar.FontSize);
    }

    [Theory]
    [InlineData("bottom", "bottom")]
    [InlineData(" BOTTOM ", "bottom")]
    [InlineData("Top", "top")]
    [InlineData("middle", "top")]
    [InlineData("", "top")]
    public void Position_is_top_or_bottom(string given, string expected)
    {
        var (settings, _) = SettingsJson.Load($$$"""{"bar":{"position":"{{{given}}}"}}""");

        Assert.Equal(expected, settings.Bar.Position);
    }

    [Theory]
    [InlineData("#112233", "#112233")]
    [InlineData("#abcdef", "#ABCDEF")]
    [InlineData(" #fff7e8 ", "#FFF7E8")]
    [InlineData("red", "#000000")]
    [InlineData("#12345", "#000000")]
    [InlineData("#GGGGGG", "#000000")]
    [InlineData("#11223344", "#000000")]
    [InlineData("", "#000000")]
    public void Colors_are_six_digit_hex_or_the_default(string given, string expected)
    {
        var (settings, _) = SettingsJson.Load($$$"""{"bar":{"background":"{{{given}}}"}}""");

        Assert.Equal(expected, settings.Bar.Background);
    }

    [Fact]
    public void An_invalid_foreground_falls_back_to_its_own_default()
    {
        var (settings, _) = SettingsJson.Load("""{"bar":{"foreground":"nope"}}""");

        Assert.Equal("#FFF7E8", settings.Bar.Foreground);
    }

    [Theory]
    [InlineData("offline", "offline")]
    [InlineData("GEMINI", "gemini")]
    [InlineData("claude", "gemini")]
    [InlineData("", "gemini")]
    public void Mode_is_gemini_or_offline(string given, string expected)
    {
        var (settings, _) = SettingsJson.Load($$$"""{"correction":{"mode":"{{{given}}}"}}""");

        Assert.Equal(expected, settings.Correction.Mode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_model_is_the_default_model(string given)
    {
        var (settings, _) = SettingsJson.Load($$$"""{"correction":{"geminiModel":"{{{given}}}"}}""");

        Assert.Equal(GeminiPrompt.DefaultModel, settings.Correction.GeminiModel);
    }

    [Fact]
    public void A_model_is_trimmed()
    {
        var (settings, _) = SettingsJson.Load("""{"correction":{"geminiModel":" gemini-x "}}""");

        Assert.Equal("gemini-x", settings.Correction.GeminiModel);
    }

    [Fact]
    public void A_blank_hotkey_is_the_default()
    {
        var (settings, _) = SettingsJson.Load("""{"system":{"hotkey":"  "}}""");

        Assert.Equal("Ctrl+Alt+Space", settings.System.Hotkey);
    }

    [Fact]
    public void A_negative_offset_is_zero()
    {
        var (settings, _) = SettingsJson.Load("""{"telegram":{"offset":-5}}""");

        Assert.Equal(0, settings.Telegram.Offset);
    }

    [Fact]
    public void A_value_of_the_wrong_type_only_resets_that_value()
    {
        var (settings, wasCorrupt) = SettingsJson.Load("""
            {"bar":{"height":"tall","speed":120,"position":5,"fontSize":"big"},
             "correction":{"enabled":"yes","mode":"offline"}}
            """);

        Assert.False(wasCorrupt);
        Assert.Equal(56, settings.Bar.Height);
        Assert.Equal(120, settings.Bar.Speed);
        Assert.Equal("top", settings.Bar.Position);
        Assert.Null(settings.Bar.FontSize);
        Assert.True(settings.Correction.Enabled);
        Assert.Equal("offline", settings.Correction.Mode);
    }

    [Fact]
    public void Numbers_that_are_huge_or_fractional_are_handled()
    {
        var (settings, wasCorrupt) = SettingsJson.Load("""{"bar":{"height":1e12,"speed":120.6,"paddingX":99999999999999999999},"telegram":{"offset":1e30}}""");

        Assert.False(wasCorrupt);
        Assert.Equal(120, settings.Bar.Height);
        Assert.Equal(121, settings.Bar.Speed);
        Assert.Equal(200, settings.Bar.PaddingX);
        Assert.Equal(0, settings.Telegram.Offset);
    }

    [Fact]
    public void A_chat_id_written_as_text_is_understood()
    {
        var (settings, _) = SettingsJson.Load("""{"telegram":{"chatId":"-1001234567890"}}""");

        Assert.Equal(-1001234567890, settings.Telegram.ChatId);
    }

    [Fact]
    public void A_chat_id_that_is_not_a_number_is_none()
    {
        var (settings, wasCorrupt) = SettingsJson.Load("""{"telegram":{"chatId":"my group"}}""");

        Assert.False(wasCorrupt);
        Assert.Equal(0, settings.Telegram.ChatId);
    }

    [Fact]
    public void Everything_survives_the_round_trip()
    {
        var original = new AppSettings
        {
            Bar = new BarSettings
            {
                Position = "bottom",
                Height = 90,
                FontSize = 48,
                Speed = 150,
                Background = "#112233",
                Foreground = "#ABCDEF",
                SlideMs = 500,
                PaddingX = 40,
            },
            Telegram = new TelegramSettings { ChatId = -1001234567890, TokenProtected = "AQAAANCMnd8BFdERjHoAwE", Offset = 987654321012 },
            Correction = new CorrectionSettings
            {
                Enabled = false,
                Mode = "offline",
                GeminiKeyProtected = "AQAAAKEY",
                GeminiModel = "gemini-2.5-flash-lite",
                FixedRules = "farol acesso => farol aceso\nparado local proibid -> parado em local proibido\n"
                    + "recep\U000000E7\U000000E3o = portaria \U0001F697",
            },
            System = new SystemSettings { Hotkey = "Ctrl+Shift+F9", LastLedDevicePath = @"\\?\DISPLAY#ABC1234#5&1a2b3c4d&0&UID4352#{e6f07b5f}" },
        };

        var (loaded, wasCorrupt) = SettingsJson.Load(SettingsJson.Save(original));

        Assert.False(wasCorrupt);
        Assert.Equal(original, loaded);
    }

    [Fact]
    public void The_defaults_survive_the_round_trip()
    {
        var (loaded, wasCorrupt) = SettingsJson.Load(SettingsJson.Save(new AppSettings()));

        Assert.False(wasCorrupt);
        Assert.Equal(new AppSettings(), loaded);
    }

    [Fact]
    public void Save_holds_out_of_range_values_in_range()
    {
        var json = SettingsJson.Save(new AppSettings { Bar = new BarSettings { Height = 500, Speed = 5 } });

        using var document = JsonDocument.Parse(json);
        Assert.Equal(120, document.RootElement.GetProperty("bar").GetProperty("height").GetInt32());
        Assert.Equal(30, document.RootElement.GetProperty("bar").GetProperty("speed").GetInt32());
    }

    [Fact]
    public void Save_writes_readable_json_with_text_not_escaped()
    {
        var json = SettingsJson.Save(new AppSettings
        {
            Correction = new CorrectionSettings { FixedRules = "recep\U000000E7\U000000E3o" },
        });

        using var document = JsonDocument.Parse(json);
        Assert.Contains("recep\U000000E7\U000000E3o", json);
        Assert.Contains('\n', json);
        Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
    }

    [Fact]
    public void Save_never_writes_the_clear_secrets_only_the_protected_form()
    {
        var protector = new FakeProtector();
        var settings = new AppSettings()
            .WithTelegramToken("123456:CLEAR-TOKEN", protector)
            .WithGeminiKey("AIza-CLEAR-KEY", protector);

        var json = SettingsJson.Save(settings);

        Assert.DoesNotContain("CLEAR-TOKEN", json);
        Assert.DoesNotContain("CLEAR-KEY", json);
        Assert.DoesNotContain("CLEAR", settings.ToString());
        Assert.Contains("tokenProtected", json);
        Assert.Contains("geminiKeyProtected", json);
    }

    [Fact]
    public void Normalize_is_idempotent_and_holds_every_value_in_range()
    {
        var wild = new AppSettings
        {
            Bar = new BarSettings { Position = "x", Height = 9999, Speed = -4, Background = "zzz", Foreground = null!, SlideMs = 0, PaddingX = -1, FontSize = 1 },
            Telegram = new TelegramSettings { Offset = -9, TokenProtected = "  " },
            Correction = new CorrectionSettings { Mode = null!, GeminiModel = null!, FixedRules = null! },
            System = new SystemSettings { Hotkey = null!, LastLedDevicePath = " " },
        };

        var once = SettingsJson.Normalize(wild);

        Assert.Equal(once, SettingsJson.Normalize(once));
        Assert.Equal("top", once.Bar.Position);
        Assert.Equal(120, once.Bar.Height);
        Assert.Equal(30, once.Bar.Speed);
        Assert.Equal("#000000", once.Bar.Background);
        Assert.Equal("#FFF7E8", once.Bar.Foreground);
        Assert.Equal(100, once.Bar.SlideMs);
        Assert.Equal(0, once.Bar.PaddingX);
        Assert.Equal(8, once.Bar.FontSize);
        Assert.Equal(0, once.Telegram.Offset);
        Assert.Null(once.Telegram.TokenProtected);
        Assert.Equal("gemini", once.Correction.Mode);
        Assert.Equal(GeminiPrompt.DefaultModel, once.Correction.GeminiModel);
        Assert.Equal(string.Empty, once.Correction.FixedRules);
        Assert.Equal("Ctrl+Alt+Space", once.System.Hotkey);
        Assert.Null(once.System.LastLedDevicePath);
    }

    [Fact]
    public void Null_sections_are_defaults_when_normalizing()
    {
        var once = SettingsJson.Normalize(new AppSettings { Bar = null!, Telegram = null!, Correction = null!, System = null! });

        Assert.Equal(new AppSettings(), once);
    }
}
