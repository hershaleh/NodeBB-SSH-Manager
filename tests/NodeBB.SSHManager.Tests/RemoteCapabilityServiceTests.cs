using NodeBB.SSHManager.Services;
using Xunit;

namespace NodeBB.SSHManager.Tests;

public sealed class RemoteCapabilityServiceTests
{
    [Fact]
    public void DetectionScriptReadsOsReleaseAsDataWithoutSourcingIt()
    {
        var script = RemoteCapabilityService.BuildDetectionScript();

        Assert.DoesNotContain(". /etc/os-release", script, StringComparison.Ordinal);
        Assert.Contains("sed -n 's/^PRETTY_NAME=//p' /etc/os-release", script, StringComparison.Ordinal);
        Assert.DoesNotContain("eval ", script, StringComparison.Ordinal);
    }

    [Fact]
    public void DetectionScriptUsesExplicitArgumentsForVersionCommands()
    {
        var script = RemoteCapabilityService.BuildDetectionScript();

        Assert.Contains("shift", script, StringComparison.Ordinal);
        Assert.Contains("\"$@\" 2>/dev/null", script, StringComparison.Ordinal);
        Assert.Contains("version_of node node --version", script, StringComparison.Ordinal);
        Assert.Contains("version_of npm npm --version", script, StringComparison.Ordinal);
    }

    [Fact]
    public void DetectionScriptKeepsInventoryOutputKeysStable()
    {
        var script = RemoteCapabilityService.BuildDetectionScript();

        foreach (var key in new[]
        {
            "hostname", "os", "kernel", "architecture", "init", "package_manager",
            "user", "is_root", "node", "npm", "git", "mongo", "redis",
            "docker", "podman", "pm2", "nodebb_cli"
        })
            Assert.Contains($"emit {key} ", script, StringComparison.Ordinal);
    }
}
