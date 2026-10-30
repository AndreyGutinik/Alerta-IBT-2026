using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using AlertaIBT.Core.Backup;

namespace AlertaIBT.Core.Tests.Backup;

public sealed class BackupCryptoTests
{
    private const string Password = "senha-da-igreja-2026";
    private static readonly byte[] Plain = Encoding.UTF8.GetBytes("{\"segredo\":\"ABC-123-conteudo\"}");

    private static BackupError ErrorOf(Action act) => Assert.Throws<BackupException>(act).Error;

    [Fact]
    public void A_file_decrypts_back_to_the_same_bytes()
    {
        var file = BackupCrypto.Encrypt(Plain, Password);

        Assert.Equal(Plain, BackupCrypto.Decrypt(file, Password));
    }

    [Fact]
    public void Two_files_of_the_same_content_differ_because_salt_and_nonce_are_random()
    {
        var a = BackupCrypto.Encrypt(Plain, Password);
        var b = BackupCrypto.Encrypt(Plain, Password);

        Assert.NotEqual(a, b);
        Assert.NotEqual(a.AsSpan(13, 16).ToArray(), b.AsSpan(13, 16).ToArray()); // salt
        Assert.NotEqual(a.AsSpan(29, 12).ToArray(), b.AsSpan(29, 12).ToArray()); // nonce
    }

    [Fact]
    public void The_key_derivation_is_pbkdf2_sha256_with_at_least_600000_iterations_a_random_salt_and_an_aes256_gcm_key()
    {
        var file = BackupCrypto.Encrypt(Plain, Password);

        Assert.True(BackupCrypto.Iterations >= 600_000);
        Assert.Equal(32, BackupCrypto.KeySize);
        Assert.Equal(16, BackupCrypto.SaltSize);
        Assert.Equal(12, BackupCrypto.NonceSize);
        Assert.Equal(16, BackupCrypto.TagSize);
        Assert.Equal("IBTBACK\0", Encoding.ASCII.GetString(file, 0, 8));
        Assert.Equal(1, file[8]);
        Assert.Equal((uint)BackupCrypto.Iterations, BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(9, 4)));
        Assert.Equal(41 + Plain.Length + 16, file.Length);

        // The file is exactly what the documented recipe gives: PBKDF2-SHA256(password, salt, iterations) -> 32-byte AES-GCM key, header as associated data.
        var key = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(Password), file.AsSpan(13, 16), BackupCrypto.Iterations, HashAlgorithmName.SHA256, 32);
        var output = new byte[Plain.Length];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(file.AsSpan(29, 12), file.AsSpan(41, Plain.Length), file.AsSpan(41 + Plain.Length, 16), output, file.AsSpan(0, 41));
        Assert.Equal(Plain, output);
    }

    [Fact]
    public void A_wrong_password_is_refused_with_the_clear_message()
    {
        var file = BackupCrypto.Encrypt(Plain, Password);

        var ex = Assert.Throws<BackupException>(() => BackupCrypto.Decrypt(file, "outra-senha-qualquer"));

        Assert.Equal(BackupError.WrongPasswordOrCorrupt, ex.Error);
        Assert.Equal("Senha incorreta ou arquivo alterado.", ex.Message);
    }

    [Theory]
    [InlineData(10)] // iteration count
    [InlineData(15)] // salt
    [InlineData(35)] // nonce
    [InlineData(45)] // cipher text
    [InlineData(-1)] // tag (last byte)
    public void Flipping_one_byte_anywhere_after_the_magic_is_detected(int index)
    {
        var file = BackupCrypto.Encrypt(Plain, Password);
        file[index < 0 ? file.Length + index : index] ^= 0x01;

        Assert.Equal(BackupError.WrongPasswordOrCorrupt, ErrorOf(() => BackupCrypto.Decrypt(file, Password)));
    }

    [Fact]
    public void A_changed_magic_is_not_a_backup()
    {
        var file = BackupCrypto.Encrypt(Plain, Password);
        file[0] ^= 0x01;

        Assert.Equal(BackupError.NotABackup, ErrorOf(() => BackupCrypto.Decrypt(file, Password)));
        Assert.Equal(BackupError.NotABackup, ErrorOf(() => BackupCrypto.Decrypt(Encoding.UTF8.GetBytes("isto nao e um backup de nada, so texto qualquer comprido"), Password)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(20)]
    [InlineData(56)]
    public void A_file_cut_short_is_reported_as_incomplete(int length)
    {
        var file = BackupCrypto.Encrypt(Plain, Password);

        Assert.Equal(BackupError.Truncated, ErrorOf(() => BackupCrypto.Decrypt(file.AsSpan(0, length), Password)));
    }

    [Fact]
    public void A_file_cut_after_the_header_fails_the_tag()
    {
        var file = BackupCrypto.Encrypt(Plain, Password);

        Assert.Equal(BackupError.WrongPasswordOrCorrupt, ErrorOf(() => BackupCrypto.Decrypt(file.AsSpan(0, file.Length - 7), Password)));
    }

    [Fact]
    public void A_newer_format_version_is_refused_with_its_own_message()
    {
        var file = BackupCrypto.Encrypt(Plain, Password);
        file[8] = 2;

        var ex = Assert.Throws<BackupException>(() => BackupCrypto.Decrypt(file, Password));

        Assert.Equal(BackupError.UnsupportedVersion, ex.Error);
        Assert.Contains("mais nova", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_absurd_iteration_count_is_refused_before_any_work()
    {
        var file = BackupCrypto.Encrypt(Plain, Password);
        BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(9, 4), uint.MaxValue);

        Assert.Equal(BackupError.WrongPasswordOrCorrupt, ErrorOf(() => BackupCrypto.Decrypt(file, Password)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("1234567")]
    public void A_short_password_is_refused_when_making_a_backup(string password)
    {
        Assert.Equal(BackupError.WeakPassword, ErrorOf(() => BackupCrypto.Encrypt(Plain, password)));
        Assert.False(BackupCrypto.IsAcceptablePassword(password));
        Assert.True(BackupCrypto.IsAcceptablePassword("12345678"));
    }

    [Fact]
    public void The_file_holds_no_readable_text_of_the_content()
    {
        var file = BackupCrypto.Encrypt(Plain, Password);
        var text = Encoding.Latin1.GetString(file);

        Assert.DoesNotContain("ABC-123", text, StringComparison.Ordinal);
        Assert.DoesNotContain("segredo", text, StringComparison.Ordinal);
        Assert.DoesNotContain(Password, text, StringComparison.Ordinal);
    }
}
