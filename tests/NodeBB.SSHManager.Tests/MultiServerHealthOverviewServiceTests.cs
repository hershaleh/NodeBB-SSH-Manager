using NodeBB.SSHManager.Services;
using Xunit;

namespace NodeBB.SSHManager.Tests;

public sealed class MultiServerHealthOverviewServiceTests
{
    [Fact]
    public void ExportCsvIncludesAllServerHealthFields()
    {
        var row = new ServerHealthOverviewRow("Forum", "forum.example.org:22", "נגיש", "25%", "40%",
            "0%", "/: 20G/100G (20%)", "RX 1 MiB/s; TX 2 MiB/s", "תקין", "2026-11-24 12:00:00", true);

        var csv = MultiServerHealthOverviewService.ExportCsv([row]);

        Assert.Contains("Server,Endpoint,ConnectionState,CPU,Memory,Swap,Disk,Network,Alerts,CollectedAt", csv);
        Assert.Contains("\"Forum\",\"forum.example.org:22\",\"נגיש\",\"25%\",\"40%\",\"0%\",", csv);
        Assert.Contains("\"RX 1 MiB/s; TX 2 MiB/s\"", csv);
    }

    [Fact]
    public void ExportCsvEscapesQuotesAndCommas()
    {
        var row = new ServerHealthOverviewRow("Forum, \"A\"", "host:22", "נגיש", "—", "—", "—",
            "—", "—", "Alert, \"high\"", "—", true);

        var csv = MultiServerHealthOverviewService.ExportCsv([row]);

        Assert.Contains("\"Forum, \"\"A\"\"\"", csv);
        Assert.Contains("\"Alert, \"\"high\"\"\"", csv);
    }

    [Theory]
    [InlineData("=HYPERLINK(\"https://evil.example\")")]
    [InlineData("+SUM(1,1)")]
    [InlineData("-1+2")]
    [InlineData("@malicious")]
    [InlineData("\tformula")]
    public void ExportCsvNeutralizesSpreadsheetFormulaPrefixes(string value)
    {
        var row = new ServerHealthOverviewRow(value, "host:22", "נגיש", "—", "—", "—",
            "—", "—", "—", "—", true);

        var csv = MultiServerHealthOverviewService.ExportCsv([row]);

        Assert.Contains("\"'" + value.Replace("\"", "\"\"") + "\"", csv);
    }

    [Fact]
    public void ExportCsvHandlesEmptyRows()
    {
        var csv = MultiServerHealthOverviewService.ExportCsv([]);
        Assert.Single(csv.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
    }
}
