using NodeBB.SSHManager.Services;
using Xunit;

namespace NodeBB.SSHManager.Tests;

public sealed class MonitoringThresholdAssessmentTests
{
    [Fact]
    public void AppliesCustomCpuThreshold()
    {
        var thresholds = new MonitoringAlertThresholds { CpuPercent = 75 };
        var below = ServerHealthAssessment.Evaluate("", "", null, 4, cpuUsagePercent: 74, thresholds: thresholds);
        var at = ServerHealthAssessment.Evaluate("", "", null, 4, cpuUsagePercent: 75, thresholds: thresholds);
        Assert.Empty(below);
        Assert.Contains(at, x => x.Contains("ניצול המעבד"));
    }

    [Fact]
    public void AppliesCustomMemoryAndSwapThresholds()
    {
        var thresholds = new MonitoringAlertThresholds { MemoryPercent = 80, SwapPercent = 70 };
        var alerts = ServerHealthAssessment.Evaluate("", "", 80, 4, 70, thresholds: thresholds);
        Assert.Contains(alerts, x => x.Contains("זיכרון"));
        Assert.Contains(alerts, x => x.Contains("Swap"));
    }

    [Fact]
    public void AppliesCustomDiskAndInodeThresholds()
    {
        var thresholds = new MonitoringAlertThresholds { DiskPercent = 85, InodePercent = 75 };
        var alerts = ServerHealthAssessment.Evaluate("", "/: 85G/100G (85%)", null, 4,
            diskInodes: "/: (75%);", thresholds: thresholds);
        Assert.Contains(alerts, x => x.Contains("מחיצות הדיסק"));
        Assert.Contains(alerts, x => x.Contains("inode"));
    }

    [Fact]
    public void AppliesCustomLoadPerCpuThreshold()
    {
        var thresholds = new MonitoringAlertThresholds { LoadPerCpu = 2.0 };
        var below = ServerHealthAssessment.Evaluate("3.9 0 0", "", null, 2, thresholds: thresholds);
        var at = ServerHealthAssessment.Evaluate("4.0 0 0", "", null, 2, thresholds: thresholds);
        Assert.Empty(below);
        Assert.Contains(at, x => x.Contains("עומס המערכת"));
    }

    [Fact]
    public void RejectsInvalidThresholdsBeforeEvaluating()
    {
        var thresholds = new MonitoringAlertThresholds { CpuPercent = 20 };
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ServerHealthAssessment.Evaluate("", "", null, 1, thresholds: thresholds));
    }
}
