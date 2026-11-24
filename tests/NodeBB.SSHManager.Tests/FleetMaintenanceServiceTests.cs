using NodeBB.SSHManager.Models;
using NodeBB.SSHManager.Services;
using Xunit;

namespace NodeBB.SSHManager.Tests;

public sealed class FleetMaintenanceServiceTests
{
    private static ServerProfile Profile() => new()
    {
        Name = "Forum", Host = "forum.example.org", Port = 22
    };

    private static NodeBbUpgradePreflight Preflight(bool git = true, bool clean = true,
        bool origin = true, bool node = true, bool npm = true, string db = "mongo") =>
        new(git, clean, origin, node, npm, "4.19.1", "main", "abc123",
            "v22.1.0", "10.8.0", db, "");

    [Fact]
    public void FromPreflightMarksSupportedCleanInstallationReadyOnlyAfterBackup()
    {
        var row = FleetMaintenanceService.FromPreflight(Profile(), "/srv/nodebb", Preflight());
        Assert.True(row.IsReachable);
        Assert.True(row.CanUpgrade);
        Assert.Equal("4.19.1", row.Version);
        Assert.Contains("גיבוי מוצפן", row.Findings);
    }

    [Theory]
    [InlineData(false, true, true, true, true, "mongo", "מאגר Git")]
    [InlineData(true, false, true, true, true, "mongo", "שינויים")]
    [InlineData(true, true, false, true, true, "mongo", "origin")]
    [InlineData(true, true, true, false, true, "mongo", "Node.js")]
    [InlineData(true, true, true, true, false, "mongo", "npm")]
    [InlineData(true, true, true, true, true, "unknown", "מסד הנתונים")]
    public void FromPreflightBlocksUnsafeUpgradePrerequisites(bool git, bool clean, bool origin,
        bool node, bool npm, string db, string expected)
    {
        var row = FleetMaintenanceService.FromPreflight(Profile(), "/srv/nodebb",
            Preflight(git, clean, origin, node, npm, db));
        Assert.False(row.CanUpgrade);
        Assert.Contains(expected, row.Findings);
    }

    [Fact]
    public void CsvQuotesFieldsAndNeutralizesSpreadsheetFormulas()
    {
        var row = FleetMaintenanceService.FromPreflight(Profile(), "/srv/nodebb", Preflight()) with
        {
            ServerName = "=HYPERLINK(\"https://bad.example\")",
            Findings = "warning, \"quoted\""
        };
        var csv = FleetMaintenanceService.ExportCsv([row]);
        Assert.Contains("\"'=HYPERLINK(\"\"https://bad.example\"\")\"", csv);
        Assert.Contains("\"warning, \"\"quoted\"\"\"", csv);
    }

    [Fact]
    public void CsvHasStableHeaderForEmptyResults()
    {
        var csv = FleetMaintenanceService.ExportCsv([]);
        Assert.Contains("InstallationPath,Version,Branch,Database", csv);
        Assert.Single(csv.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
    }
}
