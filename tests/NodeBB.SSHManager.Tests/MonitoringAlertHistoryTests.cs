using NodeBB.SSHManager.Services;
using Xunit;

namespace NodeBB.SSHManager.Tests;

public sealed class MonitoringAlertHistoryTests
{
    [Fact]
    public void RecordsOnlyAlertTransitionsAndResolution()
    {
        var history = new MonitoringAlertHistory();
        var time = DateTimeOffset.UtcNow;
        Assert.Single(history.Update(time, ["CPU high"]));
        Assert.Empty(history.Update(time.AddMinutes(1), ["CPU high"]));
        var changes = history.Update(time.AddMinutes(2), []);
        Assert.Single(changes);
        Assert.False(changes[0].IsActive);
        Assert.Equal(2, history.Events.Count);
    }

    [Fact]
    public void TracksIndependentAlerts()
    {
        var history = new MonitoringAlertHistory();
        history.Update(DateTimeOffset.UtcNow, ["CPU", "Memory"]);
        var changes = history.Update(DateTimeOffset.UtcNow.AddMinutes(1), ["Memory", "Disk"]);
        Assert.Equal(2, changes.Count);
        Assert.Contains(changes, x => x.Message == "Disk" && x.IsActive);
        Assert.Contains(changes, x => x.Message == "CPU" && !x.IsActive);
    }

    [Fact]
    public void RestoresActiveStateFromPersistedEvents()
    {
        var at = DateTimeOffset.UtcNow;
        var history = new MonitoringAlertHistory([new MonitoringAlertEvent(at, "CPU", true)]);
        Assert.Empty(history.Update(at.AddMinutes(1), ["CPU"]));
        Assert.Single(history.Update(at.AddMinutes(2), []));
    }

    [Fact]
    public void EnforcesHistoryCapacity()
    {
        var history = new MonitoringAlertHistory(capacity: 2);
        history.Update(DateTimeOffset.UtcNow, ["A", "B"]);
        history.Update(DateTimeOffset.UtcNow.AddMinutes(1), []);
        Assert.Equal(2, history.Events.Count);
    }

    [Fact]
    public void RejectsUnsafeCapacity()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MonitoringAlertHistory(capacity: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MonitoringAlertHistory(capacity: 1001));
    }
}
