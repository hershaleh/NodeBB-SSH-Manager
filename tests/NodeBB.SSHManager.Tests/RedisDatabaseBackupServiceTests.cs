using System;
using System.IO;
using System.Buffers.Binary;
using System.Text;
using NodeBB.SSHManager.Services;
using Xunit;

namespace NodeBB.SSHManager.Tests;

public sealed class RedisDatabaseBackupServiceTests
{
    private const string Work = "/tmp/nodebb-redis-backup-0123456789abcdef0123456789abcdef";

    [Fact]
    public void BuildScript_UsesProtectedEnvironmentAndDoesNotExposePasswordArguments()
    {
        var script = RedisDatabaseBackupService.BuildBackupScript("/srv/nodebb", Work);
        Assert.Contains("umask 077", script);
        Assert.Contains("chmod 600", script);
        Assert.Contains("REDISCLI_AUTH", script);
        Assert.Contains("redis-cli", script);
        Assert.Contains("--rdb", script);
        Assert.DoesNotContain("--pass", script);
        Assert.DoesNotContain("redis://user:password@", script);
    }

    [Fact]
    public void BuildScript_SupportsTlsAndAclUserWithoutShellInterpolation()
    {
        var script = RedisDatabaseBackupService.BuildBackupScript("/srv/nodebb", Work);
        Assert.Contains("--tls", script);
        Assert.Contains("--user", script);
        Assert.Contains("REDIS_TLS", script);
    }

    [Theory]
    [InlineData("/tmp/untrusted", false)]
    [InlineData("/tmp/nodebb-redis-backup-ABCDEF0123456789ABCDEF0123456789", false)]
    [InlineData(Work, true)]
    public void BuildScript_ValidatesWorkDirectory(string path, bool valid)
    {
        if (valid) Assert.NotNull(RedisDatabaseBackupService.BuildBackupScript("/srv/nodebb", path));
        else Assert.Throws<ArgumentException>(() => RedisDatabaseBackupService.BuildBackupScript("/srv/nodebb", path));
    }

    [Fact]
    public void RedisCrc64_MatchesKnownJonesCheckValue()
    {
        Assert.Equal(0xCAA717168609F281UL,
            RedisDatabaseBackupService.ComputeRedisCrc64(Encoding.ASCII.GetBytes("123456789")));
    }

    [Fact]
    public async Task ValidateRdb_AcceptsValidChecksumAndDisabledChecksum()
    {
        var dir = CreateTempDirectory();
        try
        {
            var valid = Path.Combine(dir, "valid.rdb");
            var disabled = Path.Combine(dir, "disabled-checksum.rdb");
            await File.WriteAllBytesAsync(valid, CreateRdb(withChecksum: true));
            await File.WriteAllBytesAsync(disabled, CreateRdb(withChecksum: false));

            await RedisDatabaseBackupService.ValidateRdbAsync(valid);
            await RedisDatabaseBackupService.ValidateRdbAsync(disabled);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task ValidateRdb_RejectsBadHeaderAndChecksum()
    {
        var dir = CreateTempDirectory();
        try
        {
            var badHeader = Path.Combine(dir, "bad-header.rdb");
            var badChecksum = Path.Combine(dir, "bad-checksum.rdb");
            var headerBytes = CreateRdb(withChecksum: true);
            headerBytes[0] = (byte)'X';
            await File.WriteAllBytesAsync(badHeader, headerBytes);
            var checksumBytes = CreateRdb(withChecksum: true);
            checksumBytes[^1] ^= 0x80;
            await File.WriteAllBytesAsync(badChecksum, checksumBytes);

            await Assert.ThrowsAsync<InvalidDataException>(() => RedisDatabaseBackupService.ValidateRdbAsync(badHeader));
            await Assert.ThrowsAsync<InvalidDataException>(() => RedisDatabaseBackupService.ValidateRdbAsync(badChecksum));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData("REDIS0011payload")]
    [InlineData("REDISabcd")]
    public async Task ValidateRdb_RejectsTruncatedOrMalformedFiles(string contents)
    {
        var dir = CreateTempDirectory();
        var path = Path.Combine(dir, "invalid.rdb");
        try
        {
            await File.WriteAllTextAsync(path, contents);
            await Assert.ThrowsAsync<InvalidDataException>(() => RedisDatabaseBackupService.ValidateRdbAsync(path));
        }
        finally { Directory.Delete(dir, true); }
    }

    private static byte[] CreateRdb(bool withChecksum)
    {
        // Minimal synthetic RDB envelope: header, opaque payload, EOF opcode and checksum.
        var prefix = Encoding.ASCII.GetBytes("REDIS0011");
        var payload = new byte[] { 0xFE, 0x00, 0x00, 0xFF };
        var content = new byte[prefix.Length + payload.Length + sizeof(ulong)];
        prefix.CopyTo(content, 0);
        payload.CopyTo(content, prefix.Length);
        if (withChecksum)
        {
            var checksum = RedisDatabaseBackupService.ComputeRedisCrc64(content.AsSpan(0, content.Length - sizeof(ulong)));
            BinaryPrimitives.WriteUInt64LittleEndian(content.AsSpan(content.Length - sizeof(ulong)), checksum);
        }
        return content;
    }

    private static string CreateTempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "NodeBBRedisTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
