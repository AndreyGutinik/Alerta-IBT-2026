using System.Text;
using AlertaIBT.App.ViewModels;
using AlertaIBT.Core.Backup;
using AlertaIBT.Core.Diagnostics;
using AlertaIBT.Core.Services;
using AlertaIBT.Core.Settings;

namespace AlertaIBT.App.Tests.ViewModels;

// The logic of the Backup card with the windows replaced by a script: what is asked, in which order, and that nothing is changed before the confirmation.
public sealed class BackupViewModelTests : IDisposable
{
    private const string Password = "senha-do-backup-2026";
    private const string Token = "123456789:AAFakeTokenFakeTokenFakeToken_1";

    private readonly SettingsRig _rig;
    private readonly FakeUi _ui = new();
    private readonly AutoBackupService _auto;
    private readonly BackupViewModel _vm;
    private readonly string _work;
    private int _restoredCalls;

    public BackupViewModelTests()
    {
        _rig = new SettingsRig(Machine());
        _work = Path.Combine(_rig.Folder, "work");
        Directory.CreateDirectory(_work);
        _auto = new AutoBackupService(_rig.Settings, _rig.Protector, _rig.Clock, runner: work =>
        {
            work();
            return Task.CompletedTask;
        });
        _auto.Start();
        _vm = new BackupViewModel(_rig.Settings, _rig.Protector, _auto, _rig.Saver, _ui, () => _restoredCalls++, clock: _rig.Clock);
    }

    public void Dispose()
    {
        _vm.Dispose();
        _auto.Dispose();
        _rig.Dispose();
    }

    private static AppSettings Machine()
    {
        var protector = new ReversingProtector();
        var s = new AppSettings().WithTelegramToken(Token, protector).WithGeminiKey("AIzaSyFAKE-gemini-key-0123456789", protector);
        return s with
        {
            Telegram = s.Telegram with { ChatId = -100777 },
            Bar = s.Bar with { Speed = 150, Height = 80, Position = "bottom", Background = "#123456" },
            Audio = s.Audio with { MasterVolume = 31 },
            Player = s.Player with { Volume = 44, MediaKeys = false },
            Correction = s.Correction with { FixedRules = "a=b" },
        };
    }

    // A backup file of another machine: made with the real export, then put in the work folder.
    private string BackupOfOtherMachine(string password = Password)
    {
        var protector = new ReversingProtector();
        var other = new AppSettings()
            .WithTelegramToken("999999999:ZZOtherTokenOtherTokenOther_9", protector)
            .WithGeminiKey("AIzaSyOTHER-gemini-key-9876543210", protector)
            .WithSitePassword("outra-senha-do-site", protector);
        other = other with
        {
            Telegram = other.Telegram with { ChatId = -100999 },
            Bar = other.Bar with { Speed = 60, Height = 40, Position = "top" },
            System = other.System with { Hotkey = "Ctrl+Alt+P", AlertsEnabled = false },
            Site = other.Site with { DatabaseUrl = "https://outra-rtdb.firebaseio.com", ApiKey = "AIzaSyOutraWebKey123", DeviceEmail = "pc@outra.org" },
            Correction = other.Correction with { FixedRules = "x=y\nz=w", GeminiModel = "gemini-9-pro" },
            Player = other.Player with { MediaKeys = true },
            Audio = other.Audio with { MasterVolume = 5 },
        };
        var path = Path.Combine(_work, "outro.ibtbackup");
        File.WriteAllBytes(path, SettingsBackup.Export(other, protector, password, _rig.Clock.GetUtcNow()));
        return path;
    }

    [Fact]
    public void Export_asks_where_then_the_password_and_writes_a_file_named_with_the_date_that_opens_with_it()
    {
        var path = Path.Combine(_work, "meu.ibtbackup");
        _ui.SavePath = path;
        _ui.NewPassword = Password;

        _vm.ExportCommand.Execute(null);

        Assert.Equal("ibt-backup-20261020.ibtbackup", _ui.SuggestedName);
        Assert.Equal(BackupResultKind.Ok, _vm.ResultKind);
        Assert.True(_vm.HasResult);
        var payload = SettingsBackup.Read(File.ReadAllBytes(path), Password);
        Assert.Equal(Token, payload.TelegramToken);
        Assert.Equal(150, payload.BarSpeed);
        Assert.Empty(Directory.GetFiles(_work, "*.tmp"));
        Assert.DoesNotContain(Token, Encoding.Latin1.GetString(File.ReadAllBytes(path)), StringComparison.Ordinal);
    }

    [Fact]
    public void Cancelling_the_save_dialog_or_the_password_writes_nothing()
    {
        _ui.SavePath = null;
        _ui.NewPassword = Password;
        _vm.ExportCommand.Execute(null);
        Assert.Equal(0, _ui.PasswordAsked);

        _ui.SavePath = Path.Combine(_work, "meu.ibtbackup");
        _ui.NewPassword = null;
        _vm.ExportCommand.Execute(null);

        Assert.False(File.Exists(_ui.SavePath));
        Assert.False(_vm.HasResult);
    }

    [Fact]
    public void A_folder_that_cannot_be_written_shows_the_error_chip()
    {
        _ui.SavePath = Path.Combine(_work, "nao-existe", "meu.ibtbackup");
        _ui.NewPassword = Password;

        _vm.ExportCommand.Execute(null);

        Assert.Equal(BackupResultKind.Error, _vm.ResultKind);
        Assert.Contains("salvar", _vm.ResultText, StringComparison.Ordinal);
    }

    [Fact]
    public void Restore_shows_the_masked_preview_and_changes_nothing_until_the_confirmation()
    {
        _ui.OpenPath = BackupOfOtherMachine();
        _ui.TypedPasswords = [Password];
        _ui.PreviewAnswer = true;
        _ui.ConfirmAnswer = false; // cancelled at the last question
        var before = _rig.Settings.Current;
        var changedBefore = _rig.ChangedCount;

        _vm.RestoreCommand.Execute(null);

        Assert.NotNull(_ui.Preview);
        Assert.Equal(1, _ui.ConfirmAsked);
        Assert.Same(before, _rig.Settings.Current);
        Assert.Equal(changedBefore, _rig.ChangedCount);
        Assert.Equal(0, _restoredCalls);
        var token = _ui.Preview!.Single(c => c.Label == "Token do Telegram");
        Assert.Equal("123••••••", token.Old);
        Assert.Equal("999••••••", token.New);
        Assert.All(_ui.Preview!.Where(c => c.IsSecret), c => Assert.True(c.New.Length <= 9));
        Assert.DoesNotContain(_ui.Preview!, c => c.New.Contains("OtherToken", StringComparison.Ordinal) || c.Old.Contains("FakeToken", StringComparison.Ordinal));
    }

    [Fact]
    public void Cancelling_at_the_preview_changes_nothing_and_never_reaches_the_last_question()
    {
        _ui.OpenPath = BackupOfOtherMachine();
        _ui.TypedPasswords = [Password];
        _ui.PreviewAnswer = false;
        var before = _rig.Settings.Current;

        _vm.RestoreCommand.Execute(null);

        Assert.Equal(0, _ui.ConfirmAsked);
        Assert.Same(before, _rig.Settings.Current);
    }

    [Fact]
    public void A_wrong_password_is_told_inside_the_password_window_and_the_right_one_goes_on()
    {
        _ui.OpenPath = BackupOfOtherMachine();
        _ui.TypedPasswords = ["senha-errada-123", Password];
        _ui.PreviewAnswer = false;

        _vm.RestoreCommand.Execute(null);

        Assert.Equal(["Senha incorreta ou arquivo alterado.", null], _ui.VerifyResults);
        Assert.NotNull(_ui.Preview);
    }

    [Fact]
    public void Confirmed_restore_changes_only_the_included_items_through_the_service_and_tells_the_tabs()
    {
        var before = _rig.Settings.Current;
        _ui.OpenPath = BackupOfOtherMachine();
        _ui.TypedPasswords = [Password];
        _ui.PreviewAnswer = true;
        _ui.ConfirmAnswer = true;
        AppSettings? announced = null;
        _rig.Settings.Changed += s => announced = s;

        _vm.RestoreCommand.Execute(null);

        var after = _rig.Settings.Current;
        Assert.Same(after, announced);
        Assert.Equal(1, _restoredCalls);
        Assert.Equal(BackupResultKind.Ok, _vm.ResultKind);
        Assert.Equal("999999999:ZZOtherTokenOtherTokenOther_9", after.GetTelegramToken(_rig.Protector));
        Assert.Equal(-100999, after.Telegram.ChatId);
        Assert.Equal(60, after.Bar.Speed);
        Assert.Equal("top", after.Bar.Position);
        Assert.Equal("Ctrl+Alt+P", after.System.Hotkey);
        Assert.False(after.System.AlertsEnabled);
        Assert.Equal("https://outra-rtdb.firebaseio.com", after.Site.DatabaseUrl);
        Assert.Equal("gemini-9-pro", after.Correction.GeminiModel);
        Assert.True(after.Player.MediaKeys);

        // Outside the list: untouched.
        Assert.Equal(before.Audio, after.Audio);
        Assert.Equal(before.Player with { MediaKeys = true }, after.Player);
        Assert.Equal(before.Bar with { Speed = 60, Height = 40, Position = "top" }, after.Bar);
        Assert.Equal(before.Ndi, after.Ndi);
        Assert.Equal(before.MouseLock, after.MouseLock);
        Assert.Equal(before.Backup, after.Backup);
        Assert.Equal(before.Site.Enabled, after.Site.Enabled);
        Assert.Equal(before.System.AutoUpdate, after.System.AutoUpdate);

        // Saved at once, with the secrets only protected.
        Assert.False(_rig.Settings.HasUnsavedChanges);
        Assert.DoesNotContain("OtherToken", _rig.FileText(), StringComparison.Ordinal);
        Assert.Equal(after.Bar, _rig.OnDisk().Bar);
    }

    [Theory]
    [InlineData("not-a-backup")]
    [InlineData("truncated")]
    [InlineData("future")]
    public void A_file_that_is_not_usable_shows_its_own_message_before_any_password_is_asked(string kind)
    {
        var path = Path.Combine(_work, "ruim.ibtbackup");
        var good = File.ReadAllBytes(BackupOfOtherMachine());
        switch (kind)
        {
            case "not-a-backup":
                File.WriteAllText(path, "isto e so um texto qualquer que nao e um backup de nada, bem comprido");
                break;
            case "truncated":
                File.WriteAllBytes(path, good[..30]);
                break;
            default:
                good[8] = 9;
                File.WriteAllBytes(path, good);
                break;
        }

        _ui.OpenPath = path;
        _vm.RestoreCommand.Execute(null);

        Assert.Equal(BackupResultKind.Error, _vm.ResultKind);
        Assert.Equal(0, _ui.PasswordAsked);
        Assert.Equal(
            kind switch
            {
                "not-a-backup" => BackupException.MessageFor(BackupError.NotABackup),
                "truncated" => BackupException.MessageFor(BackupError.Truncated),
                _ => BackupException.MessageFor(BackupError.UnsupportedVersion),
            },
            _vm.ResultText);
    }

    [Fact]
    public void A_missing_file_shows_the_error_chip()
    {
        _ui.OpenPath = Path.Combine(_work, "sumiu.ibtbackup");

        _vm.RestoreCommand.Execute(null);

        Assert.Equal(BackupResultKind.Error, _vm.ResultKind);
    }

    [Fact]
    public void Turning_the_automatic_copy_on_asks_for_the_folder_then_the_password_twice_and_keeps_the_password_protected()
    {
        var folder = Path.Combine(_work, "copias");
        Directory.CreateDirectory(folder);
        _ui.FolderPick = folder;
        _ui.NewPassword = Password;

        _vm.AutoEnabled = true;

        Assert.True(_vm.AutoEnabled);
        Assert.Equal(["folder", "password"], _ui.Order);
        var stored = _rig.Settings.Current.Backup;
        Assert.True(stored.AutoEnabled);
        Assert.Equal(folder, stored.Folder);
        Assert.Equal(_rig.Protector.Protect(Password), stored.PasswordProtected);
        Assert.NotEqual(Password, stored.PasswordProtected);
        _rig.Settings.Flush();
        Assert.DoesNotContain(Password, _rig.FileText(), StringComparison.Ordinal);
        Assert.Single(Directory.GetFiles(folder, "*.ibtbackup"));
        Assert.StartsWith("hoje", _vm.AutoText, StringComparison.Ordinal);
        Assert.Equal(StatusLight.Green, _vm.AutoLight);
    }

    [Fact]
    public void Cancelling_the_folder_or_the_password_leaves_the_automatic_copy_off()
    {
        _ui.FolderPick = null;
        _ui.NewPassword = Password;
        _vm.AutoEnabled = true;
        Assert.False(_vm.AutoEnabled);
        Assert.Equal(0, _ui.PasswordAsked);

        _ui.FolderPick = _work;
        _ui.NewPassword = null;
        _vm.AutoEnabled = true;

        Assert.False(_vm.AutoEnabled);
        Assert.False(_rig.Settings.Current.Backup.AutoEnabled);
        Assert.Null(_rig.Settings.Current.Backup.PasswordProtected);
    }

    [Fact]
    public void Turning_it_off_forgets_the_password_and_keeps_the_folder()
    {
        _ui.FolderPick = _work;
        _ui.NewPassword = Password;
        _vm.AutoEnabled = true;

        _vm.AutoEnabled = false;

        Assert.False(_vm.AutoEnabled);
        Assert.Null(_rig.Settings.Current.Backup.PasswordProtected);
        Assert.Equal(_work, _rig.Settings.Current.Backup.Folder);
        Assert.Equal("Desligada", _vm.AutoText);
    }

    [Fact]
    public void A_folder_that_vanished_turns_the_dot_red_with_the_reason_as_the_tip()
    {
        var folder = Path.Combine(_work, "copias");
        Directory.CreateDirectory(folder);
        _ui.FolderPick = folder;
        _ui.NewPassword = Password;
        _vm.AutoEnabled = true;
        Directory.Delete(folder, recursive: true);

        _rig.Settings.Update(s => s with { Bar = s.Bar with { Speed = 99 } });
        _rig.Clock.Advance(AutoBackupService.Debounce);

        Assert.Equal(StatusLight.Red, _vm.AutoLight);
        Assert.Equal("Falhou", _vm.AutoText);
        Assert.Contains("pasta", _vm.AutoTip, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Choosing_another_folder_moves_the_copies_there()
    {
        _ui.FolderPick = _work;
        _ui.NewPassword = Password;
        _vm.AutoEnabled = true;
        var other = Path.Combine(_work, "outra");
        Directory.CreateDirectory(other);
        _ui.FolderPick = other;

        _vm.ChooseFolderCommand.Execute(null);

        Assert.Equal(other, _rig.Settings.Current.Backup.Folder);
        Assert.Equal(other, _vm.FolderText);
        Assert.Single(Directory.GetFiles(other, "*.ibtbackup"));
    }

    [Fact]
    public void After_a_restore_the_settings_tab_and_the_correction_tab_read_the_new_values()
    {
        using var settings = new SettingsViewModel(
            _rig.Settings,
            _rig.Protector,
            _rig.Hub,
            _rig.Saver,
            new DiagnosticsViewModel(new LogFiles(Path.Combine(_work, "logs")), _ => { }, () => null),
            () => Task.CompletedTask);
        using var correction = new CorrectionViewModel(_rig.Settings, _rig.Protector, _rig.Hub, _rig.Saver);
        using var vm = new BackupViewModel(
            _rig.Settings,
            _rig.Protector,
            _auto,
            _rig.Saver,
            _ui,
            () =>
            {
                settings.ReloadFromSettings();
                correction.Reload();
            },
            clock: _rig.Clock);
        Assert.Equal(150, settings.BarSpeed);
        Assert.Equal("a=b", correction.RulesText);
        _ui.OpenPath = BackupOfOtherMachine();
        _ui.TypedPasswords = [Password];
        _ui.PreviewAnswer = true;
        _ui.ConfirmAnswer = true;

        vm.RestoreCommand.Execute(null);

        Assert.Equal(60, settings.BarSpeed);
        Assert.Equal(40, settings.BarHeight);
        Assert.True(settings.IsTop);
        Assert.Equal("-100999", settings.ChatIdText);
        Assert.Equal("https://outra-rtdb.firebaseio.com", settings.SiteUrl.Text);
        Assert.Equal("pc@outra.org", settings.SiteEmail.Text);
        Assert.Contains("P", settings.HotkeyDisplay, StringComparison.Ordinal);
        Assert.False(settings.AlertsEnabled);
        Assert.True(settings.Token.IsLocked);
        Assert.True(settings.SitePassword.IsLocked);
        Assert.Equal("x=y\nz=w", correction.RulesText);
        Assert.Equal("gemini-9-pro", correction.Model);
        Assert.True(correction.GeminiKey.IsLocked);

        // Reading the controls again saved nothing of its own: the file still equals the restored settings.
        _rig.Rest();
        Assert.Equal(_rig.Settings.Current.Bar, _rig.OnDisk().Bar);
        Assert.False(_rig.Saver.HasPending);
    }

    private sealed class FakeUi : IBackupUi
    {
        private int _typed;

        public List<string> Order { get; } = [];

        public string? SavePath { get; set; }

        public string? OpenPath { get; set; }

        public string? FolderPick { get; set; }

        public string? NewPassword { get; set; }

        public string[] TypedPasswords { get; set; } = [];

        public bool PreviewAnswer { get; set; }

        public bool ConfirmAnswer { get; set; }

        public string? SuggestedName { get; private set; }

        public int PasswordAsked { get; private set; }

        public int ConfirmAsked { get; private set; }

        public IReadOnlyList<BackupChange>? Preview { get; private set; }

        public List<string?> VerifyResults { get; } = [];

        public string? PickSavePath(string suggestedName)
        {
            SuggestedName = suggestedName;
            Order.Add("save");
            return SavePath;
        }

        public string? PickOpenPath()
        {
            Order.Add("open");
            return OpenPath;
        }

        public string? PickFolder(string? current)
        {
            Order.Add("folder");
            return FolderPick;
        }

        public string? AskNewPassword(string title)
        {
            PasswordAsked++;
            Order.Add("password");
            return NewPassword;
        }

        public string? AskPassword(string title, Func<string, string?> verify)
        {
            PasswordAsked++;
            Order.Add("password");
            while (_typed < TypedPasswords.Length)
            {
                var typed = TypedPasswords[_typed++];
                var problem = verify(typed);
                VerifyResults.Add(problem);
                if (problem is null)
                {
                    return typed;
                }
            }

            return null;
        }

        public bool ShowPreview(IReadOnlyList<BackupChange> changes)
        {
            Preview = changes;
            return PreviewAnswer;
        }

        public bool ConfirmRestore()
        {
            ConfirmAsked++;
            return ConfirmAnswer;
        }
    }
}
