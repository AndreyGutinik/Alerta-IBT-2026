using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace AlertaIBT.Core.Backup;

/// <summary>
/// The envelope of a <c>.ibtbackup</c> file. Layout (all built-in .NET, no package): 8 bytes of magic <c>IBTBACK\0</c>, 1 byte of format
/// version, 4 bytes of PBKDF2 iterations (big endian), 16 random bytes of salt, 12 random bytes of nonce, then the AES-256-GCM cipher text and
/// its 16-byte tag. The key is PBKDF2-SHA256 of the password with that salt. The whole header is the GCM associated data, so changing the
/// iteration count, the salt or the version breaks the tag too. Nothing readable is in the file: not a name, not a value.
/// </summary>
public static class BackupCrypto
{
    /// <summary>The file extension, with the dot.</summary>
    public const string Extension = ".ibtbackup";

    /// <summary>The shortest password accepted when making a backup.</summary>
    public const int MinPasswordLength = 8;

    /// <summary>The format version this app writes and reads.</summary>
    public const byte FormatVersion = 1;

    /// <summary>PBKDF2-SHA256 iterations used for new files (the OWASP minimum for SHA-256 is 600000).</summary>
    public const int Iterations = 600_000;

    /// <summary>Most iterations a file may ask for: a bigger number is a damaged header, not a reason to freeze the app.</summary>
    public const int MaxIterations = 10_000_000;

    /// <summary>Bytes of salt.</summary>
    public const int SaltSize = 16;

    /// <summary>Bytes of GCM nonce.</summary>
    public const int NonceSize = 12;

    /// <summary>Bytes of GCM tag.</summary>
    public const int TagSize = 16;

    /// <summary>Bytes of the AES key (256 bits).</summary>
    public const int KeySize = 32;

    private const int MagicSize = 8;
    private const int HeaderSize = MagicSize + 1 + 4 + SaltSize + NonceSize;

    private static readonly byte[] Magic = "IBTBACK\0"u8.ToArray();

    /// <summary>The size of the smallest valid file (header and tag, empty content).</summary>
    public const int MinFileSize = HeaderSize + TagSize;

    /// <summary>Whether <paramref name="password"/> may be used for a new backup.</summary>
    public static bool IsAcceptablePassword(string? password) => password is not null && password.Length >= MinPasswordLength;

    /// <summary>Encrypts <paramref name="plaintext"/> with a key made from <paramref name="password"/>; a new salt and nonce every time.</summary>
    /// <exception cref="BackupException"><see cref="BackupError.WeakPassword"/> when the password is too short.</exception>
    public static byte[] Encrypt(ReadOnlySpan<byte> plaintext, string password)
    {
        if (!IsAcceptablePassword(password))
        {
            throw new BackupException(BackupError.WeakPassword);
        }

        var file = new byte[HeaderSize + plaintext.Length + TagSize];
        var span = file.AsSpan();
        Magic.CopyTo(span);
        span[MagicSize] = FormatVersion;
        BinaryPrimitives.WriteUInt32BigEndian(span.Slice(MagicSize + 1, 4), Iterations);
        RandomNumberGenerator.Fill(span.Slice(MagicSize + 5, SaltSize));
        RandomNumberGenerator.Fill(span.Slice(MagicSize + 5 + SaltSize, NonceSize));

        var key = DeriveKey(password, span.Slice(MagicSize + 5, SaltSize), Iterations);
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Encrypt(
                span.Slice(MagicSize + 5 + SaltSize, NonceSize),
                plaintext,
                span.Slice(HeaderSize, plaintext.Length),
                span.Slice(HeaderSize + plaintext.Length, TagSize),
                span[..HeaderSize]);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }

        return file;
    }

    /// <summary>Checks the magic, the size and the version of <paramref name="file"/> without any password work.</summary>
    /// <exception cref="BackupException">Not a backup, cut short, or from a newer version.</exception>
    public static void CheckHeader(ReadOnlySpan<byte> file)
    {
        if (file.Length < MagicSize)
        {
            throw new BackupException(BackupError.Truncated);
        }

        if (!file[..MagicSize].SequenceEqual(Magic))
        {
            throw new BackupException(BackupError.NotABackup);
        }

        if (file.Length < MinFileSize)
        {
            throw new BackupException(BackupError.Truncated);
        }

        if (file[MagicSize] > FormatVersion)
        {
            throw new BackupException(BackupError.UnsupportedVersion);
        }

        if (file[MagicSize] != FormatVersion)
        {
            throw new BackupException(BackupError.NotABackup);
        }
    }

    /// <summary>Decrypts a file made by <see cref="Encrypt"/>.</summary>
    /// <exception cref="BackupException">Not a backup, a newer version, cut short, or the tag did not verify (wrong password, damaged or changed).</exception>
    public static byte[] Decrypt(ReadOnlySpan<byte> file, string password)
    {
        CheckHeader(file);

        var iterations = BinaryPrimitives.ReadUInt32BigEndian(file.Slice(MagicSize + 1, 4));
        if (iterations < Iterations || iterations > MaxIterations)
        {
            throw new BackupException(BackupError.WrongPasswordOrCorrupt);
        }

        var cipherLength = file.Length - HeaderSize - TagSize;
        var plaintext = new byte[cipherLength];
        var key = DeriveKey(password ?? string.Empty, file.Slice(MagicSize + 5, SaltSize), (int)iterations);
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(
                file.Slice(MagicSize + 5 + SaltSize, NonceSize),
                file.Slice(HeaderSize, cipherLength),
                file.Slice(HeaderSize + cipherLength, TagSize),
                plaintext,
                file[..HeaderSize]);
        }
        catch (CryptographicException ex)
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw new BackupException(BackupError.WrongPasswordOrCorrupt, ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }

        return plaintext;
    }

    private static byte[] DeriveKey(string password, ReadOnlySpan<byte> salt, int iterations)
    {
        var bytes = Encoding.UTF8.GetBytes(password.Normalize(NormalizationForm.FormKC));
        try
        {
            return Rfc2898DeriveBytes.Pbkdf2(bytes, salt, iterations, HashAlgorithmName.SHA256, KeySize);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }
}
