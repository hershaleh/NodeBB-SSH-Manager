using NodeBB.SSHManager.Models;
using NodeBB.SSHManager.Services;
using Xunit;

namespace NodeBB.SSHManager.Tests;

public sealed class FleetReadinessServiceTests
{
    private static ServerProfile Profile() => new() { Name = "Forum", Host = "forum.example.org", Port = 22 };

    private static RemoteServerInventory Inventory(string node = "v22.1.0", string npm = "10.8.0",
        string git = "2.46.0", bool root = false, string packageManager = "apt-get") =>
        new("forum", "Ubuntu 24.04", "6.8.0", "x86_64", "systemd", packageManager, "nodebb", root,
            node, npm, git, "db version v7.0", "Redis server v7.2", "Docker 26", "לא מותקן",
            "5.0.0", true, DateTimeOffset.Parse("2026-11-24T12:00:00Z"));

    [Fact]
    public void FromInventoryMarksBasicToolsAvailableButRequiresSeparateCompatibilityCheck()
    {
        var row = FleetReadinessService.FromInventory(Profile(), Inventory());
        Assert.True(row.IsReachable);
        Assert.False(row.NeedsReview);
        Assert.Equal("כלים בסיסיים זמינים", row.State);
        Assert.Contains("יש לאמת בנפרד תאימות", row.Findings);
    }

    [Theory]
    [InlineData("לא מותקן", "10.0", "2.0")]
    [InlineData("v22", "לא מותקן", "2.0")]
    [InlineData("v22", "10.0", "לא מותקן")]
    public void FromInventoryFlagsMissingCoreMaintenanceTools(string node, string npm, string git)
    {
        var row = FleetReadinessService.FromInventory(Profile(), Inventory(node, npm, git));
        Assert.True(row.NeedsReview);
        Assert.Contains("לא זוהה", row.Findings);
    }

    [Fact]
    public void FromInventoryWarnsWhenConnectedAsRoot()
    {
        var row = FleetReadinessService.FromInventory(Profile(), Inventory(root: true));
        Assert.True(row.NeedsReview);
        Assert.Contains("root", row.Findings);
    }

    [Fact]
    public void CsvQuotesFieldsAndNeutralizesFormulaInjection()
    {
        var row = FleetReadinessService.FromInventory(Profile(), Inventory()) with
        {
            ServerName = "=HYPERLINK(\"https://bad.example\")",
            Findings = "warning, \"quoted\""
        };
        var csv = FleetReadinessService.ExportCsv([row]);
        Assert.Contains("\"'=HYPERLINK(\"\"https://bad.example\"\")\"", csv);
        Assert.Contains("\"warning, \"\"quoted\"\"\"", csv);
    }

    [Fact]
    public void CsvContainsAllInventoryColumnsAndSupportsEmptyRows()
    {
        var csv = FleetReadinessService.ExportCsv([]);
        Assert.Contains("OperatingSystem,Node,Npm,Git,Mongo,Redis", csv);
        Assert.Single(csv.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
    }
}