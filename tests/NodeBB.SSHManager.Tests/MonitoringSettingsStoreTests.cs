using System.IO;
using NodeBB.SSHManager.Services;
using Xunit;

namespace NodeBB.SSHManager.Tests;

public sealed class MonitoringSettingsStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "NodeBB-monitor-settings-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }

    [Fact]
    public void PersistsSettingsSeparatelyForEachProfile()
    {
        var store = new MonitoringSettingsStore(_directory);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        store.Save(first, new MonitoringAlertThresholds { CpuPercent = 75 });
        store.Save(second, new MonitoringAlertThresholds { CpuPercent = 95 });
        Assert.Equal(75, store.Load(first).CpuPercent);
        Assert.Equal(95, store.Load(second).CpuPercent);
    }

    [Fact]
    public void MissingSettingsUseSafeDefaults()
    {
        Assert.Equal(90, new MonitoringSettingsStore(_directory).Load(Guid.NewGuid()).CpuPercent);
    }

    [Fact]
    public void InvalidSettingsArePreservedAndDefaultsReturned()
    {
        Directory.CreateDirectory(_directory);
        var id = Guid.NewGuid();
        File.WriteAllText(Path.Combine(_directory, id.ToString("N") + ".json"), "{\"CpuPercent\":5}");
        var loaded = new MonitoringSettingsStore(_directory).Load(id);
        Assert.Equal(90, loaded.CpuPercent);
        Assert.Single(Directory.GetFiles(_directory, "*.bak"));
    }

    [Fact]
    public void RejectsEmptyProfileId()
    {
        var store = new MonitoringSettingsStore(_directory);
        Assert.Throws<ArgumentException>(() => store.Load(Guid.Empty));
        Assert.Throws<ArgumentException>(() => store.Save(Guid.Empty, new MonitoringAlertThresholds()));
    }
}
