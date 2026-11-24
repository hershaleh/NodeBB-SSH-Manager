using NodeBB.SSHManager.Services;
using Xunit;

namespace NodeBB.SSHManager.Tests;

public sealed class MonitoringAlertHistoryStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "NodeBB-monitor-alerts-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }

    [Fact]
    public void SavesAndLoadsBoundedEventHistoryPerProfile()
    {
        var store = new MonitoringAlertHistoryStore(_directory);
        var profile = Guid.NewGuid();
        var events = Enumerable.Range(0, 120)
            .Select(i => new MonitoringAlertEvent(DateTimeOffset.UnixEpoch.AddMinutes(i), "Alert " + i, i % 2 == 0));
        store.Save(profile, events);
        var loaded = store.Load(profile);
        Assert.Equal(100, loaded.Count);
        Assert.Equal("Alert 20", loaded[0].Message);
        Assert.Equal("Alert 119", loaded[^1].Message);
    }

    [Fact]
    public void RejectsEmptyProfileId()
    {
        var store = new MonitoringAlertHistoryStore(_directory);
        Assert.Throws<ArgumentException>(() => store.Load(Guid.Empty));
        Assert.Throws<ArgumentException>(() => store.Save(Guid.Empty, []));
    }
}
