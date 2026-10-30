using System.Reflection;
using System.Text;
using System.Text.Json;
using AlertaIBT.Core.Backup;
using AlertaIBT.Core.Settings;
using AlertaIBT.Core.Tests.Settings;

namespace AlertaIBT.Core.Tests.Backup;

public sealed class SettingsBackupTests
{
    private const string Password = "senha-da-igreja-2026";
    private const string Token = "123456789:AAFakeTokenFakeTokenFakeToken_1";
    private const string GeminiKey = "AIzaSyFAKE-gemini-key-0123456789";
    private const string SitePassword = "senha-do-pc-do-site";
    private static readonly FakeProtector Protector = new();
    private static readonly DateTimeOffset Now = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);

    // A machine with everything set to something other than the default, the included items and the others alike.
    private static AppSettings Machine()
    {
        var s = new AppSettings()
            .WithTelegramToken(Token, Protector)
            .WithGeminiKey(GeminiKey, Protector)
            .WithSitePassword(SitePassword, Protector);
        return s with
        {
            Telegram = s.Telegram with { ChatId = -1001234567890, Offset = 777 },
            Correction = s.Correction with { GeminiModel = "gemini-2.5-flash", FixedRules = "corola=Corolla\nhb 20=HB20", Enabled = false, Mode = "offline" },
            System = s.System with { Hotkey = "Ctrl+Alt+P", LastLedDevicePath = @"\\?\DISPLAY#LED#1", AlertsEnabled = false, ClearHistoryWeekly = false, AutoUpdate = false },
            MouseLock = s.MouseLock with { Hotkey = "Ctrl+Alt+K", TripleCtrl = false, Enabled = true },
            Ndi = s.Ndi with { ShortcutsEnabled = false, HideFromCapture = true, FixedIps = ["10.0.0.5"] },
            Player = s.Player with { MediaKeys = false, Volume = 42, Shuffle = false },
            Bar = s.Bar with { Position = "bottom", Height = 90, Speed = 150, Background = "#112233", FontSize = 40 },
            Site = s.Site with { Enabled = true, DatabaseUrl = "https://igreja-rtdb.firebaseio.com", ApiKey = "AIzaSyFakeWebKey123", DeviceEmail = "pc@igreja.org", ProjectId = "igreja" },
            Audio = s.Audio with { MasterVolume = 33, LockOutput = false },
        };
    }

    private static BackupPayload RoundTrip(AppSettings settings) =>
        SettingsBackup.Read(SettingsBackup.Export(settings, Protector, Password, Now), Password);

    [Fact]
    public void Export_then_read_gives_back_every_included_value_in_clear()
    {
        var payload = RoundTrip(Machine());

        Assert.Equal(Token, payload.TelegramToken);
        Assert.Equal(-1001234567890, payload.TelegramChatId);
        Assert.Equal(GeminiKey, payload.GeminiKey);
        Assert.Equal("gemini-2.5-flash", payload.GeminiModel);
        Assert.Equal("Ctrl+Alt+P", payload.PanelHotkey);
        Assert.Equal("Ctrl+Alt+K", payload.MouseLockHotkey);
        Assert.False(payload.TripleCtrl);
        Assert.False(payload.NdiShortcuts);
        Assert.False(payload.MediaKeys);
        Assert.Equal("bottom", payload.BarPosition);
        Assert.Equal(90, payload.BarHeight);
        Assert.Equal(150, payload.BarSpeed);
        Assert.Equal(@"\\?\DISPLAY#LED#1", payload.LedDevicePath);
        Assert.Equal("https://igreja-rtdb.firebaseio.com", payload.SiteDatabaseUrl);
        Assert.Equal("AIzaSyFakeWebKey123", payload.SiteApiKey);
        Assert.Equal("pc@igreja.org", payload.SiteEmail);
        Assert.Equal(SitePassword, payload.SitePassword);
        Assert.Equal("corola=Corolla\nhb 20=HB20", payload.FixedRules);
        Assert.False(payload.AlertsEnabled);
        Assert.False(payload.ClearHistoryWeekly);
        Assert.Equal(Now.ToString("O"), payload.ExportedAt);
    }

    [Fact]
    public void The_file_holds_no_secret_and_no_field_name_in_plain_text()
    {
        var file = SettingsBackup.Export(Machine(), Protector, Password, Now);
        var text = Encoding.Latin1.GetString(file);

        foreach (var secret in new[] { Token, GeminiKey, SitePassword, Password, "AIzaSyFakeWebKey123", "pc@igreja.org", "igreja-rtdb", "telegramToken", "geminiKey", "sitePassword", "corola" })
        {
            Assert.DoesNotContain(secret, text, StringComparison.OrdinalIgnoreCase);
        }

        Assert.DoesNotContain(Convert.ToBase64String(Encoding.UTF8.GetBytes(Token)), text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_payload_never_prints_its_secrets()
    {
        var payload = RoundTrip(Machine());

        var text = payload.ToString();

        Assert.DoesNotContain(Token, text, StringComparison.Ordinal);
        Assert.DoesNotContain(SitePassword, text, StringComparison.Ordinal);
    }

    [Fact]
    public void Reading_with_the_wrong_password_or_a_damaged_file_gives_the_clear_errors()
    {
        var file = SettingsBackup.Export(Machine(), Protector, Password, Now);

        var wrong = Assert.Throws<BackupException>(() => SettingsBackup.Read(file, "nao-e-essa-senha"));
        Assert.Equal("Senha incorreta ou arquivo alterado.", wrong.Message);

        file[60] ^= 0xFF;
        Assert.Equal(BackupError.WrongPasswordOrCorrupt, Assert.Throws<BackupException>(() => SettingsBackup.Read(file, Password)).Error);
    }

    [Fact]
    public void A_file_made_by_something_else_with_the_right_password_but_wrong_content_is_invalid()
    {
        var json = Encoding.UTF8.GetBytes("[1,2,3]");
        var file = BackupCrypto.Encrypt(json, Password);

        Assert.Equal(BackupError.InvalidContent, Assert.Throws<BackupException>(() => SettingsBackup.Read(file, Password)).Error);
    }

    [Fact]
    public void A_payload_with_values_that_cannot_be_applied_is_refused()
    {
        var good = SettingsBackup.Capture(Machine(), Protector, Now);
        var cases = new[]
        {
            good with { PanelHotkey = "isso nao e atalho" },
            good with { MouseLockHotkey = string.Empty },
            good with { BarPosition = "left" },
            good with { BarHeight = 5000 },
            good with { BarSpeed = 1 },
            good with { SiteDatabaseUrl = "http://evil.example.com" },
            good with { GeminiModel = "  " },
        };

        foreach (var bad in cases)
        {
            var json = JsonSerializer.SerializeToUtf8Bytes(bad, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            var file = BackupCrypto.Encrypt(json, Password);
            Assert.Equal(BackupError.InvalidContent, Assert.Throws<BackupException>(() => SettingsBackup.Read(file, Password)).Error);
        }
    }

    [Fact]
    public void A_newer_content_version_is_refused_as_unsupported()
    {
        var future = SettingsBackup.Capture(Machine(), Protector, Now) with { Version = 99 };
        var json = JsonSerializer.SerializeToUtf8Bytes(future, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        var file = BackupCrypto.Encrypt(json, Password);

        Assert.Equal(BackupError.UnsupportedVersion, Assert.Throws<BackupException>(() => SettingsBackup.Read(file, Password)).Error);
    }

    [Fact]
    public void Apply_changes_only_the_included_values_and_every_other_property_stays_equal()
    {
        var before = new AppSettings
        {
            Audio = new AudioSettings { MasterVolume = 11, OutputDeviceName = "WING" },
            Player = new PlayerSettings { Volume = 17, Shuffle = false, CurrentPlaylist = "Louvor", MediaKeys = true },
            Ndi = new NdiSettings { HideFromCapture = true, FixedIps = ["1.2.3.4"], ShortcutsEnabled = true },
            System = new SystemSettings { AutoUpdate = false, UpdateAttempts = 3, SitePendingClear = true, Hotkey = "Ctrl+Alt+Space" },
            Bar = new BarSettings { Background = "#ABCDEF", PaddingX = 50, SlideMs = 700, FontSize = 33 },
            Correction = new CorrectionSettings { Enabled = false, Mode = "offline" },
            MouseLock = new MouseLockSettings { Enabled = true },
            Site = new SiteSettings { Enabled = true, ProjectId = "meu-projeto" },
            Backup = new BackupSettings { AutoEnabled = true, Folder = @"D:\copias", PasswordProtected = "abc" },
        }.WithTelegramToken("987654321:BBOldTokenOldTokenOldToken_2", Protector);
        before = before with { Telegram = before.Telegram with { Offset = 55 } };

        var payload = SettingsBackup.Capture(Machine(), Protector, Now);
        var after = SettingsBackup.Apply(before, payload, Protector);

        // Put the included values back and nothing may differ: property by property, section by section.
        var restoredBack = SettingsBackup.Apply(after, SettingsBackup.Capture(before, Protector, Now), Protector);
        AssertSectionsEqual(before, restoredBack, ignore: ["Telegram.Offset"]);

        // And the included values really changed.
        Assert.Equal(Token, after.GetTelegramToken(Protector));
        Assert.Equal(-1001234567890, after.Telegram.ChatId);
        Assert.Equal("bottom", after.Bar.Position);
        Assert.Equal(90, after.Bar.Height);
        Assert.Equal(150, after.Bar.Speed);
        Assert.Equal("Ctrl+Alt+P", after.System.Hotkey);
        Assert.False(after.Ndi.ShortcutsEnabled);
        Assert.False(after.Player.MediaKeys);

        // What is outside the list is the same object-for-object.
        Assert.Equal(before.Audio, after.Audio);
        Assert.Equal(before.Backup, after.Backup);
        Assert.Equal(before.Bar with { Position = "bottom", Height = 90, Speed = 150 }, after.Bar);
        Assert.Equal(before.Player with { MediaKeys = false }, after.Player);
        Assert.Equal(before.Ndi with { ShortcutsEnabled = false }, after.Ndi);
        Assert.Equal(before.Site with { DatabaseUrl = payload.SiteDatabaseUrl, ApiKey = payload.SiteApiKey, DeviceEmail = payload.SiteEmail, PasswordProtected = after.Site.PasswordProtected }, after.Site);
        Assert.Equal(before.Correction with { GeminiModel = payload.GeminiModel, FixedRules = payload.FixedRules, GeminiKeyProtected = after.Correction.GeminiKeyProtected }, after.Correction);
        Assert.Equal(before.MouseLock with { Hotkey = "Ctrl+Alt+K", TripleCtrl = false }, after.MouseLock);
        Assert.Equal(before.System.AutoUpdate, after.System.AutoUpdate);
        Assert.Equal(before.System.UpdateAttempts, after.System.UpdateAttempts);
        Assert.Equal(before.System.SitePendingClear, after.System.SitePendingClear);
        Assert.Equal(before.System.LastHistoryClearAt, after.System.LastHistoryClearAt);
    }

    [Fact]
    public void A_new_telegram_token_sends_the_offset_back_to_zero_as_typing_it_does_and_the_same_token_keeps_it()
    {
        var before = Machine();
        var same = SettingsBackup.Apply(before, SettingsBackup.Capture(before, Protector, Now), Protector);
        Assert.Equal(777, same.Telegram.Offset);

        var other = SettingsBackup.Apply(before, SettingsBackup.Capture(before, Protector, Now) with { TelegramToken = "111111111:CCOtherTokenOtherTokenOther_3" }, Protector);
        Assert.Equal(0, other.Telegram.Offset);
    }

    [Fact]
    public void Applying_through_the_settings_service_announces_the_change_once_so_the_app_reacts_live()
    {
        var dir = Path.Combine(Path.GetTempPath(), "alerta-ibt-bk-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var service = new SettingsService(new SettingsStore(Path.Combine(dir, "settings.json")));
            var raised = new List<AppSettings>();
            service.Changed += raised.Add;
            var payload = SettingsBackup.Capture(Machine(), Protector, Now);

            service.Update(s => SettingsBackup.Apply(s, payload, Protector));

            var announced = Assert.Single(raised);
            Assert.Equal("Ctrl+Alt+P", announced.System.Hotkey);
            Assert.Equal(90, service.Current.Bar.Height);
            Assert.Equal(Token, service.Current.GetTelegramToken(Protector));
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public void The_preview_lists_all_items_marks_what_changes_and_masks_the_secrets()
    {
        var current = new AppSettings().WithTelegramToken("987654321:BBOldTokenOldTokenOldToken_2", Protector);
        var payload = SettingsBackup.Capture(Machine(), Protector, Now);

        var preview = SettingsBackup.Preview(current, payload, Protector);

        Assert.Equal(SettingsBackup.ItemLabels.Count, preview.Count);
        Assert.Equal(SettingsBackup.ItemLabels, preview.Select(c => c.Label).ToList());
        var token = preview[0];
        Assert.True(token.IsSecret);
        Assert.True(token.Changed);
        Assert.StartsWith("987", token.Old, StringComparison.Ordinal);
        Assert.StartsWith("123", token.New, StringComparison.Ordinal);
        Assert.Equal("123\u2022\u2022\u2022\u2022\u2022\u2022", token.New);

        foreach (var line in preview.Where(c => c.IsSecret))
        {
            Assert.DoesNotContain(Token, line.New, StringComparison.Ordinal);
            Assert.DoesNotContain(GeminiKey, line.New, StringComparison.Ordinal);
            Assert.DoesNotContain(SitePassword, line.New, StringComparison.Ordinal);
            Assert.True(line.New.Length <= 9);
        }

        var all = string.Join("|", preview.Select(c => c.Old + c.New));
        Assert.DoesNotContain(Token, all, StringComparison.Ordinal);
        Assert.DoesNotContain(GeminiKey, all, StringComparison.Ordinal);
        Assert.DoesNotContain(SitePassword, all, StringComparison.Ordinal);
        Assert.DoesNotContain("corola", all, StringComparison.Ordinal);

        Assert.Equal("Em cima", preview.Single(c => c.Label == "Posi\u00e7\u00e3o da barra").Old);
        Assert.Equal("Embaixo", preview.Single(c => c.Label == "Posi\u00e7\u00e3o da barra").New);
        Assert.Equal("2 linhas", preview.Single(c => c.Label == "Regras fixas").New);
        Assert.Equal("90 px", preview.Single(c => c.Label == "Tamanho da barra").New);
    }

    [Fact]
    public void Restoring_a_backup_equal_to_the_current_settings_shows_nothing_to_change()
    {
        var current = Machine();

        var preview = SettingsBackup.Preview(current, SettingsBackup.Capture(current, Protector, Now), Protector);

        Assert.DoesNotContain(preview, change => change.Changed);
    }

    [Theory]
    [InlineData(null, "(vazio)")]
    [InlineData("", "(vazio)")]
    [InlineData("abc", "\u2022\u2022\u2022\u2022\u2022\u2022")]
    [InlineData("abcdef", "\u2022\u2022\u2022\u2022\u2022\u2022")]
    [InlineData("abcdefg", "abc\u2022\u2022\u2022\u2022\u2022\u2022")]
    public void A_secret_is_masked_to_its_first_three_characters_and_dots(string? secret, string expected)
    {
        Assert.Equal(expected, SettingsBackup.Mask(secret));
    }

    [Fact]
    public void The_manual_file_name_has_the_date_and_the_extension()
    {
        Assert.Equal("ibt-backup-20261029.ibtbackup", SettingsBackup.ManualFileName(Now));
        Assert.False(BackupFiles.IsAutoCopyName(SettingsBackup.ManualFileName(Now)));
    }

    [Fact]
    public void The_backup_settings_survive_settings_json_and_hold_the_password_only_protected()
    {
        var settings = new AppSettings { Backup = new BackupSettings { AutoEnabled = true, Folder = @"D:\copias", PasswordProtected = Protector.Protect("a-senha-da-copia") } };

        var json = SettingsJson.Save(settings);
        var loaded = SettingsJson.Load(json).Settings;

        Assert.Equal(settings.Backup, loaded.Backup);
        Assert.DoesNotContain("a-senha-da-copia", json, StringComparison.Ordinal);
        Assert.Equal(new BackupSettings(), SettingsJson.Load("{}").Settings.Backup);
    }

    // Compares every property of every section; "Section.Property" names in ignore are skipped.
    private static void AssertSectionsEqual(AppSettings expected, AppSettings actual, string[] ignore)
    {
        foreach (var section in typeof(AppSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var a = section.GetValue(expected)!;
            var b = section.GetValue(actual)!;
            foreach (var property in section.PropertyType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (ignore.Contains(section.Name + "." + property.Name))
                {
                    continue;
                }

                var left = property.GetValue(a);
                var right = property.GetValue(b);
                if (left is System.Collections.IEnumerable list && left is not string)
                {
                    Assert.True(list.Cast<object>().SequenceEqual(((System.Collections.IEnumerable)right!).Cast<object>()), $"{section.Name}.{property.Name} changed");
                }
                else
                {
                    Assert.True(Equals(left, right), $"{section.Name}.{property.Name} changed");
                }
            }
        }
    }
}
