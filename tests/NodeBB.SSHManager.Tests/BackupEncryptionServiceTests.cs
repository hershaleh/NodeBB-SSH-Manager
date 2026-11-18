using System.IO;
using System.Security.Cryptography;
using NodeBB.SSHManager.Services;
using Xunit;

namespace NodeBB.SSHManager.Tests;

public sealed class BackupEncryptionServiceTests
{
    [Fact]
    public async Task EncryptAndDecrypt_RoundTripsLargePayload()
    {
        var directory = Path.Combine(Path.GetTempPath(), "NodeBBBackupTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var source = Path.Combine(directory, "source.tar.gz");
        var encrypted = Path.Combine(directory, "backup.nbbbackup");
        var decrypted = Path.Combine(directory, "restored.tar.gz");
        var data = new byte[2 * 1024 * 1024 + 317];
        RandomNumberGenerator.Fill(data);
        await File.WriteAllBytesAsync(source, data);
        try
        {
            await BackupEncryptionService.EncryptFileAsync(source, encrypted, "correct-horse-battery-2026");
            Assert.NotEqual(data, await File.ReadAllBytesAsync(encrypted));
            await BackupEncryptionService.DecryptFileAsync(encrypted, decrypted, "correct-horse-battery-2026");
            Assert.Equal(data, await File.ReadAllBytesAsync(decrypted));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Decrypt_RejectsWrongPasswordAndTampering()
    {
        var directory = Path.Combine(Path.GetTempPath(), "NodeBBBackupTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var source = Path.Combine(directory, "source.tar.gz");
        var encrypted = Path.Combine(directory, "backup.nbbbackup");
        var output = Path.Combine(directory, "restored.tar.gz");
        await File.WriteAllTextAsync(source, "private backup payload");
        try
        {
            await BackupEncryptionService.EncryptFileAsync(source, encrypted, "correct-horse-battery-2026");
            await Assert.ThrowsAsync<CryptographicException>(() =>
                BackupEncryptionService.DecryptFileAsync(encrypted, output, "incorrect-password-2026"));
            var bytes = await File.ReadAllBytesAsync(encrypted);
            bytes[50] ^= 0x40;
            await File.WriteAllBytesAsync(encrypted, bytes);
            await Assert.ThrowsAsync<CryptographicException>(() =>
                BackupEncryptionService.DecryptFileAsync(encrypted, output, "correct-horse-battery-2026"));
            Assert.False(File.Exists(output));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Encrypt_RequiresStrongPassword()
    {
        var source = Path.GetTempFileName();
        try
        {
            await Assert.ThrowsAsync<ArgumentException>(() =>
                BackupEncryptionService.EncryptFileAsync(source, source + ".enc", "short"));
        }
        finally { File.Delete(source); }
    }
}
