using AlertaIBT.Core.Settings;

namespace AlertaIBT.Core.Tests.Settings;

public class SettingsSecretsTests
{
    private static readonly FakeProtector Protector = new();

    [Fact]
    public void The_telegram_token_goes_in_protected_and_comes_out_clear()
    {
        var settings = new AppSettings().WithTelegramToken("123456:ABC-def", Protector);

        Assert.NotNull(settings.Telegram.TokenProtected);
        Assert.DoesNotContain("123456", settings.Telegram.TokenProtected);
        Assert.Equal("123456:ABC-def", settings.GetTelegramToken(Protector));
    }

    [Fact]
    public void The_gemini_key_goes_in_protected_and_comes_out_clear()
    {
        var settings = new AppSettings().WithGeminiKey("AIza-secret", Protector);

        Assert.DoesNotContain("AIza", settings.Correction.GeminiKeyProtected);
        Assert.Equal("AIza-secret", settings.GetGeminiKey(Protector));
    }

    [Fact]
    public void Secrets_are_trimmed()
    {
        var settings = new AppSettings().WithTelegramToken("  123:abc \r\n", Protector).WithGeminiKey(" key\n", Protector);

        Assert.Equal("123:abc", settings.GetTelegramToken(Protector));
        Assert.Equal("key", settings.GetGeminiKey(Protector));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void A_blank_secret_clears_it(string? blank)
    {
        var settings = new AppSettings().WithTelegramToken("123:abc", Protector).WithGeminiKey("key", Protector)
            .WithTelegramToken(blank, Protector).WithGeminiKey(blank, Protector);

        Assert.Null(settings.Telegram.TokenProtected);
        Assert.Null(settings.Correction.GeminiKeyProtected);
        Assert.Null(settings.GetTelegramToken(Protector));
        Assert.Null(settings.GetGeminiKey(Protector));
    }

    [Fact]
    public void Nothing_else_changes_when_a_secret_is_set()
    {
        var before = new AppSettings { Bar = new BarSettings { Height = 80 } };

        var after = before.WithTelegramToken("123:abc", Protector);

        Assert.Equal(before with { Telegram = after.Telegram }, after);
        Assert.Equal(80, after.Bar.Height);
    }

    [Fact]
    public void A_different_telegram_token_sets_the_offset_back_to_zero_in_the_same_change()
    {
        var first = new AppSettings().WithTelegramToken("123:old", Protector);
        first = first with { Telegram = first.Telegram with { ChatId = -100, Offset = 5000 } };

        var changed = first.WithTelegramToken("456:new", Protector);

        Assert.Equal(0, changed.Telegram.Offset);
        Assert.Equal(-100, changed.Telegram.ChatId);
        Assert.Equal("456:new", changed.GetTelegramToken(Protector));
    }

    [Fact]
    public void The_same_telegram_token_set_again_keeps_the_offset()
    {
        var settings = new AppSettings().WithTelegramToken("123:same", Protector);
        settings = settings with { Telegram = settings.Telegram with { Offset = 777 } };

        var again = settings.WithTelegramToken("  123:same ", Protector);

        Assert.Equal(777, again.Telegram.Offset);
    }

    [Fact]
    public void Clearing_the_telegram_token_also_resets_the_offset()
    {
        var settings = new AppSettings().WithTelegramToken("123:abc", Protector);
        settings = settings with { Telegram = settings.Telegram with { Offset = 9 } };

        Assert.Equal(0, settings.WithTelegramToken(null, Protector).Telegram.Offset);
    }

    [Fact]
    public void A_secret_that_cannot_be_unprotected_is_none_not_a_crash()
    {
        var settings = new AppSettings { Telegram = new TelegramSettings { TokenProtected = "not-base64!!" } };

        Assert.Null(settings.GetTelegramToken(Protector));
        Assert.Null(settings.GetTelegramToken(new DpapiSecretProtector()));
    }

    [Fact]
    public void The_real_dpapi_works_through_the_same_path_and_the_file_never_has_the_clear_text()
    {
        var settings = new AppSettings().WithTelegramToken("123456:REAL-DPAPI-TOKEN", new DpapiSecretProtector());

        var json = SettingsJson.Save(settings);

        Assert.DoesNotContain("REAL-DPAPI-TOKEN", json);
        var (loaded, _) = SettingsJson.Load(json);
        Assert.Equal("123456:REAL-DPAPI-TOKEN", loaded.GetTelegramToken(new DpapiSecretProtector()));
    }
}
