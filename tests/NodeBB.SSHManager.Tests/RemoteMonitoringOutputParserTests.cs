using NodeBB.SSHManager.Services;
using Xunit;

namespace NodeBB.SSHManager.Tests;

public sealed class RemoteMonitoringOutputParserTests
{
    [Fact]
    public void ParsesAllMetricsAndCpuCount()
    {
        var collected = DateTimeOffset.Parse("2026-11-19T12:00:00Z");
        var metrics = RemoteMonitoringOutputParser.Parse(
            "UPTIME|up 4 days\nLOAD|1.20 0.80 0.60 1/200 123\nCPU_COUNT|8\nMEMORY|2G / 8G בשימוש\nMEMORY_PERCENT|25\nDISK|/: 10G/100G (10%)\nPROCESSES|123 node", collected);

        Assert.Equal("up 4 days", metrics.Uptime);
        Assert.Equal(8, metrics.CpuCount);
        Assert.Equal(25, metrics.MemoryUsagePercent);
        Assert.Equal("1.20 0.80 0.60 1/200 123", metrics.LoadAverage);
        Assert.Equal(collected, metrics.CollectedAt);
        Assert.Equal("לא זמין", metrics.Swap);
        Assert.Null(metrics.SwapUsagePercent);
        Assert.Equal("לא זמין", metrics.DiskInodes);
        Assert.Equal("לא זמין", metrics.TopMemoryProcesses);
    }

    [Theory]
    [InlineData("0", 0)]
    [InlineData("-1", null)]
    [InlineData("101", null)]
    [InlineData("not-a-number", null)]
    public void ParsesOnlyValidMemoryPercent(string value, int? expected)
    {
        var metrics = RemoteMonitoringOutputParser.Parse($"MEMORY_PERCENT|{value}");
        Assert.Equal(expected, metrics.MemoryUsagePercent);
    }

    [Theory]
    [InlineData("0", 1)]
    [InlineData("-4", 1)]
    [InlineData("4097", 1)]
    [InlineData("not-a-number", 1)]
    [InlineData("16", 16)]
    public void UsesSafeCpuCountFallback(string value, int expected)
    {
        var metrics = RemoteMonitoringOutputParser.Parse($"CPU_COUNT|{value}");
        Assert.Equal(expected, metrics.CpuCount);
    }

    [Fact]
    public void MissingFieldsBecomeUnavailable()
    {
        var metrics = RemoteMonitoringOutputParser.Parse(string.Empty);
        Assert.Equal("לא זמין", metrics.Uptime);
        Assert.Equal("לא זמין", metrics.LoadAverage);
        Assert.Equal("לא זמין", metrics.Memory);
        Assert.Null(metrics.MemoryUsagePercent);
        Assert.Equal(1, metrics.CpuCount);
    }

    [Theory]
    [InlineData("0", 0)]
    [InlineData("90", 90)]
    [InlineData("100", 100)]
    [InlineData("-1", null)]
    [InlineData("101", null)]
    [InlineData("bad", null)]
    public void ParsesOnlyValidSwapPercent(string value, int? expected)
    {
        var metrics = RemoteMonitoringOutputParser.Parse($"SWAP|1G / 2G בשימוש\nSWAP_PERCENT|{value}");
        Assert.Equal(expected, metrics.SwapUsagePercent);
        Assert.Equal("1G / 2G בשימוש", metrics.Swap);
    }

    [Fact]
    public void ParsesDiskInodeUsageWithoutChangingDiskUsage()
    {
        var metrics = RemoteMonitoringOutputParser.Parse("DISK|/: 90G/100G (90%)\nDISK_INODES|/: (12%); /data: (95%);");
        Assert.Equal("/: 90G/100G (90%)", metrics.Disk);
        Assert.Equal("/: (12%); /data: (95%);", metrics.DiskInodes);
    }

    [Fact]
    public void ParsesTopMemoryProcessesIndependentlyFromCpuProcesses()
    {
        var metrics = RemoteMonitoringOutputParser.Parse("PROCESSES|101 node 80 12\nMEMORY_PROCESSES|202 mongo 2 45");
        Assert.Equal("101 node 80 12", metrics.TopProcesses);
        Assert.Equal("202 mongo 2 45", metrics.TopMemoryProcesses);
    }

    [Fact]
    public void UsesLastValueWhenOutputContainsRepeatedKeys()
    {
        var metrics = RemoteMonitoringOutputParser.Parse("CPU_COUNT|2\nCPU_COUNT|12");
        Assert.Equal(12, metrics.CpuCount);
    }

    [Fact]
    public void IgnoresUnknownOutputLinesWithoutBreakingParsing()
    {
        var metrics = RemoteMonitoringOutputParser.Parse("noise without delimiter\nCPU_COUNT|4\nUNKNOWN|ignored");
        Assert.Equal(4, metrics.CpuCount);
    }
}
