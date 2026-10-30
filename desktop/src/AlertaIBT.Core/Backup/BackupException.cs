namespace AlertaIBT.Core.Backup;

/// <summary>Why a backup file could not be read.</summary>
public enum BackupError
{
    /// <summary>The file does not start like a backup of this app.</summary>
    NotABackup,

    /// <summary>The file was made by a newer format than this app knows.</summary>
    UnsupportedVersion,

    /// <summary>The file is shorter than the smallest valid backup (cut in the middle).</summary>
    Truncated,

    /// <summary>The tag did not verify: wrong password, or the file was damaged or changed (AES-GCM cannot tell them apart).</summary>
    WrongPasswordOrCorrupt,

    /// <summary>The password is shorter than <see cref="BackupCrypto.MinPasswordLength"/>.</summary>
    WeakPassword,

    /// <summary>The decrypted content is not a usable backup (wrong shape or values out of range).</summary>
    InvalidContent,

    /// <summary>The file could not be read from disk.</summary>
    Unreadable,
}

/// <summary>A backup that could not be made or read. <see cref="Exception.Message"/> is the pt-BR text shown to the person; it never holds a secret.</summary>
public sealed class BackupException : Exception
{
    /// <param name="error">What went wrong.</param>
    /// <param name="inner">The cause, when there is one.</param>
    public BackupException(BackupError error, Exception? inner = null)
        : base(MessageFor(error), inner)
    {
        Error = error;
    }

    /// <summary>What went wrong.</summary>
    public BackupError Error { get; }

    /// <summary>The text for <paramref name="error"/>.</summary>
    public static string MessageFor(BackupError error) => error switch
    {
        BackupError.NotABackup => "Este arquivo n\U000000E3o \U000000E9 um backup do IBT Controller.",
        BackupError.UnsupportedVersion => "Este backup \U000000E9 de uma vers\U000000E3o mais nova do IBT Controller. Atualize o app para restaurar.",
        BackupError.Truncated => "O arquivo est\U000000E1 incompleto (foi cortado).",
        BackupError.WrongPasswordOrCorrupt => "Senha incorreta ou arquivo alterado.",
        BackupError.WeakPassword => "A senha precisa ter pelo menos 8 caracteres.",
        BackupError.InvalidContent => "O backup est\U000000E1 danificado ou tem valores inv\U000000E1lidos.",
        _ => "N\U000000E3o foi poss\U000000EDvel ler o arquivo.",
    };
}
