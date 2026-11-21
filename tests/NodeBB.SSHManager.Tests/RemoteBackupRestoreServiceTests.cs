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
    private const string ValidConfig = "{\"database\":\"mongo\",\"mongo\":{\"host\":\"127.0.0.1\",\"port\":27017,\"database\":\"nodebb\"}}";


    private const string FullWork = "/tmp/nodebb-full-restore-0123456789abcdef0123456789abcdef";
    private const string ValidHash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public void BuildFullInstallationRestoreScript_StagesValidatesAndKeepsEmergencyCopy()
    {
        var script = RemoteBackupRestoreService.BuildFullInstallationRestoreScript(
            "/srv/nodebb", FullWork, ValidHash, 1024 * 1024);
        Assert.Contains("sha256sum", script);
        Assert.Contains("EXPECTED_SHA256", script);
        Assert.Contains("FULL_RESTORE_SWITCHED", script);
        Assert.Contains("ROLLBACK=", script);
        Assert.Contains("SWAP_STARTED=1", script);
        Assert.Contains("KEEP_ROLLBACK=1", script);
        Assert.Contains("identity(currentConfig) !== identity(incomingConfig)", script);
        Assert.Contains("find \"$STAGE\" -mindepth 1 ! -type f ! -type d", script);
        Assert.Contains("df -Pk", script);
        Assert.Contains("nodebb", script);
        Assert.DoesNotContain("mongorestore", script);
        Assert.DoesNotContain("redis-cli FLUSH", script);
    }

    [Fact]
    public void BuildFullInstallationRollbackScript_RestoresOriginalDirectoryAndPreservesFailedTreeUntilFinalized()
    {
        var script = RemoteBackupRestoreService.BuildFullInstallationRollbackScript("/srv/nodebb", FullWork);
        Assert.Contains("mv -- \"$INSTALL\" \"$FAILED\"", script);
        Assert.Contains("mv -- \"$ROLLBACK\" \"$INSTALL\"", script);
        Assert.Contains("FULL_RESTORE_ROLLED_BACK", script);
    }

    [Fact]
    public void BuildFullInstallationFinalizeScript_OnlyDeletesTheExpectedEmergencyDirectory()
    {
        var success = RemoteBackupRestoreService.BuildFullInstallationFinalizeScript("/srv/nodebb", FullWork, false);
        var rollback = RemoteBackupRestoreService.BuildFullInstallationFinalizeScript("/srv/nodebb", FullWork, true);
        Assert.Contains("rm -rf -- \"$ROLLBACK\"", success);
        Assert.DoesNotContain("rm -rf -- \"$FAILED\"", success);
        Assert.Contains("rm -rf -- \"$FAILED\"", rollback);
        Assert.DoesNotContain("rm -rf -- \"$ROLLBACK\"", rollback);
    }

    [Theory]
    [InlineData("relative/nodebb", FullWork, ValidHash, 1024)]
    [InlineData("/", FullWork, ValidHash, 1024)]
    [InlineData("/srv/nodebb", "/tmp/wrong", ValidHash, 1024)]
    [InlineData("/srv/nodebb", FullWork, "bad-hash", 1024)]
    [InlineData("/srv/nodebb", FullWork, ValidHash, 0)]
    [InlineData("/srv/nodebb", FullWork, ValidHash, 100L * 1024 * 1024 * 1024 + 1)]
    public void BuildFullInstallationRestoreScript_RejectsUnsafeInputs(
        string installationPath, string workDirectory, string hash, long expandedBytes) =>
        Assert.ThrowsAny<ArgumentException>(() =>
            RemoteBackupRestoreService.BuildFullInstallationRestoreScript(
                installationPath, workDirectory, hash, expandedBytes));

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

    [Theory]
    [InlineData("relative/nodebb")]
    [InlineData("/")]
    [InlineData("/srv/nodebb/../other")]
    [InlineData("/srv/nodebb/./forum")]
    [InlineData("/srv/nodebb\nforum")]
    public void BuildRestoreScript_RejectsUnsafeInstallationPaths(string path) =>
        Assert.Throws<ArgumentException>(() => RemoteBackupRestoreService.BuildRestoreScript(path, Work, true));

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
    [InlineData("public/uploads/bad\\nname")]
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
        var directory = CreateTempDirectory();
        var good = Path.Combine(directory, "good.tar.gz");
        var bad = Path.Combine(directory, "bad.tar.gz");
        try
        {
            await CreateArchiveAsync(good, ("config.json", TarEntryType.RegularFile, ValidConfig));
            var info = await RemoteBackupRestoreService.ValidateEssentialArchiveAsync(good);
            Assert.Equal("mongo", info.DatabaseType);
            Assert.False(info.ContainsUploads);

            await CreateArchiveAsync(bad, ("../config.json", TarEntryType.RegularFile, "{}"));
            await Assert.ThrowsAsync<InvalidDataException>(() => RemoteBackupRestoreService.ValidateEssentialArchiveAsync(bad));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task ValidateEssentialArchive_RejectsTruncatedArchive()
    {
        var directory = CreateTempDirectory();
        var path = Path.Combine(directory, "truncated.tar.gz");
        try
        {
            await CreateArchiveAsync(path,
                ("config.json", TarEntryType.RegularFile, ValidConfig),
                ("public/uploads/large.bin", TarEntryType.RegularFile, new string('x', 8192)));
            var bytes = await File.ReadAllBytesAsync(path);
            await File.WriteAllBytesAsync(path, bytes[..Math.Max(1, bytes.Length / 2)]);
            await Assert.ThrowsAnyAsync<Exception>(() => RemoteBackupRestoreService.ValidateEssentialArchiveAsync(path));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task ValidateEssentialArchive_RejectsMissingOrUnsupportedDatabaseConfiguration()
    {
        var directory = CreateTempDirectory();
        try
        {
            var missing = Path.Combine(directory, "missing.tar.gz");
            await CreateArchiveAsync(missing, ("public/uploads/photo.jpg", TarEntryType.RegularFile, "image"));
            await Assert.ThrowsAsync<InvalidDataException>(() => RemoteBackupRestoreService.ValidateEssentialArchiveAsync(missing));

            var unsupported = Path.Combine(directory, "unsupported.tar.gz");
            await CreateArchiveAsync(unsupported, ("config.json", TarEntryType.RegularFile, "{\"database\":\"sqlite\"}"));
            await Assert.ThrowsAsync<InvalidDataException>(() => RemoteBackupRestoreService.ValidateEssentialArchiveAsync(unsupported));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task ValidateEssentialArchive_RejectsDuplicatePathsAndUnexpectedFiles()
    {
        var directory = CreateTempDirectory();
        try
        {
            var duplicate = Path.Combine(directory, "duplicate.tar.gz");
            await CreateArchiveAsync(duplicate,
                ("config.json", TarEntryType.RegularFile, ValidConfig),
                ("config.json", TarEntryType.RegularFile, ValidConfig));
            await Assert.ThrowsAsync<InvalidDataException>(() => RemoteBackupRestoreService.ValidateEssentialArchiveAsync(duplicate));

            var unexpected = Path.Combine(directory, "unexpected.tar.gz");
            await CreateArchiveAsync(unexpected,
                ("config.json", TarEntryType.RegularFile, ValidConfig),
                ("node_modules/unexpected.js", TarEntryType.RegularFile, "not allowed"));
            await Assert.ThrowsAsync<InvalidDataException>(() => RemoteBackupRestoreService.ValidateEssentialArchiveAsync(unexpected));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task ValidateFullInstallationArchive_AcceptsNodeBbRootAndReportsContents()
    {
        var directory = CreateTempDirectory();
        var path = Path.Combine(directory, "full.tar.gz");
        try
        {
            await CreateArchiveAsync(path,
                (".", TarEntryType.Directory, ""),
                ("./config.json", TarEntryType.RegularFile, ValidConfig),
                ("./package.json", TarEntryType.RegularFile, "{\"name\":\"nodebb\"}"),
                ("./public", TarEntryType.Directory, ""),
                ("./public/uploads", TarEntryType.Directory, ""),
                ("./public/uploads/photo.jpg", TarEntryType.RegularFile, "image"));
            var info = await RemoteBackupRestoreService.ValidateFullInstallationArchiveAsync(path);
            Assert.True(info.EntryCount >= 6);
            Assert.True(info.ExpandedBytes > 0);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("../escape.txt", TarEntryType.RegularFile)]
    [InlineData("/etc/passwd", TarEntryType.RegularFile)]
    [InlineData("node_modules\\evil.js", TarEntryType.RegularFile)]
    [InlineData("config.json", TarEntryType.SymbolicLink)]
    [InlineData("config.json", TarEntryType.HardLink)]
    public async Task ValidateFullInstallationArchive_RejectsTraversalAndLinks(string name, TarEntryType type)
    {
        var directory = CreateTempDirectory();
        var path = Path.Combine(directory, "unsafe.tar.gz");
        try
        {
            await CreateFullArchiveWithSpecialEntryAsync(path, name, type);
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                RemoteBackupRestoreService.ValidateFullInstallationArchiveAsync(path));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task ValidateFullInstallationArchive_RequiresNodeBbMetadataAndRejectsWrongPackage()
    {
        var directory = CreateTempDirectory();
        try
        {
            var missing = Path.Combine(directory, "missing.tar.gz");
            await CreateArchiveAsync(missing,
                ("config.json", TarEntryType.RegularFile, ValidConfig));
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                RemoteBackupRestoreService.ValidateFullInstallationArchiveAsync(missing));

            var wrongPackage = Path.Combine(directory, "wrong-package.tar.gz");
            await CreateArchiveAsync(wrongPackage,
                ("config.json", TarEntryType.RegularFile, ValidConfig),
                ("package.json", TarEntryType.RegularFile, "{\"name\":\"other-app\"}"));
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                RemoteBackupRestoreService.ValidateFullInstallationArchiveAsync(wrongPackage));
        }
        finally { Directory.Delete(directory, true); }
    }

    private static async Task CreateFullArchiveWithSpecialEntryAsync(string path, string name, TarEntryType type)
    {
        await using var file = File.Create(path);
        await using var gzip = new GZipStream(file, CompressionLevel.Optimal);
        using var writer = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true);
        var config = new PaxTarEntry(TarEntryType.RegularFile, "config.json")
        {
            DataStream = new MemoryStream(Encoding.UTF8.GetBytes(ValidConfig))
        };
        var package = new PaxTarEntry(TarEntryType.RegularFile, "package.json")
        {
            DataStream = new MemoryStream(Encoding.UTF8.GetBytes("{\"name\":\"nodebb\"}"))
        };
        writer.WriteEntry(config);
        writer.WriteEntry(package);
        var special = new PaxTarEntry(type, name);
        if (type is TarEntryType.SymbolicLink or TarEntryType.HardLink)
            special.LinkName = "../outside";
        writer.WriteEntry(special);
    }

    private static string CreateTempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "NodeBBFileRestoreTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static async Task CreateArchiveAsync(string path, params (string Name, TarEntryType Type, string Contents)[] entries)
    {
        await using var file = File.Create(path);
        await using var gzip = new GZipStream(file, CompressionLevel.Optimal);
        using var writer = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true);
        foreach (var item in entries)
        {
            var entry = new PaxTarEntry(item.Type, item.Name);
            if (item.Type is TarEntryType.RegularFile or TarEntryType.V7RegularFile)
                entry.DataStream = new MemoryStream(Encoding.UTF8.GetBytes(item.Contents));
            writer.WriteEntry(entry);
        }
    }
}
