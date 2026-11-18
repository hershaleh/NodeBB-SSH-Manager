using System;
using NodeBB.SSHManager.Services;
using Xunit;

namespace NodeBB.SSHManager.Tests;

public sealed class NodeBbUpgradeServiceTests
{
    [Theory]
    [InlineData("v4.19.1", true)]
    [InlineData("v4.20.0-rc.1", true)]
    [InlineData("4.19.1", false)]
    [InlineData("v4.x", false)]
    [InlineData("v4.19.1; rm -rf /", false)]
    [InlineData("v4..1", false)]
    [InlineData("../v4.19.1", false)]
    [InlineData("", false)]
    public void IsValidTargetTag_OnlyAcceptsVersionTags(string tag, bool expected) =>
        Assert.Equal(expected, NodeBbUpgradeService.IsValidTargetTag(tag));

    [Fact]
    public void PreflightScript_InspectsGitCleanlinessAndRuntimeWithoutMutatingRepository()
    {
        var script = NodeBbUpgradeService.BuildPreflightScript("/srv/nodebb");
        Assert.Contains("git status --porcelain --untracked-files=all", script);
        Assert.Contains("git remote get-url origin", script);
        Assert.Contains("node --version", script);
        Assert.Contains("npm --version", script);
        Assert.DoesNotContain("git reset --hard", script);
        Assert.DoesNotContain("git clean", script);
    }

    [Fact]
    public void UpgradeScript_FetchesAndChecksOutOnlyValidatedTag()
    {
        var script = NodeBbUpgradeService.BuildUpgradeScript("/srv/nodebb", "v4.19.1",
            "0123456789abcdef0123456789abcdef01234567");
        Assert.DoesNotContain("git fetch --tags origin", script);
        Assert.Contains("refs/tags/$TARGET^{commit}", script);
        Assert.Contains("git checkout --detach", script);
        Assert.Contains("./nodebb upgrade", script);
        Assert.Contains("git status --porcelain --untracked-files=all", script);
    }

    [Fact]
    public void UpgradeScript_RejectsUntrustedTargetAndCommit()
    {
        Assert.Throws<ArgumentException>(() => NodeBbUpgradeService.BuildUpgradeScript("/srv/nodebb", "v4.19.1;id", "0123456789abcdef0123456789abcdef01234567"));
        Assert.Throws<ArgumentException>(() => NodeBbUpgradeService.BuildUpgradeScript("/srv/nodebb", "v4.19.1", "not-a-commit"));
    }

    [Fact]
    public void UpgradeScript_QuotesInstallationPath()
    {
        var script = NodeBbUpgradeService.BuildUpgradeScript("/srv/my nodebb", "v4.19.1",
            "0123456789abcdef0123456789abcdef01234567");
        Assert.Contains("'/srv/my nodebb'", script);
    }
    [Fact]
    public void VerifyTargetTagScript_FetchesAndValidatesBeforeMaintenance()
    {
        var script = NodeBbUpgradeService.BuildVerifyTargetTagScript("/srv/nodebb", "v4.19.1");
        Assert.Contains("git fetch --tags origin", script);
        Assert.Contains("refs/tags/v4.19.1^{commit}", script);
    }
    [Fact]
    public void HealthCheckScript_UsesNodeAndConfiguredPortWithBoundedHttpRetries()
    {
        var script = NodeBbUpgradeService.BuildHealthCheckScript("/srv/nodebb");
        Assert.Contains("config.port || 4567", script);
        Assert.Contains("config.bind_address", script);
        Assert.Contains("http.get", script);
        Assert.Contains("status >= 200 && status < 400", script);
        Assert.Contains("status === 401 || status === 403", script);
        Assert.Contains("timeoutAt", script);
        Assert.Contains("NODEBB_HTTP_HEALTH|ok|", script);
        Assert.DoesNotContain("curl ", script);
        Assert.DoesNotContain("wget ", script);
    }

    [Fact]
    public void HealthCheckScript_QuotesPathAndRejectsUnsafeTimeout()
    {
        var script = NodeBbUpgradeService.BuildHealthCheckScript("/srv/my nodebb", 60);
        Assert.Contains("cd '/srv/my nodebb'", script);
        Assert.Contains("timeout_seconds=60", script);
        Assert.Throws<ArgumentOutOfRangeException>(() => NodeBbUpgradeService.BuildHealthCheckScript("/srv/nodebb", 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => NodeBbUpgradeService.BuildHealthCheckScript("/srv/nodebb", 121));
    }

    [Fact]
    public void HealthCheckScript_RejectsInvalidInstallationPath()
    {
        Assert.Throws<ArgumentException>(() => NodeBbUpgradeService.BuildHealthCheckScript(" "));
        Assert.Throws<ArgumentException>(() => NodeBbUpgradeService.BuildHealthCheckScript("/srv/nodebb\\0bad"));
    }

}
