using System.IO;
using System.Text;
using NodeBB.SSHManager.Services;
using Xunit;

namespace NodeBB.SSHManager.Tests;

public sealed class PostgresDatabaseRestoreServiceTests
{
    private const string Work = "/tmp/nodebb-postgres-restore-0123456789abcdef0123456789abcdef";

    [Fact]
    public void BuildRestoreScript_ValidatesArchiveAndCreatesRollbackBeforeRestore()
    {
        var script = PostgresDatabaseRestoreService.BuildRestoreScript("/srv/nodebb", Work);
        Assert.Contains("pg_restore --list", script);
        Assert.Contains(";     dbname: $PGDATABASE", script);
        Assert.Contains("pg_dump --no-password --format=custom", script);
        Assert.Contains("rollback.dump", script);
        Assert.Contains("KEEP_WORK=1", script);
        Assert.Contains("--single-transaction", script);
        Assert.Contains("--clean --if-exists", script);
        Assert.Contains("exit 41", script);
        Assert.Contains("exit 42", script);
        Assert.DoesNotContain("--password", script);
        Assert.DoesNotContain("PGPASSWORD=", script);
    }

    [Fact]
    public void BuildRestoreScript_QuotesInstallationPath()
    {
        var script = PostgresDatabaseRestoreService.BuildRestoreScript("/srv/nodebb's forum", Work);
        Assert.Contains("'/srv/nodebb'\\\\''s forum'", script);
    }

    [Theory]
    [InlineData("/tmp/not-restore", false)]
    [InlineData("/tmp/nodebb-postgres-restore-ABCDEF0123456789ABCDEF0123456789", false)]
    [InlineData(Work, true)]
    public void BuildRestoreScript_ValidatesWorkPath(string path, bool valid)
    {
        if (valid) Assert.NotNull(PostgresDatabaseRestoreService.BuildRestoreScript("/srv/nodebb", path));
        else Assert.Throws<ArgumentException>(() => PostgresDatabaseRestoreService.BuildRestoreScript("/srv/nodebb", path));
    }

    [Fact]
    public async Task ValidateDump_AcceptsCustomFormatAndRejectsOtherFiles()
    {
        var directory = Path.Combine(Path.GetTempPath(), "NodeBBPostgresRestoreTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var good = Path.Combine(directory, "good.dump");
        var bad = Path.Combine(directory, "bad.dump");
        try
        {
            await File.WriteAllBytesAsync(good, Encoding.ASCII.GetBytes("PGDMPpayload"));
            await File.WriteAllTextAsync(bad, "not a postgres dump");
            await PostgresDatabaseRestoreService.ValidateDumpAsync(good);
            await Assert.ThrowsAsync<InvalidDataException>(() => PostgresDatabaseRestoreService.ValidateDumpAsync(bad));
        }
        finally { Directory.Delete(directory, true); }
    }
}
