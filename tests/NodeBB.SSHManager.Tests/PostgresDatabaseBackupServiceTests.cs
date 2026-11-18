using System.Text;
using NodeBB.SSHManager.Services;
using Xunit;

namespace NodeBB.SSHManager.Tests;

public sealed class PostgresDatabaseBackupServiceTests
{
    private const string Work = "/tmp/nodebb-postgres-backup-0123456789abcdef0123456789abcdef";

    [Fact]
    public void BuildScript_UsesProtectedPgpassAndNeverPassesPasswordAsArgument()
    {
        var script = PostgresDatabaseBackupService.BuildBackupScript("/srv/nodebb", Work);
        Assert.Contains("umask 077", script);
        Assert.Contains("mkdir -m 700", script);
        Assert.Contains("chmod 600", script);
        Assert.Contains("PGPASSFILE", script);
        Assert.Contains("pg_dump --no-password --format=custom", script);
        Assert.DoesNotContain("--password", script);
        Assert.DoesNotContain("postgres://user:password@", script);
    }

    [Fact]
    public void BuildScript_ParsesUriAndEscapesPgpassFields()
    {
        var script = PostgresDatabaseBackupService.BuildBackupScript("/srv/nodebb", Work);
        Assert.Contains("postgresql:", script);
        Assert.Contains("replace(/:/g", script);
        Assert.Contains("sslmode", script);
    }

    [Theory]
    [InlineData("/tmp/untrusted", false)]
    [InlineData("/tmp/nodebb-postgres-backup-ABCDEF0123456789ABCDEF0123456789", false)]
    [InlineData(Work, true)]
    public void BuildScript_ValidatesWorkDirectory(string path, bool valid)
    {
        if (valid) Assert.NotNull(PostgresDatabaseBackupService.BuildBackupScript("/srv/nodebb", path));
        else Assert.Throws<ArgumentException>(() => PostgresDatabaseBackupService.BuildBackupScript("/srv/nodebb", path));
    }

    [Fact]
    public async Task ValidateDump_AcceptsCustomFormatAndRejectsOtherFiles()
    {
        var dir = Path.Combine(Path.GetTempPath(), "NodeBBPostgresTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var good = Path.Combine(dir, "good.dump");
        var bad = Path.Combine(dir, "bad.dump");
        try
        {
            await File.WriteAllBytesAsync(good, Encoding.ASCII.GetBytes("PGDMPpayload"));
            await File.WriteAllTextAsync(bad, "not a postgres dump");
            await PostgresDatabaseBackupService.ValidateDumpAsync(good);
            await Assert.ThrowsAsync<InvalidDataException>(() => PostgresDatabaseBackupService.ValidateDumpAsync(bad));
        }
        finally { Directory.Delete(dir, true); }
    }
}
