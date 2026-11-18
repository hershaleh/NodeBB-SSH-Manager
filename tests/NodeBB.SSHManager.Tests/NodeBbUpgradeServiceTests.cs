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
        Assert.Contains("git fetch --tags origin", script);
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
}
