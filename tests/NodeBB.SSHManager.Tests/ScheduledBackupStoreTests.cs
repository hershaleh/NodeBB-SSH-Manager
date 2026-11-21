using System.IO;
using NodeBB.SSHManager.Models;
using NodeBB.SSHManager.Services;
using Xunit;

namespace NodeBB.SSHManager.Tests;

public sealed class ScheduledBackupStoreTests
{
    [Fact]
    public void StoreRoundTripsProtectedScheduleAndDoesNotExposeMetadata()
    {
        var directory = Path.Combine(Path.GetTempPath(), "NodeBBScheduledBackupTests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ScheduledBackupStore(directory);
            var schedule = CreateValid();
            schedule.ProfileName = "Private Production Server";
            schedule.InstallationPath = "/srv/private-nodebb";
            schedule.DestinationDirectory = Path.Combine(Path.GetTempPath(), "NodeBB Backups");
            schedule.Frequency = ScheduledBackupFrequency.Weekly;
            schedule.DayOfWeek = DayOfWeek.Friday;
            schedule.LocalTime = "02:45";
            schedule.IncludeDatabase = true;
            schedule.LastRunUtc = DateTime.UtcNow;
            schedule.LastRunStatus = ScheduledBackupRunStatus.Succeeded;
            schedule.LastRunSummary = "הגיבוי הושלם בהצלחה.";
            store.Save(schedule);

            var file = Directory.GetFiles(directory, "*.schedule").Single();
            var protectedContent = File.ReadAllText(file);
            Assert.DoesNotContain(schedule.ProfileName, protectedContent);
            Assert.DoesNotContain(schedule.InstallationPath, protectedContent);

            var loaded = store.Load(schedule.Id);
            Assert.Equal(schedule.Id, loaded.Id);
            Assert.Equal(schedule.ProfileId, loaded.ProfileId);
            Assert.Equal(schedule.InstallationPath, loaded.InstallationPath);
            Assert.Equal(schedule.DestinationDirectory, loaded.DestinationDirectory);
            Assert.Equal(schedule.BackupKind, loaded.BackupKind);
            Assert.Equal(schedule.Frequency, loaded.Frequency);
            Assert.Equal(schedule.DayOfWeek, loaded.DayOfWeek);
            Assert.Equal(schedule.LocalTime, loaded.LocalTime);
            Assert.Equal(schedule.IncludeDatabase, loaded.IncludeDatabase);
            Assert.Equal(schedule.LastRunStatus, loaded.LastRunStatus);
            Assert.Equal(schedule.LastRunUtc, loaded.LastRunUtc);
            Assert.Equal(schedule.LastRunSummary, loaded.LastRunSummary);
            Assert.Contains("כולל מסד נתונים", loaded.DisplayLabel);
            Assert.Contains("הצליח", loaded.DisplayLabel);
            Assert.Single(store.LoadAll());

            store.Delete(schedule.Id);
            Assert.Empty(store.LoadAll());
        }
        finally { try { if (Directory.Exists(directory)) Directory.Delete(directory, true); } catch { } }
    }

    [Fact]
    public void LoadAll_IgnoresScheduleCopiedUnderAnotherFileId()
    {
        var directory = Path.Combine(Path.GetTempPath(), "NodeBBScheduledBackupTests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ScheduledBackupStore(directory);
            var schedule = CreateValid();
            store.Save(schedule);
            var original = Directory.GetFiles(directory, "*.schedule").Single();
            File.Copy(original, Path.Combine(directory, Guid.NewGuid().ToString("N") + ".schedule"));

            Assert.Single(store.LoadAll());
        }
        finally { try { if (Directory.Exists(directory)) Directory.Delete(directory, true); } catch { } }
    }

    [Fact]
    public void LoadRejectsOversizedScheduleFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), "NodeBBScheduledBackupTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var id = Guid.NewGuid();
        try
        {
            File.WriteAllBytes(Path.Combine(directory, id.ToString("N") + ".schedule"), new byte[64 * 1024 + 1]);
            Assert.Throws<InvalidDataException>(() => new ScheduledBackupStore(directory).Load(id));
            Assert.Empty(new ScheduledBackupStore(directory).LoadAll());
        }
        finally { try { if (Directory.Exists(directory)) Directory.Delete(directory, true); } catch { } }
    }

    [Theory]
    [InlineData("25:00")]
    [InlineData("3:00")]
    [InlineData("not-a-time")]
    public void ValidateRejectsInvalidLocalTime(string time)
    {
        var schedule = CreateValid();
        schedule.LocalTime = time;
        Assert.Throws<InvalidDataException>(schedule.Validate);
    }

    [Fact]
    public void DailyScheduleCalculatesNextRunInLocalTime()
    {
        var schedule = CreateValid();
        schedule.Frequency = ScheduledBackupFrequency.Daily;
        schedule.LocalTime = "03:00";
        var now = new DateTime(2026, 11, 20, 1, 30, 0, DateTimeKind.Local);
        Assert.Equal(new DateTime(2026, 11, 20, 3, 0, 0, DateTimeKind.Local), schedule.GetNextRunLocal(now));
    }

    [Fact]
    public void WeeklyScheduleCalculatesNextSelectedWeekday()
    {
        var schedule = CreateValid();
        schedule.Frequency = ScheduledBackupFrequency.Weekly;
        schedule.DayOfWeek = DayOfWeek.Friday;
        schedule.LocalTime = "03:00";
        var now = new DateTime(2026, 11, 16, 4, 0, 0, DateTimeKind.Local);
        Assert.Equal(new DateTime(2026, 11, 20, 3, 0, 0, DateTimeKind.Local), schedule.GetNextRunLocal(now));
    }

    [Fact]
    public void WeeklyScheduleMovesToNextWeekWhenTimeHasPassed()
    {
        var schedule = CreateValid();
        schedule.Frequency = ScheduledBackupFrequency.Weekly;
        schedule.DayOfWeek = DayOfWeek.Friday;
        schedule.LocalTime = "03:00";
        var now = new DateTime(2026, 11, 20, 4, 0, 0, DateTimeKind.Local);
        Assert.Equal(new DateTime(2026, 11, 27, 3, 0, 0, DateTimeKind.Local), schedule.GetNextRunLocal(now));
    }

    [Fact]
    public void DisplayLabelMarksAbandonedRunningScheduleAsInterrupted()
    {
        var schedule = CreateValid();
        schedule.LastRunStatus = ScheduledBackupRunStatus.Running;
        schedule.LastRunUtc = DateTime.UtcNow.AddDays(-2);
        Assert.Contains("נקטע", schedule.DisplayLabel);
    }

    [Fact]
    public void ValidateRejectsNullLastRunSummary()
    {
        var schedule = CreateValid();
        schedule.LastRunSummary = null!;
        Assert.Throws<InvalidDataException>(schedule.Validate);
    }

    [Fact]
    public void ValidateRejectsUnsupportedRetentionCount()
    {
        var schedule = CreateValid();
        schedule.RetentionCount = 3;
        Assert.Throws<InvalidDataException>(schedule.Validate);
    }

    [Fact]
    public void ValidateRejectsRelativeDestinationDirectory()
    {
        var schedule = CreateValid();
        schedule.DestinationDirectory = "relative-backups";
        Assert.Throws<InvalidDataException>(schedule.Validate);
    }

    private static ScheduledBackupDefinition CreateValid() => new()
    {
        ProfileId = Guid.NewGuid(),
        ProfileName = "Production",
        InstallationPath = "/srv/nodebb",
        DestinationDirectory = Path.GetTempPath(),
        BackupKind = RemoteBackupKind.ConfigurationAndUploads,
        RetentionCount = 5,
        Frequency = ScheduledBackupFrequency.Daily,
        LocalTime = "03:00"
    };
}