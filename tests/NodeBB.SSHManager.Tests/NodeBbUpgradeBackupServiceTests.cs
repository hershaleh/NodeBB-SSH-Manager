using System.IO;
using System.IO.Compression;
using System.Text;
using NodeBB.SSHManager.Services;
using Xunit;

namespace NodeBB.SSHManager.Tests;

public sealed class NodeBbUpgradeBackupServiceTests
{
    [Theory]
    [InlineData("mongo", "mongo")]
    [InlineData("POSTGRES", "postgres")]
    [InlineData(" Redis ", "redis")]
    public void NormalizeDatabaseType_AcceptsSupportedEngines(string input, string expected) =>
        Assert.Equal(expected, NodeBbUpgradeBackupService.NormalizeDatabaseType(input));

    [Theory]
    [InlineData("mysql")]
    [InlineData("")]
    [InlineData("unknown")]
    public void NormalizeDatabaseType_RejectsUnsupportedEngines(string input) =>
        Assert.Throws<ArgumentException>(() => NodeBbUpgradeBackupService.NormalizeDatabaseType(input));

    [Theory]
    [InlineData("short")]
    [InlineData("")]
    public void ValidatePassword_RejectsWeakPasswords(string password) =>
        Assert.Throws<ArgumentException>(() => NodeBbUpgradeBackupService.ValidatePassword(password));

    [Fact]
    public void ValidatePassword_AcceptsTwelveOrMoreCharacters() =>
        NodeBbUpgradeBackupService.ValidatePassword("correct-horse-12");

    [Fact]
    public async Task ValidateGzipAsync_ValidatesFullCompressedStream()
    {
        var directory = Path.Combine(Path.GetTempPath(), "NodeBBUpgradeBackupTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var valid = Path.Combine(directory, "valid.gz");
        var invalid = Path.Combine(directory, "invalid.gz");
        try
        {
            await using (var file = File.Create(valid))
            await using (var gzip = new GZipStream(file, CompressionMode.Compress))
                await gzip.WriteAsync(Encoding.UTF8.GetBytes("NodeBB backup payload"));
            await File.WriteAllBytesAsync(invalid, Encoding.ASCII.GetBytes("not-a-gzip-archive"));
            await NodeBbUpgradeBackupService.ValidateGzipAsync(valid);
            await Assert.ThrowsAnyAsync<Exception>(() => NodeBbUpgradeBackupService.ValidateGzipAsync(invalid));
        }
        finally { Directory.Delete(directory, true); }
    }
}
