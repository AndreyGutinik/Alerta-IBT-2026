using System.IO;
using System.Globalization;
using AlertaIBT.Core.Backup;
using AlertaIBT.Core.Services;
using AlertaIBT.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AlertaIBT.App.ViewModels;

/// <summary>The windows the Backup card opens (file and folder pickers, the password and preview dialogs); a fake in tests.</summary>
public interface IBackupUi
{
    /// <summary>Asks where to save the backup; null when cancelled.</summary>
    string? PickSavePath(string suggestedName);

    /// <summary>Asks which backup file to open; null when cancelled.</summary>
    string? PickOpenPath();

    /// <summary>Asks for the folder of the automatic copies; null when cancelled.</summary>
    string? PickFolder(string? current);

    /// <summary>Asks for a new password twice (at least 8 characters); null when cancelled.</summary>
    string? AskNewPassword(string title);

    /// <summary>
    /// Asks for the password of a file. <paramref name="verify"/> gives the problem with a password typed (shown in the dialog, which stays open) or
    /// null when it works. Null when cancelled.
    /// </summary>
    string? AskPassword(string title, Func<string, string?> verify);

    /// <summary>Shows what the restore would replace; true to go on.</summary>
    bool ShowPreview(IReadOnlyList<BackupChange> changes);

    /// <summary>The last question before the restore (Restaurar / Cancelar); true to restore.</summary>
    bool ConfirmRestore();
}

/// <summary>How the last manual action ended.</summary>
public enum BackupResultKind
{
    /// <summary>Nothing done yet.</summary>
    None,

    /// <summary>It worked.</summary>
    Ok,

    /// <summary>It did not work.</summary>
    Error,
}

/// <summary>
/// The Backup card of Configurações: "Exportar" and "Restaurar" (manual) and the automatic copy (switch, folder, state). Restoring applies the
/// values through <see cref="SettingsService.Update"/> so the app reacts at once, and only after the preview and the confirmation.
/// </summary>
public sealed partial class BackupViewModel : ObservableObject, IDisposable
{
    private readonly SettingsService _settings;
    private readonly ISecretProtector _protector;
    private readonly AutoBackupService _auto;
    private readonly Autosaver _saver;
    private readonly IBackupUi _ui;
    private readonly Action<Action> _post;
    private readonly Action _onRestored;
    private readonly TimeProvider _clock;
    private readonly ILogger _logger;
    private bool _autoEnabled;
    private bool _disposed;

    [ObservableProperty]
    private string _resultText = string.Empty;

    [ObservableProperty]
    private BackupResultKind _resultKind;

    [ObservableProperty]
    private string _folderText = string.Empty;

    [ObservableProperty]
    private string _folderTip = string.Empty;

    [ObservableProperty]
    private StatusLight _autoLight = StatusLight.Gray;

    [ObservableProperty]
    private string _autoText = string.Empty;

    [ObservableProperty]
    private string _autoTip = string.Empty;

    /// <param name="settings">The settings the restore goes through.</param>
    /// <param name="protector">Reads and protects the secrets.</param>
    /// <param name="auto">The automatic copy.</param>
    /// <param name="saver">The autosave: its waiting changes are saved before a backup is read or a restore is applied.</param>
    /// <param name="ui">The dialogs.</param>
    /// <param name="onRestored">Runs after a restore, so the tabs read their controls again.</param>
    /// <param name="post">Runs an action on the UI thread; at once when null (tests).</param>
    /// <param name="clock">Names the manual file and dates "hoje"; the system clock by default.</param>
    /// <param name="logger">Receives failures (never a password or a secret).</param>
    public BackupViewModel(
        SettingsService settings,
        ISecretProtector protector,
        AutoBackupService auto,
        Autosaver saver,
        IBackupUi ui,
        Action onRestored,
        Action<Action>? post = null,
        TimeProvider? clock = null,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(protector);
        ArgumentNullException.ThrowIfNull(auto);
        ArgumentNullException.ThrowIfNull(saver);
        ArgumentNullException.ThrowIfNull(ui);
        ArgumentNullException.ThrowIfNull(onRestored);

        _settings = settings;
        _protector = protector;
        _auto = auto;
        _saver = saver;
        _ui = ui;
        _onRestored = onRestored;
        _post = post ?? (action => action());
        _clock = clock ?? TimeProvider.System;
        _logger = logger ?? NullLogger.Instance;
        ExportCommand = new RelayCommand(Export);
        RestoreCommand = new RelayCommand(Restore);
        ChooseFolderCommand = new RelayCommand(ChooseFolder);
        _autoEnabled = _settings.Current.Backup.AutoEnabled;
        Refresh(_auto.Status);
        _auto.StatusChanged += OnStatusChanged;
    }

    /// <summary>"Exportar": save dialog, password twice, the file.</summary>
    public IRelayCommand ExportCommand { get; }

    /// <summary>"Restaurar": open dialog, password, preview, confirmation, apply.</summary>
    public IRelayCommand RestoreCommand { get; }

    /// <summary>The folder picker of the automatic copy.</summary>
    public IRelayCommand ChooseFolderCommand { get; }

    /// <summary>Whether the result chip is shown.</summary>
    public bool HasResult => ResultKind != BackupResultKind.None;

    /// <summary>Whether the automatic copy is on. Turning it on asks for the folder (if none) and the password; cancelling leaves it off.</summary>
    public bool AutoEnabled
    {
        get => _autoEnabled;
        set
        {
            if (_autoEnabled == value)
            {
                return;
            }

            if (value ? TryEnable() : TryDisable())
            {
                _autoEnabled = value;
            }

            _post(() => OnPropertyChanged(nameof(AutoEnabled)));
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _auto.StatusChanged -= OnStatusChanged;
    }

    partial void OnResultKindChanged(BackupResultKind value) => OnPropertyChanged(nameof(HasResult));

    private static string When(DateTimeOffset at, DateTimeOffset now)
    {
        var local = at.ToLocalTime();
        var today = now.ToLocalTime().Date;
        var time = local.ToString("HH:mm", CultureInfo.InvariantCulture);
        if (local.Date == today)
        {
            return "hoje " + time;
        }

        return local.Date == today.AddDays(-1)
            ? "ontem " + time
            : local.ToString("dd/MM HH:mm", CultureInfo.InvariantCulture);
    }

    private void Export()
    {
        var path = _ui.PickSavePath(SettingsBackup.ManualFileName(_clock.GetLocalNow()));
        if (path is null)
        {
            return;
        }

        var password = _ui.AskNewPassword("Senha do backup");
        if (password is null)
        {
            return;
        }

        try
        {
            _saver.FlushNow();
            var bytes = SettingsBackup.Export(_settings.Current, _protector, password, _clock.GetUtcNow());
            BackupFiles.WriteAtomic(path, bytes);
            Report(BackupResultKind.Ok, "Backup salvo");
        }
        catch (BackupException ex)
        {
            Report(BackupResultKind.Error, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Backup manual nao foi gravado: {Reason}", ex.GetType().Name);
            Report(BackupResultKind.Error, "N\U000000E3o foi poss\U000000EDvel salvar o arquivo.");
        }
    }

    private void Restore()
    {
        var path = _ui.PickOpenPath();
        if (path is null)
        {
            return;
        }

        byte[] file;
        try
        {
            file = File.ReadAllBytes(path);
            BackupCrypto.CheckHeader(file);
        }
        catch (BackupException ex)
        {
            Report(BackupResultKind.Error, ex.Message);
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Report(BackupResultKind.Error, BackupException.MessageFor(BackupError.Unreadable));
            return;
        }

        BackupPayload? payload = null;
        var password = _ui.AskPassword(
            "Senha do backup",
            typed =>
            {
                try
                {
                    payload = SettingsBackup.Read(file, typed);
                    return null;
                }
                catch (BackupException ex)
                {
                    return ex.Message;
                }
            });
        if (password is null || payload is null)
        {
            return;
        }

        _saver.FlushNow();
        if (!_ui.ShowPreview(SettingsBackup.Preview(_settings.Current, payload, _protector)) || !_ui.ConfirmRestore())
        {
            return;
        }

        try
        {
            var chosen = payload;
            _saver.FlushNow();
            _settings.Update(current => SettingsBackup.Apply(current, chosen, _protector));
            _settings.Flush();
            _onRestored();
            Report(BackupResultKind.Ok, "Restaurado");
        }
        catch (Exception ex)
        {
            _logger.LogError("Falha ao restaurar o backup: {Reason}", ex.GetType().Name);
            Report(BackupResultKind.Error, "N\U000000E3o foi poss\U000000EDvel restaurar. Veja o registro (log).");
        }
    }

    private void ChooseFolder()
    {
        var folder = _ui.PickFolder(_settings.Current.Backup.Folder);
        if (folder is null)
        {
            return;
        }

        _auto.ChangeFolder(folder);
    }

    private bool TryEnable()
    {
        var folder = _settings.Current.Backup.Folder;
        if (folder.Length == 0)
        {
            folder = _ui.PickFolder(null) ?? string.Empty;
            if (folder.Length == 0)
            {
                return false;
            }
        }

        var password = _ui.AskNewPassword("Senha da c\U000000F3pia autom\U000000E1tica");
        if (password is null)
        {
            return false;
        }

        try
        {
            _auto.Enable(folder, password);
            return true;
        }
        catch (BackupException ex)
        {
            Report(BackupResultKind.Error, ex.Message);
            return false;
        }
    }

    private bool TryDisable()
    {
        _auto.Disable();
        return true;
    }

    private void Report(BackupResultKind kind, string text)
    {
        ResultText = text;
        ResultKind = kind;
    }

    private void OnStatusChanged(AutoBackupStatus status) => _post(() => Refresh(status));

    private void Refresh(AutoBackupStatus status)
    {
        if (_disposed)
        {
            return;
        }

        _autoEnabled = status.Enabled;
        OnPropertyChanged(nameof(AutoEnabled));
        FolderText = status.Folder.Length == 0 ? "Nenhuma pasta" : status.Folder;
        FolderTip = status.Folder.Length == 0 ? "Escolher a pasta das c\U000000F3pias" : status.Folder;
        var now = _clock.GetUtcNow();
        if (!status.Enabled)
        {
            AutoLight = StatusLight.Gray;
            AutoText = "Desligada";
            AutoTip = "A c\U000000F3pia autom\U000000E1tica est\U000000E1 desligada.";
        }
        else if (status.Error is not null)
        {
            AutoLight = StatusLight.Red;
            AutoText = "Falhou";
            AutoTip = status.Error + " Tenta de novo na pr\U000000F3xima mudan\U000000E7a.";
        }
        else if (status.LastCopyAt is { } at)
        {
            AutoLight = StatusLight.Green;
            AutoText = When(at, now);
            AutoTip = "\U000000DAltima c\U000000F3pia: " + at.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.InvariantCulture);
        }
        else
        {
            AutoLight = StatusLight.Yellow;
            AutoText = "Aguardando";
            AutoTip = "Nenhuma c\U000000F3pia feita ainda.";
        }
    }
}
