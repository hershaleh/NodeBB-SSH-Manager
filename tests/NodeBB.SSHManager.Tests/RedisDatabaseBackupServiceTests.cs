using System.IO;
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
    public async Task ValidateRdb_AcceptsRedisHeaderAndRejectsOtherFiles()
    {
        var dir = Path.Combine(Path.GetTempPath(), "NodeBBRedisTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var good = Path.Combine(dir, "good.rdb");
        var bad = Path.Combine(dir, "bad.rdb");
        try
        {
            await File.WriteAllBytesAsync(good, Encoding.ASCII.GetBytes("REDIS0011payload"));
            await File.WriteAllTextAsync(bad, "not a redis dump");
            await RedisDatabaseBackupService.ValidateRdbAsync(good);
            await Assert.ThrowsAsync<InvalidDataException>(() => RedisDatabaseBackupService.ValidateRdbAsync(bad));
        }
        finally { Directory.Delete(dir, true); }
    }
}
