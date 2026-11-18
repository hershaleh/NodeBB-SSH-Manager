using System.IO.Compression;
using NodeBB.SSHManager.Services;
using Xunit;

namespace NodeBB.SSHManager.Tests;

public sealed class MongoDatabaseRestoreServiceTests
{
    private const string Work = "/tmp/nodebb-mongo-restore-0123456789abcdef0123456789abcdef";

    [Fact]
    public void BuildRestoreScript_UsesDryRunRollbackAndProtectedConfig()
    {
        var script = MongoDatabaseRestoreService.BuildRestoreScript("/srv/nodebb", Work);
        Assert.Contains("mongorestore --dryRun", script);
        Assert.Contains("mongodump --config=", script);
        Assert.Contains("rollback.archive.gz", script);
        Assert.Contains("KEEP_WORK=1", script);
        Assert.DoesNotContain("--password", script);
    }

    [Theory]
    [InlineData("/tmp/not-restore", false)]
    [InlineData("/tmp/nodebb-mongo-restore-ABCDEF0123456789ABCDEF0123456789", false)]
    [InlineData(Work, true)]
    public void BuildRestoreScript_ValidatesWorkPath(string path, bool valid)
    {
        if (valid) Assert.NotNull(MongoDatabaseRestoreService.BuildRestoreScript("/srv/nodebb", path));
        else Assert.Throws<ArgumentException>(() => MongoDatabaseRestoreService.BuildRestoreScript("/srv/nodebb", path));
    }

    [Fact]
    public void BuildRestoreScript_QuotesInstallationPath()
    {
        var script = MongoDatabaseRestoreService.BuildRestoreScript("/srv/nodebb's forum", Work);
        Assert.Contains("'/srv/nodebb'\\''s forum'", script);
    }

    [Fact]
    public async Task ValidateArchive_AcceptsValidGzipAndRejectsCorruption()
    {
        var dir = Path.Combine(Path.GetTempPath(), "NodeBBRestoreTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var valid = Path.Combine(dir, "valid.gz");
        var invalid = Path.Combine(dir, "invalid.gz");
        try
        {
            await using (var file = File.Create(valid))
            await using (var gzip = new GZipStream(file, CompressionMode.Compress))
            {
                var payload = System.Text.Encoding.UTF8.GetBytes("mongodump archive payload");
                await gzip.WriteAsync(payload);
            }
            await MongoDatabaseRestoreService.ValidateArchiveAsync(valid);
            await File.WriteAllTextAsync(invalid, "not gzip");
            await Assert.ThrowsAsync<InvalidDataException>(() => MongoDatabaseRestoreService.ValidateArchiveAsync(invalid));
        }
        finally { Directory.Delete(dir, true); }
    }
}