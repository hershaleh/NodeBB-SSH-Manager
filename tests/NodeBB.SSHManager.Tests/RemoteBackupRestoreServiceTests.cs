using System;
using System.IO;
using System.IO.Compression;
using System.Formats.Tar;
using System.Text;
using NodeBB.SSHManager.Services;
using Xunit;

namespace NodeBB.SSHManager.Tests;

public sealed class RemoteBackupRestoreServiceTests
{
    private const string Work = "/tmp/nodebb-file-restore-0123456789abcdef0123456789abcdef";

    [Fact]
    public void BuildRestoreScript_UsesStagingRollbackAndDatabaseIdentityGuard()
    {
        var script = RemoteBackupRestoreService.BuildRestoreScript("/srv/nodebb", Work, true);
        Assert.Contains("ROLLBACK=", script);
        Assert.Contains("rollback_changes()", script);
        Assert.Contains("identity(current) !== identity(incoming)", script);
        Assert.Contains("cp -p", script);
        Assert.Contains("mv \"$INSTALL/public/uploads\" \"$ROLLBACK/uploads\"", script);
        Assert.Contains("result=41", script);
        Assert.Contains("result=42", script);
        Assert.Contains("--no-same-owner --no-same-permissions", script);
        Assert.DoesNotContain("git reset --hard", script);
    }

    [Fact]
    public void BuildRestoreScript_CanRestoreConfigWithoutReplacingUploads()
    {
        var script = RemoteBackupRestoreService.BuildRestoreScript("/srv/nodebb", Work, false);
        Assert.Contains("RESTORE_UPLOADS=0", script);
    }

    [Fact]
    public void BuildRestoreScript_QuotesInstallationPath()
    {
        var script = RemoteBackupRestoreService.BuildRestoreScript("/srv/nodebb's forum", Work, true);
        Assert.Contains("'/srv/nodebb'\\''s forum'", script);
    }

    [Theory]
    [InlineData("/tmp/not-restore", false)]
    [InlineData("/tmp/nodebb-file-restore-ABCDEF0123456789ABCDEF0123456789", false)]
    [InlineData(Work, true)]
    public void BuildRestoreScript_ValidatesWorkPath(string path, bool valid)
    {
        if (valid) Assert.NotNull(RemoteBackupRestoreService.BuildRestoreScript("/srv/nodebb", path, false));
        else Assert.Throws<ArgumentException>(() => RemoteBackupRestoreService.BuildRestoreScript("/srv/nodebb", path, false));
    }

    [Theory]
    [InlineData("../config.json")]
    [InlineData("/etc/passwd")]
    [InlineData("public/../../etc/passwd")]
    [InlineData("public\\uploads\\file")]
    [InlineData("C:/Windows/win.ini")]
    [InlineData("public/uploads/bad\nname")]
    public void NormalizeArchivePath_RejectsTraversalAndPlatformSpecificPaths(string path) =>
        Assert.Throws<InvalidDataException>(() => RemoteBackupRestoreService.NormalizeArchivePath(path));

    [Theory]
    [InlineData("config.json", TarEntryType.RegularFile, true)]
    [InlineData("package.json", TarEntryType.RegularFile, true)]
    [InlineData("public", TarEntryType.Directory, true)]
    [InlineData("public/uploads", TarEntryType.Directory, true)]
    [InlineData("public/uploads/photo.jpg", TarEntryType.RegularFile, true)]
    [InlineData("public/uploads/link", TarEntryType.SymbolicLink, false)]
    [InlineData("node_modules/evil.js", TarEntryType.RegularFile, false)]
    public void IsAllowedEntry_OnlyAllowsExpectedBackupContent(string path, TarEntryType type, bool expected) =>
        Assert.Equal(expected, RemoteBackupRestoreService.IsAllowedEntry(path, type));

    [Fact]
    public async Task ValidateEssentialArchive_AcceptsExpectedContentsAndRejectsTraversal()
    {
        var directory = Path.Combine(Path.GetTempPath(), "NodeBBFileRestoreTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var good = Path.Combine(directory, "good.tar.gz");
        var bad = Path.Combine(directory, "bad.tar.gz");
        try
        {
            await CreateArchiveAsync(good, "config.json", "{\"database\":\"mongo\",\"mongo\":{\"host\":\"127.0.0.1\",\"port\":27017,\"database\":\"nodebb\"}}");
            var info = await RemoteBackupRestoreService.ValidateEssentialArchiveAsync(good);
            Assert.Equal("mongo", info.DatabaseType);
            Assert.False(info.ContainsUploads);

            await CreateArchiveAsync(bad, "../config.json", "{}");
            await Assert.ThrowsAsync<InvalidDataException>(() => RemoteBackupRestoreService.ValidateEssentialArchiveAsync(bad));
        }
        finally { Directory.Delete(directory, true); }
    }

    private static async Task CreateArchiveAsync(string path, string entryName, string contents)
    {
        await using var file = File.Create(path);
        await using var gzip = new GZipStream(file, CompressionLevel.Optimal);
        using var writer = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true);
        var entry = new PaxTarEntry(TarEntryType.RegularFile, entryName)
        {
            DataStream = new MemoryStream(Encoding.UTF8.GetBytes(contents))
        };
        writer.WriteEntry(entry);
    }
}
