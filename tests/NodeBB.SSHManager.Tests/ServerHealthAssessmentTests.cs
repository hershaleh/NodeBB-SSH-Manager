using NodeBB.SSHManager.Services;
using Xunit;

namespace NodeBB.SSHManager.Tests;

public sealed class ServerHealthAssessmentTests
{
    [Fact]
    public void DoesNotFlagHighLoadThatIsNormalForLargeServer()
    {
        var alerts = ServerHealthAssessment.Evaluate("4.2 3.8 3.1 2/500 1234", "/: 10G/100G (10%)", 35, 8);
        Assert.Empty(alerts);
    }

    [Fact]
    public void FlagsLoadRelativeToCpuCount()
    {
        var alerts = ServerHealthAssessment.Evaluate("3.1 2.8 2.0 4/100 1234", "", null, 2);
        Assert.Contains(alerts, alert => alert.Contains("עומס המערכת"));
    }

    [Theory]
    [InlineData(89, false)]
    [InlineData(90, true)]
    [InlineData(100, true)]
    [InlineData(-1, false)]
    [InlineData(101, false)]
    public void CpuUsageAlertUsesNinetyPercentThreshold(int? cpuPercent, bool expected)
    {
        var alerts = ServerHealthAssessment.Evaluate("0.1 0.1 0.1 1/100 1234", "", null, 4,
            cpuUsagePercent: cpuPercent);
        Assert.Equal(expected, alerts.Any(alert => alert.Contains("ניצול המעבד")));
    }

    [Fact]
    public void CpuAlertUsesStableLocalizableText()
    {
        var alerts = ServerHealthAssessment.Evaluate("", "", null, 1, cpuUsagePercent: 95);
        Assert.Contains("ניצול המעבד גבוה (90% ומעלה).", alerts);
    }

    [Fact]
    public void FlagsMemoryAtNinetyPercent()
    {
        var alerts = ServerHealthAssessment.Evaluate("0.1 0.1 0.1 1/100 1234", "", 90, 4);
        Assert.Contains(alerts, alert => alert.Contains("זיכרון"));
    }

    [Fact]
    public void FlagsAnyDiskAtNinetyPercent()
    {
        var alerts = ServerHealthAssessment.Evaluate("0.1 0.1 0.1 1/100 1234", "/: 90G/100G (90%); /data: 10G/100G (10%)", null, 4);
        Assert.Contains(alerts, alert => alert.Contains("מחיצות הדיסק"));
    }

    [Fact]
    public void FlagsHighSwapUsage()
    {
        var alerts = ServerHealthAssessment.Evaluate("0.1 0.1 0.1 1/100 1234", "", 20, 4, 90);
        Assert.Contains(alerts, alert => alert.Contains("Swap"));
    }

    [Fact]
    public void FlagsHighInodeUsageOnAnyFilesystem()
    {
        var alerts = ServerHealthAssessment.Evaluate("0.1 0.1 0.1 1/100 1234", "", 20, 4, null,
            "/: (10%); /data: (95%);");
        Assert.Contains(alerts, alert => alert.Contains("inode"));
    }

    [Theory]
    [InlineData("/: (89%);", false)]
    [InlineData("/: (90%);", true)]
    [InlineData("/: (100%);", true)]
    [InlineData("/: (101%);", false)]
    public void InodeAlertUsesNinetyPercentThreshold(string inodes, bool expected)
    {
        var alerts = ServerHealthAssessment.Evaluate("", "", null, 1, null, inodes);
        Assert.Equal(expected, alerts.Any(alert => alert.Contains("inode")));
    }

    [Fact]
    public void IgnoresUnavailableOrMalformedMetrics()
    {
        var alerts = ServerHealthAssessment.Evaluate("לא זמין", "לא זמין", null, 0);
        Assert.Empty(alerts);
    }

    [Fact]
    public void CanReportMultipleIndependentAlerts()
    {
        var alerts = ServerHealthAssessment.Evaluate("8.0 7.0 6.0 1/100 1234", "/: 95G/100G (95%)", 96, 4);
        Assert.Equal(3, alerts.Count);
    }
}
