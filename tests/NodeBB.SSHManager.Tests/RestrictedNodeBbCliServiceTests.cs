using NodeBB.SSHManager.Services;

namespace NodeBB.SSHManager.Tests;

public sealed class RestrictedNodeBbCliServiceTests
{
    [Theory]
    [InlineData("nodebb-plugin-spoilers")]
    [InlineData("@scope/nodebb-plugin-example")]
    [InlineData("nodebb-plugin-example@1.2.3")]
    public void ValidateInstallTarget_AcceptsNpmPackages(string value) =>
        Assert.Equal(value, RestrictedNodeBbCliService.ValidateInstallTarget(value));

    [Theory]
    [InlineData("https://github.com/NodeBB-Community/nodebb-plugin-spoilers")]
    [InlineData("https://github.com/owner/repo#main")]
    public void ValidateInstallTarget_AcceptsGithubHttps(string value) =>
        Assert.Equal(value, RestrictedNodeBbCliService.ValidateInstallTarget(value));

    [Fact]
    public void ValidateInstallTarget_AcceptsZipFile() =>
        Assert.Equal("my-plugin.zip", RestrictedNodeBbCliService.ValidateInstallTarget("my-plugin.zip"));

    [Theory]
    [InlineData("nodebb-plugin-x;reboot")]
    [InlineData("$(id)")]
    [InlineData("../plugin.zip")]
    [InlineData("https://evil.example/owner/repo")]
    [InlineData("-rf")]
    public void ValidateInstallTarget_RejectsUnsafeTargets(string value) =>
        Assert.Throws<ArgumentException>(() => RestrictedNodeBbCliService.ValidateInstallTarget(value));

    [Theory]
    [InlineData("logs.txt")]
    [InlineData("../bad.zip")]
    [InlineData("bad name.zip")]
    public void ValidateZipName_RejectsUnsafeNames(string value) =>
        Assert.Throws<ArgumentException>(() => RestrictedNodeBbCliService.ValidateZipName(value));
}
