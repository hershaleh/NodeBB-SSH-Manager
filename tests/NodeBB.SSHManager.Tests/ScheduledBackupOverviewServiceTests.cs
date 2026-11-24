using NodeBB.SSHManager.Models;
using NodeBB.SSHManager.Services;
using Xunit;

namespace NodeBB.SSHManager.Tests;

public sealed class ScheduledBackupOverviewServiceTests
{
    private static ServerProfile Profile(Guid id, string name = "Forum") =>
        new() { Id = id, Name = name, Host = "forum.example.org", Port = 22 };

    private static ScheduledBackupDefinition Schedule(Guid profileId) => new()
    {
        Id = Guid.NewGuid(), ProfileId = profileId, ProfileName = "Forum",
        InstallationPath = "/srv/nodebb", DestinationDirectory = Path.GetTempPath(),
        BackupKind = RemoteBackupKind.FullInstallation, RetentionCount = 5,
        Frequency = ScheduledBackupFrequency.Daily, LocalTime = "03:00"
    };

    [Fact]
    public void BuildRowsMapsProfileAndCalculatesNextRun()
    {
        var id = Guid.NewGuid();
        var now = new DateTime(2026, 11, 24, 1, 0, 0, DateTimeKind.Local);
        var row = Assert.Single(ScheduledBackupOverviewService.BuildRows([Profile(id)], [Schedule(id)], now));
        Assert.Equal("Forum", row.ServerName);
        Assert.Equal("forum.example.org:22", row.Endpoint);
        Assert.Equal("2026-11-24 03:00", row.NextRunLocal);
        Assert.False(row.IsAttentionRequired);
    }

    [Fact]
    public void BuildRowsFlagsMissingProfileAndKeepsScheduleVisible()
    {
        var schedule = Schedule(Guid.NewGuid());
        var row = Assert.Single(ScheduledBackupOverviewService.BuildRows([], [schedule], DateTime.Now));
        Assert.Contains("פרופיל חסר", row.ServerName);
        Assert.True(row.IsAttentionRequired);
    }

    [Fact]
    public void BuildRowsFlagsFailedAndAbandonedRuns()
    {
        var id = Guid.NewGuid();
        var failed = Schedule(id);
        failed.LastRunStatus = ScheduledBackupRunStatus.Failed;
        var abandoned = Schedule(id);
        abandoned.LastRunStatus = ScheduledBackupRunStatus.Running;
        abandoned.LastRunUtc = DateTime.UtcNow.AddHours(-25);
        var rows = ScheduledBackupOverviewService.BuildRows([Profile(id)], [failed, abandoned], DateTime.Now);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.True(row.IsAttentionRequired));
        Assert.Contains(rows, row => row.LastResult == "נכשל");
        Assert.Contains(rows, row => row.LastResult.Contains("נקטעה"));
    }

    [Fact]
    public void BuildRowsIncludesDatabaseAndWeeklyCadence()
    {
        var id = Guid.NewGuid();
        var schedule = Schedule(id);
        schedule.IncludeDatabase = true;
        schedule.Frequency = ScheduledBackupFrequency.Weekly;
        schedule.DayOfWeek = DayOfWeek.Friday;
        var row = Assert.Single(ScheduledBackupOverviewService.BuildRows([Profile(id)], [schedule], DateTime.Now));
        Assert.True(row.IncludesDatabase);
        Assert.Contains("מסד נתונים", row.BackupType);
        Assert.Contains("Friday", row.Cadence);
    }

    [Fact]
    public void CsvEscapesQuotesAndNeutralizesSpreadsheetFormulas()
    {
        var id = Guid.NewGuid();
        var schedule = Schedule(id);
        schedule.ProfileName = "=HYPERLINK(\"https://bad.example\")";
        schedule.LastRunSummary = "quoted, \"message\"";
        var row = Assert.Single(ScheduledBackupOverviewService.BuildRows(
            [Profile(id, schedule.ProfileName)], [schedule], DateTime.Now));
        var csv = ScheduledBackupOverviewService.ExportCsv([row]);
        Assert.Contains("\"'=HYPERLINK(\"\"https://bad.example\"\")\"", csv);
        Assert.Contains("\"quoted, \"\"message\"\"\"", csv);
    }

    [Fact]
    public void CsvExportsStableHeaderForEmptyScheduleList()
    {
        var csv = ScheduledBackupOverviewService.ExportCsv([]);
        Assert.Contains("Server,Endpoint,InstallationPath,BackupType", csv);
        Assert.Single(csv.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
    }
}