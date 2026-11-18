using NodeBB.SSHManager.Services;
using Xunit;
namespace NodeBB.SSHManager.Tests;
public sealed class MongoDatabaseBackupServiceTests
{
    [Fact] public void Script_UsesProtectedConfigAndNoCredentialArguments()
    {
        var script = MongoDatabaseBackupService.BuildBackupScript("/srv/nodebb", "/tmp/nodebb-mongo-backup-0123456789abcdef0123456789abcdef");
        Assert.Contains("umask 077", script); Assert.Contains("mkdir -m 700", script); Assert.Contains("mongodump --config=", script);
        Assert.DoesNotContain("--password", script); Assert.DoesNotContain("mongodb://user:password@", script); Assert.DoesNotContain("trap cleanup EXIT", script);
        Assert.Contains("database.archive.gz", script);
    }
    [Theory]
    [InlineData("/tmp/untrusted", false)]
    [InlineData("/tmp/nodebb-mongo-backup-ABCDEF0123456789ABCDEF0123456789", false)]
    [InlineData("/tmp/nodebb-mongo-backup-0123456789abcdef0123456789abcdef", true)]
    public void Script_ValidatesTemporaryDirectory(string path, bool valid)
    {
        if (valid) Assert.NotNull(MongoDatabaseBackupService.BuildBackupScript("/srv/nodebb", path));
        else Assert.Throws<ArgumentException>(() => MongoDatabaseBackupService.BuildBackupScript("/srv/nodebb", path));
    }
    [Fact] public void Script_ShellQuotesInstallationPath()
    {
        var script = MongoDatabaseBackupService.BuildBackupScript("/srv/nodebb's forum", "/tmp/nodebb-mongo-backup-0123456789abcdef0123456789abcdef");
        Assert.Contains("'/srv/nodebb'\\''s forum'", script);
    }
}