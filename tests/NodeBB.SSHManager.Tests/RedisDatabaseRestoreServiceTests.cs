using System;
using System.IO;
using System.Threading.Tasks;
using NodeBB.SSHManager.Services;
using Xunit;

namespace NodeBB.SSHManager.Tests;

public sealed class RedisDatabaseRestoreServiceTests
{
    private const string Work = "/tmp/nodebb-redis-restore-0123456789abcdef0123456789abcdef";
    private const string Hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public void BuildRestoreScript_MarksRecoveryBeforeStopAndNeverOverwritesRdbUntilServiceIsInactive()
    {
        var script = RedisDatabaseRestoreService.BuildRestoreScript("/srv/nodebb", Work, Hash);
        var cleanupStart = script.IndexOf("cleanup()", StringComparison.Ordinal);
        var markChanges = script.IndexOf("CHANGES_STARTED=1", cleanupStart, StringComparison.Ordinal);
        var requestStop = script.IndexOf("systemctl stop \"$SERVICE_UNIT\"", markChanges, StringComparison.Ordinal);
        var cleanupStop = script.IndexOf("systemctl stop \"$SERVICE_UNIT\"", cleanupStart, StringComparison.Ordinal);
        var verifyInactive = script.IndexOf("systemctl is-active --quiet \"$SERVICE_UNIT\"", cleanupStart, StringComparison.Ordinal);
        var restoreCopy = script.IndexOf("cp -p -- \"$WORK/original.rdb\"", cleanupStart, StringComparison.Ordinal);

        Assert.True(cleanupStart >= 0 && markChanges > cleanupStart && requestStop > markChanges);
        Assert.True(cleanupStop > cleanupStart && verifyInactive > cleanupStop && restoreCopy > verifyInactive);
        Assert.Contains("Never replace an RDB file while Redis may still be writing to or loading it.", script);
        Assert.Contains("SERVICE_STOPPED", script);
        Assert.Contains("ROLLBACK_OK", script);
    }

    [Fact]
    public void BuildRestoreScript_UsesFullServerScopeAndProtectedRecovery()
    {
        var script = RedisDatabaseRestoreService.BuildRestoreScript("/srv/nodebb", Work, Hash);
        Assert.Contains("systemctl stop", script);
        Assert.Contains("systemctl start", script);
        Assert.Contains("CONFIG GET dir dbfilename", script);
        Assert.Contains("INFO replication", script);
        Assert.Contains("REDISCLI_AUTH", script);
        Assert.Contains("original.rdb", script);
        Assert.Contains("CHANGES_STARTED", script);
        Assert.Contains("sha256sum", script);
        Assert.Contains("REDIS_FULL_RESTORE_OK", script);
        Assert.Contains("127.0.0.1", script);
        Assert.Contains("ss -ltnp", script);
    }

    [Theory]
    [InlineData("/tmp/not-restore", false)]
    [InlineData("/tmp/nodebb-redis-restore-ABCDEF0123456789ABCDEF0123456789", false)]
    [InlineData(Work, true)]
    public void BuildRestoreScript_ValidatesWorkDirectory(string path, bool valid)
    {
        if (valid) Assert.NotNull(RedisDatabaseRestoreService.BuildRestoreScript("/srv/nodebb", path, Hash));
        else Assert.Throws<ArgumentException>(() => RedisDatabaseRestoreService.BuildRestoreScript("/srv/nodebb", path, Hash));
    }

    [Theory]
    [InlineData("not-a-hash")]
    [InlineData("aaaaaaaa")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaZ")]
    public void BuildRestoreScript_RejectsInvalidExpectedHash(string hash)
    {
        Assert.Throws<ArgumentException>(() => RedisDatabaseRestoreService.BuildRestoreScript("/srv/nodebb", Work, hash));
    }

    [Fact]
    public void BuildRestoreScript_QuotesInstallationPath()
    {
        var script = RedisDatabaseRestoreService.BuildRestoreScript("/srv/my nodebb's forum", Work, Hash);
        Assert.Contains("'/srv/my nodebb'\\''s forum'", script);
    }

    [Fact]
    public void BuildRestoreScript_RejectsUnsafeInstallationPath()
    {
        Assert.Throws<ArgumentException>(() => RedisDatabaseRestoreService.BuildRestoreScript(" ", Work, Hash));
        Assert.Throws<ArgumentException>(() => RedisDatabaseRestoreService.BuildRestoreScript("/srv/nodebb\0bad", Work, Hash));
    }

    [Fact]
    public async Task RestoreInputValidation_RejectsInvalidRdbBeforeAnyRemoteOperation()
    {
        var directory = Path.Combine(Path.GetTempPath(), "NodeBBRedisRestoreTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var invalid = Path.Combine(directory, "invalid.rdb");
        try
        {
            await File.WriteAllTextAsync(invalid, "not an RDB");
            await Assert.ThrowsAsync<InvalidDataException>(() => RedisDatabaseBackupService.ValidateRdbAsync(invalid));
        }
        finally { Directory.Delete(directory, true); }
    }
}
