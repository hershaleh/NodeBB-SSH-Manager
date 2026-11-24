using System;
using System.IO;
using System.Linq;
using NodeBB.SSHManager.Services;
using Xunit;

namespace NodeBB.SSHManager.Tests;

public sealed class ServerMonitoringHistoryStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "NodeBB-Monitor-History-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void SavesAndLoadsSamplesSeparatelyForEachProfile()
    {
        var store = new ServerMonitoringHistoryStore(_directory);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var sample = new ServerMonitoringSample(DateTimeOffset.Parse("2026-11-24T10:00:00Z"), 72, 48, 0.75);
        store.Save(first, [sample]);
        store.Save(second, [sample with { CpuPercent = 12 }]);

        Assert.Equal(72, Assert.Single(store.Load(first)).CpuPercent);
        Assert.Equal(12, Assert.Single(store.Load(second)).CpuPercent);
    }

    [Fact]
    public void RetainsOnlyNewest1440Samples()
    {
        var store = new ServerMonitoringHistoryStore(_directory);
        var id = Guid.NewGuid();
        var samples = Enumerable.Range(0, 1500)
            .Select(i => new ServerMonitoringSample(DateTimeOffset.UnixEpoch.AddMinutes(i), i % 101, i % 101, i / 10d));
        store.Save(id, samples);

        var loaded = store.Load(id);
        Assert.Equal(1440, loaded.Count);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddMinutes(60), loaded[0].CollectedAt);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddMinutes(1499), loaded[^1].CollectedAt);
    }

    [Fact]
    public void InvalidMetricsAreSanitizedBeforePersistence()
    {
        var store = new ServerMonitoringHistoryStore(_directory);
        var id = Guid.NewGuid();
        store.Save(id, [new ServerMonitoringSample(DateTimeOffset.UtcNow, 101, -1, double.NaN)]);
        var loaded = Assert.Single(store.Load(id));
        Assert.Null(loaded.CpuPercent);
        Assert.Null(loaded.MemoryPercent);
        Assert.Null(loaded.LoadPerCpu);
    }

    [Fact]
    public void CorruptHistoryIsPreservedAndIgnored()
    {
        Directory.CreateDirectory(_directory);
        var id = Guid.NewGuid();
        File.WriteAllText(Path.Combine(_directory, id.ToString("N") + ".history.json"), "{broken");
        var store = new ServerMonitoringHistoryStore(_directory);

        Assert.Empty(store.Load(id));
        Assert.Single(Directory.GetFiles(_directory, "*.bak"));
    }

    [Fact]
    public void RejectsEmptyProfileId()
    {
        var store = new ServerMonitoringHistoryStore(_directory);
        Assert.Throws<ArgumentException>(() => store.Load(Guid.Empty));
        Assert.Throws<ArgumentException>(() => store.Save(Guid.Empty, []));
    }

    [Fact]
    public void HistoryFiltersSamplesByTimestamp()
    {
        var start = DateTimeOffset.UnixEpoch;
        var history = new ServerMonitoringHistory(10);
        history.Add(start.AddMinutes(1), 10, 20, 0.5);
        history.Add(start.AddMinutes(2), 30, 40, 1.5);
        history.Add(start.AddMinutes(3), 50, 60, 2.5);

        var filtered = history.GetSince(start.AddMinutes(2));
        Assert.Equal(2, filtered.Count);
        Assert.Equal(30, filtered[0].CpuPercent);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
