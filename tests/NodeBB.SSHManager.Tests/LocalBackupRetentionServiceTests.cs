using System.IO;
using NodeBB.SSHManager.Services;
using Xunit;

namespace NodeBB.SSHManager.Tests;

public sealed class LocalBackupRetentionServiceTests
{
    [Fact]
    public void ApplyRetention_KeepsNewestAndDoesNotTouchOtherServerOrBackupKind()
    {
        var dir = Path.Combine(Path.GetTempPath(), "NodeBBRetentionTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var old = Create(dir, "NodeBB-Production-full-1.nbbbackup", -3);
            var middle = Create(dir, "NodeBB-Production-full-2.tar.gz", -2);
            var newest = Create(dir, "NodeBB-Production-full-3.nbbbackup", -1);
            var otherServer = Create(dir, "NodeBB-Staging-full-1.nbbbackup", -4);
            var otherKind = Create(dir, "NodeBB-Production-config-uploads-1.nbbbackup", -5);
            var unrelated = Create(dir, "notes.txt", -6);

            Assert.Equal(1, LocalBackupRetentionService.ApplyRetention(dir, "NodeBB-Production-full", 2));
            Assert.False(File.Exists(old));
            Assert.True(File.Exists(middle));
            Assert.True(File.Exists(newest));
            Assert.True(File.Exists(otherServer));
            Assert.True(File.Exists(otherKind));
            Assert.True(File.Exists(unrelated));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void BuildFilePrefixSeparatesProfilesWithIdenticalNamesAndBackupKinds()
    {
        var first = LocalBackupRetentionService.BuildFilePrefix("Production Server", Guid.Parse("11111111-1111-1111-1111-111111111111"), RemoteBackupKind.FullInstallation);
        var second = LocalBackupRetentionService.BuildFilePrefix("Production Server", Guid.Parse("22222222-2222-2222-2222-222222222222"), RemoteBackupKind.FullInstallation);
        var essential = LocalBackupRetentionService.BuildFilePrefix("Production Server", Guid.Parse("11111111-1111-1111-1111-111111111111"), RemoteBackupKind.ConfigurationAndUploads);

        Assert.NotEqual(first, second);
        Assert.NotEqual(first, essential);
        Assert.StartsWith("NodeBB-Production-Server-", first);
        Assert.EndsWith("-full", first);
        Assert.EndsWith("-config-uploads", essential);
    }

    [Fact]
    public void RetentionForOneProfileDoesNotDeleteAnotherProfileWithSameName()
    {
        var dir = Path.Combine(Path.GetTempPath(), "NodeBBRetentionTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var firstPrefix = LocalBackupRetentionService.BuildFilePrefix("Production", Guid.Parse("11111111-1111-1111-1111-111111111111"), RemoteBackupKind.FullInstallation);
            var secondPrefix = LocalBackupRetentionService.BuildFilePrefix("Production", Guid.Parse("22222222-2222-2222-2222-222222222222"), RemoteBackupKind.FullInstallation);
            var oldFirst = Create(dir, firstPrefix + "-old.nbbbackup", -3);
            var newestFirst = Create(dir, firstPrefix + "-new.nbbbackup", -1);
            var otherProfile = Create(dir, secondPrefix + "-only.nbbbackup", -2);

            Assert.Equal(1, LocalBackupRetentionService.ApplyRetention(dir, firstPrefix, 1));
            Assert.False(File.Exists(oldFirst));
            Assert.True(File.Exists(newestFirst));
            Assert.True(File.Exists(otherProfile));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void DatabasePrefixIsIsolatedByProfileAndDatabaseType()
    {
        var profileId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var mongo = LocalBackupRetentionService.BuildDatabaseFilePrefix("Production Server", profileId, "mongo");
        var postgres = LocalBackupRetentionService.BuildDatabaseFilePrefix("Production Server", profileId, "postgres");
        var otherProfile = LocalBackupRetentionService.BuildDatabaseFilePrefix("Production Server",
            Guid.Parse("22222222-2222-2222-2222-222222222222"), "mongo");
        Assert.NotEqual(mongo, postgres);
        Assert.NotEqual(mongo, otherProfile);
        Assert.EndsWith("-database-mongo", mongo);
        Assert.EndsWith("-database-postgres", postgres);
    }

    [Fact]
    public void RejectsUnsupportedDatabasePrefixType()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            LocalBackupRetentionService.BuildDatabaseFilePrefix("Production", Guid.NewGuid(), "sqlite"));
    }

    [Fact]
    public void RejectsDirectorySeparatorsInPrefix()
    {
        Assert.Throws<ArgumentException>(() => LocalBackupRetentionService.ApplyRetention(".", "folder\\backup", 1));
    }

    [Fact]
    public void ZeroRetentionKeepsAllFiles()
    {
        var dir = Path.Combine(Path.GetTempPath(), "NodeBBRetentionTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var file = Create(dir, "NodeBB-Production-full-1.nbbbackup", -1);
            Assert.Equal(0, LocalBackupRetentionService.ApplyRetention(dir, "NodeBB-Production-full", 0));
            Assert.True(File.Exists(file));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void RejectsUnsafePrefixAndNegativeCount()
    {
        Assert.Throws<ArgumentException>(() => LocalBackupRetentionService.ApplyRetention(".", "../*", 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => LocalBackupRetentionService.ApplyRetention(".", "NodeBB", -1));
    }

    private static string Create(string dir, string name, int daysAgo)
    {
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, "backup");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(daysAgo));
        return path;
    }
}