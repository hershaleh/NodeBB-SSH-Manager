using NodeBB.SSHManager.Services;
using Xunit;

namespace NodeBB.SSHManager.Tests;

public sealed class ServerMonitoringHistoryTests
{
    [Fact]
    public void RetainsOnlyNewestSamplesWithinConfiguredCapacity()
    {
        var history = new ServerMonitoringHistory(3);
        for (var i = 1; i <= 5; i++)
            history.Add(DateTimeOffset.UnixEpoch.AddMinutes(i), i * 10, i * 5);

        Assert.Equal(3, history.Samples.Count);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddMinutes(3), history.Samples[0].CollectedAt);
        Assert.Equal(50, history.Samples[^1].CpuPercent);
    }

    [Fact]
    public void InvalidMetricValuesAreStoredAsUnavailable()
    {
        var history = new ServerMonitoringHistory();
        history.Add(DateTimeOffset.UtcNow, -1, 101);
        Assert.Null(history.Samples[0].CpuPercent);
        Assert.Null(history.Samples[0].MemoryPercent);
    }

    [Fact]
    public void AcceptsValidBoundaryPercentages()
    {
        var history = new ServerMonitoringHistory();
        history.Add(DateTimeOffset.UtcNow, 0, 100);
        Assert.Equal(0, history.Samples[0].CpuPercent);
        Assert.Equal(100, history.Samples[0].MemoryPercent);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(1441)]
    public void RejectsUnsafeHistoryCapacities(int capacity)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ServerMonitoringHistory(capacity));
    }
}
