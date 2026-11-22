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


    [Theory]
    [InlineData(true, false, false, false, false, false, true)]
    [InlineData(true, false, false, true, true, false, true)]
    [InlineData(true, false, false, true, false, false, false)]
    [InlineData(true, false, false, true, true, true, false)]
    [InlineData(true, true, false, true, true, false, false)]
    [InlineData(true, false, true, true, true, false, false)]
    [InlineData(false, false, false, false, false, false, false)]
    public void ShouldRestartOriginalAfterRestoreFailure_OnlyWhenRemoteStateIsKnownSafe(
        bool stopped, bool switched, bool restartAttempted, bool scriptStarted,
        bool restoreCommandReturned, bool recoveryRequired, bool expected)
    {
        Assert.Equal(expected, RemoteBackupRestoreService.ShouldRestartOriginalAfterRestoreFailure(
            stopped, switched, restartAttempted, scriptStarted, restoreCommandReturned, recoveryRequired));
    }

    [Fact]
    public void BuildFullInstallationRecoveryInspectionScript_IsReadOnlyAndFindsEveryRecoveryArtifactType()
    {
        var script = RemoteBackupRestoreService.BuildFullInstallationRecoveryInspectionScript("/srv/nodebb");
        Assert.Contains(".nodebb-stage-nodebb-full-restore-*", script);
        Assert.Contains(".nodebb-rollback-nodebb-full-restore-*", script);
        Assert.Contains(".nodebb-failed-nodebb-full-restore-*", script);
        Assert.Contains("/tmp/nodebb-full-restore-*", script);
        Assert.Contains("RECOVERY_ARTIFACT", script);
        Assert.Contains("INSTALLATION|missing", script);
        Assert.DoesNotContain("rm -rf", script);
        Assert.DoesNotContain("mv --", script);
        Assert.DoesNotContain("mkdir", script);
        Assert.DoesNotContain("touch", script);
    }

    [Theory]
    [InlineData("relative/nodebb")]
    [InlineData("/")]
    [InlineData("/srv/nodebb/../other")]
    public void BuildFullInstallationRecoveryInspectionScript_RejectsUnsafePaths(string path) =>
        Assert.Throws<ArgumentException>(() =>
            RemoteBackupRestoreService.BuildFullInstallationRecoveryInspectionScript(path));

    [Fact]
    public void BuildFullInstallationPreflightScriptChecksDiskPermissionsArchiveAndExistingRecoveryPaths()
    {
        var script = RemoteBackupRestoreService.BuildFullInstallationPreflightScript(
            "/srv/nodebb", FullWork, ValidHash, 1024 * 1024);
        Assert.Contains("FULL_RESTORE_PREFLIGHT_OK", script);
        Assert.Contains("sha256sum", script);
        Assert.Contains("tar -tzf", script);
        Assert.Contains("df -Pk", script);
        Assert.Contains("FOREIGN=", script);
        Assert.Contains("du -skx", script);
        Assert.Contains("CURRENT_KB", script);
        Assert.Contains("REQUIRED_BYTES", script);
        Assert.Contains(".nodebb-stage-", script);
        Assert.Contains(".nodebb-rollback-", script);
        Assert.Contains("NodeBB", script);
    }


    [Fact]
    public void BuildFullInstallationRollbackScript_NeverDeletesEmergencyOrFailedInstallationTrees()
    {
        var script = RemoteBackupRestoreService.BuildFullInstallationRollbackScript("/srv/nodebb", FullWork);
        Assert.DoesNotContain("rm -rf", script);
        Assert.Contains("mv -- \"$INSTALL\" \"$FAILED\"", script);
        Assert.Contains("mv -- \"$ROLLBACK\" \"$INSTALL\"", script);
    }

    [Fact]
    public void BuildFullInstallationRestoreScript_OnlyRemovesStagingAfterSafeRollbackAndKeepsEmergencyCopyOnUncertainFailure()
    {
        var script = RemoteBackupRestoreService.BuildFullInstallationRestoreScript(
            "/srv/nodebb", FullWork, ValidHash, 1024 * 1024);
        Assert.Contains("if [ \"$SWAP_STARTED\" = \"1\" ] && [ \"$KEEP_ROLLBACK\" = \"0\" ]", script);
        Assert.Contains("if [ \"$KEEP_ROLLBACK\" = \"0\" ]; then rm -rf -- \"$STAGE\"; fi", script);
        Assert.Contains("rm -rf -- \"$WORK\"", script);
        Assert.Contains("KEEP_ROLLBACK=1", script);
        Assert.Contains("result=42", script);
        Assert.Contains("exit \"$result\"", script);
        Assert.DoesNotContain("rm -rf -- \"$FAILED\"", script);
    }

    [Theory]
    [InlineData("relative/nodebb", FullWork, ValidHash, 1024)]
    [InlineData("/srv/nodebb", "/tmp/wrong", ValidHash, 1024)]
    [InlineData("/srv/nodebb", FullWork, "bad-hash", 1024)]
    [InlineData("/srv/nodebb", FullWork, ValidHash, 0)]
    [InlineData("/srv/nodebb", FullWork, ValidHash, 100L * 1024 * 1024 * 1024 + 1)]
    public void BuildFullInstallationPreflightScriptRejectsUnsafeInputs(
        string installationPath, string workDirectory, string hash, long expandedBytes) =>
        Assert.ThrowsAny<ArgumentException>(() =>
            RemoteBackupRestoreService.BuildFullInstallationPreflightScript(
                installationPath, workDirectory, hash, expandedBytes));

    [Fact]
    public void BuildFullInstallationPreflightScript_FailureMatrixNeverMutatesInstallation()
    {
        var script = RemoteBackupRestoreService.BuildFullInstallationPreflightScript(
            "/srv/nodebb", FullWork, ValidHash, 1024 * 1024);

        Assert.Contains("[ -d \"$WORK\" ] && [ ! -L \"$WORK\" ]", script);
        Assert.Contains("[ -s \"$WORK/incoming.tar.gz\" ] && [ ! -L \"$WORK/incoming.tar.gz\" ]", script);
        Assert.Contains("[ \"$ACTUAL_SHA256\" = \"$EXPECTED_SHA256\" ]", script);
        Assert.Contains("[ ! -e \"$STAGE\" ] && [ ! -e \"$ROLLBACK\" ] && [ ! -e \"$FAILED\" ]", script);
        Assert.Contains("df -Pk", script);
        Assert.Contains("du -skx", script);
        Assert.Contains("tar -tzf", script);
        Assert.Contains("exit 50", script);
        Assert.Contains("FULL_RESTORE_PREFLIGHT_OK", script);
        Assert.DoesNotContain("mkdir", script);
        Assert.DoesNotContain("mv --", script);
        Assert.DoesNotContain("rm -rf", script);
    }

    [Fact]
    public void BuildFullInstallationRestoreScript_ValidatesEverythingBeforeBeginningSwap()
    {
        var script = RemoteBackupRestoreService.BuildFullInstallationRestoreScript(
            "/srv/nodebb", FullWork, ValidHash, 1024 * 1024);

        var validationMarkers = new[]
        {
            "[ -d \"$WORK\" ]",
            "[ -s \"$WORK/incoming.tar.gz\" ]",
            "ACTUAL_SHA256=",
            "tar -tzf",
            "mkdir -m 700 \"$STAGE\"",
            "node - \"$INSTALL/package.json\"",
            "SPECIAL="
        };
        var swap = script.IndexOf("SWAP_STARTED=1", StringComparison.Ordinal);
        Assert.True(swap > 0);
        foreach (var marker in validationMarkers)
        {
            var position = script.IndexOf(marker, StringComparison.Ordinal);
            Assert.True(position >= 0 && position < swap, $"Expected '{marker}' before the swap begins.");
        }

        Assert.Contains("exit 40", script);
        Assert.Contains("exit 41", script);
        Assert.Contains("mv -- \"$INSTALL\" \"$ROLLBACK\"", script);
        Assert.Contains("mv -- \"$STAGE\" \"$INSTALL\"", script);
        Assert.Contains("result=42", script);
        Assert.Contains("FULL_RESTORE_SWITCHED", script);
    }

    [Fact]
    public void BuildFullInstallationRollbackScript_FailureMatrixPreservesBothTreesWhenMovesFail()
    {
        var script = RemoteBackupRestoreService.BuildFullInstallationRollbackScript("/srv/nodebb", FullWork);

        Assert.Contains("[ -d \"$ROLLBACK\" ] && [ ! -L \"$ROLLBACK\" ]", script);
        Assert.Contains("[ -d \"$INSTALL\" ] && [ ! -L \"$INSTALL\" ]", script);
        Assert.Contains("[ ! -e \"$FAILED\" ]", script);
        Assert.Contains("mv -- \"$INSTALL\" \"$FAILED\" || exit 44", script);
        Assert.Contains("mv -- \"$ROLLBACK\" \"$INSTALL\" || result=45", script);
        Assert.Contains("mv -- \"$FAILED\" \"$INSTALL\" ||", script);
        Assert.Contains("trap recover_interrupted_rollback EXIT", script);
        Assert.Contains("exit 45", script);
        Assert.DoesNotContain("rm -rf", script);
    }

    [Fact]
    public void BuildFullInstallationFinalizeScript_ValidatesPreservationTargetBeforeRemovingWorkspace()
    {
        var success = RemoteBackupRestoreService.BuildFullInstallationFinalizeScript("/srv/nodebb", FullWork, false);
        var rollback = RemoteBackupRestoreService.BuildFullInstallationFinalizeScript("/srv/nodebb", FullWork, true);

        Assert.True(success.IndexOf("[ -d \"$ROLLBACK\" ]", StringComparison.Ordinal) <
                    success.IndexOf("rm -rf -- \"$WORK\"", StringComparison.Ordinal));
        Assert.True(rollback.IndexOf("[ -d \"$FAILED\" ]", StringComparison.Ordinal) <
                    rollback.IndexOf("rm -rf -- \"$WORK\"", StringComparison.Ordinal));
        Assert.Contains("exit 46", success);
        Assert.Contains("exit 46", rollback);
        Assert.DoesNotContain("rm -rf -- \"$ROLLBACK\"", success);
        Assert.DoesNotContain("rm -rf -- \"$FAILED\"", rollback);
    }

    [Fact]
    public void RestoreFullInstallationAsync_OrdersPreflightStopSwapHealthRollbackAndFinalizeSafely()
    {
        var source = File.ReadAllText(FindServiceSource());
        var start = source.IndexOf("public async Task RestoreFullInstallationAsync(", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var end = source.IndexOf("public async Task<string> InspectFullInstallationRecoveryAsync(", start, StringComparison.Ordinal);
        Assert.True(end > start);
        var method = source[start..end];

        AssertOrdered(method,
            "ValidateFullInstallationArchiveAsync",
            "mkdir -m 700",
            "UploadFile",
            "sha256sum --",
            "BuildFullInstallationPreflightScript",
            "ExecuteLifecycleAsync(installation, runtime, \"stop\"",
            "VerifyHttpStoppedAsync",
            "BuildFullInstallationRestoreScript",
            "ExecuteLifecycleAsync(installation, runtime, \"start\"",
            "BuildFullInstallationRollbackScript",
            "BuildFullInstallationFinalizeScript");
        Assert.Contains("recoveryRequired = true", method);
        Assert.Contains("CancellationToken.None", method);
        Assert.Contains("if (switched)", method);
        Assert.Contains("if (workCreated && !scriptStarted)", method);
    }

    private static void AssertOrdered(string source, params string[] markers)
    {
        var previous = -1;
        foreach (var marker in markers)
        {
            var current = source.IndexOf(marker, previous + 1, StringComparison.Ordinal);
            Assert.True(current > previous, $"Expected '{marker}' after the preceding restore phase.");
            previous = current;
        }
    }

    private static string FindServiceSource()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "src", "NodeBB.SSHManager.App", "Services", "RemoteBackupRestoreService.cs");
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }
        throw new FileNotFoundException("Could not locate RemoteBackupRestoreService.cs for restore orchestration assertions.");
    }

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
        Assert.Contains("trap cleanup EXIT", script);
        Assert.Contains("trap 'exit 129' HUP", script);
        Assert.Contains("trap 'exit 130' INT", script);
        Assert.Contains("trap 'exit 143' TERM", script);
        Assert.Contains("identity(currentConfig) !== identity(incomingConfig)", script);
        Assert.Contains("find \"$STAGE\" -mindepth 1 ! -type f ! -type d", script);
        Assert.Contains("df -Pk", script);
        Assert.Contains("du -skx", script);
        Assert.Contains("CURRENT_KB", script);
        Assert.Contains("REQUIRED_BYTES", script);
        Assert.Contains("[ -x \"$STAGE/nodebb\" ]", script);
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
        Assert.Contains("recover_interrupted_rollback()", script);
        Assert.Contains("trap recover_interrupted_rollback EXIT", script);
        Assert.Contains("trap 'exit 129' HUP", script);
        Assert.Contains("trap 'exit 130' INT", script);
        Assert.Contains("trap 'exit 143' TERM", script);
        Assert.Contains("FULL_RESTORE_ROLLED_BACK", script);
    }

    [Fact]
    public void BuildFullInstallationFinalizeScript_OnlyDeletesTheExpectedEmergencyDirectory()
    {
        var success = RemoteBackupRestoreService.BuildFullInstallationFinalizeScript("/srv/nodebb", FullWork, false);
        var rollback = RemoteBackupRestoreService.BuildFullInstallationFinalizeScript("/srv/nodebb", FullWork, true);
        Assert.Contains("if [ \"0\" = \"1\" ]; then", success);
        Assert.Contains("FULL_RESTORE_ROLLBACK_PRESERVED|$ROLLBACK", success);
        Assert.Contains("if [ \"1\" = \"1\" ]; then", rollback);
        Assert.Contains("FULL_RESTORE_FAILED_TREE_PRESERVED|$FAILED", rollback);
        Assert.DoesNotContain("rm -rf -- \"$ROLLBACK\"", success);
        Assert.DoesNotContain("rm -rf -- \"$FAILED\"", rollback);
        Assert.Contains("rm -rf -- \"$WORK\"", success);
        Assert.Contains("rm -rf -- \"$WORK\"", rollback);
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
                ("./nodebb", TarEntryType.RegularFile, "#!/usr/bin/env node"),
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
    public async Task ValidateFullInstallationArchive_RejectsMissingNodeBbCliAndFileDirectoryCollisions()
    {
        var directory = CreateTempDirectory();
        try
        {
            var missingCli = Path.Combine(directory, "missing-cli.tar.gz");
            await CreateArchiveAsync(missingCli,
                ("config.json", TarEntryType.RegularFile, ValidConfig),
                ("package.json", TarEntryType.RegularFile, "{\"name\":\"nodebb\"}"));
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                RemoteBackupRestoreService.ValidateFullInstallationArchiveAsync(missingCli));

            var collision = Path.Combine(directory, "collision.tar.gz");
            await CreateArchiveAsync(collision,
                ("config.json", TarEntryType.RegularFile, ValidConfig),
                ("package.json", TarEntryType.RegularFile, "{\"name\":\"nodebb\"}"),
                ("nodebb", TarEntryType.RegularFile, "#!/usr/bin/env node"),
                ("assets", TarEntryType.RegularFile, "file"),
                ("assets/logo.png", TarEntryType.RegularFile, "image"));
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                RemoteBackupRestoreService.ValidateFullInstallationArchiveAsync(collision));
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
        var cli = new PaxTarEntry(TarEntryType.RegularFile, "nodebb")
        {
            DataStream = new MemoryStream(Encoding.UTF8.GetBytes("#!/usr/bin/env node"))
        };
        writer.WriteEntry(cli);
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
