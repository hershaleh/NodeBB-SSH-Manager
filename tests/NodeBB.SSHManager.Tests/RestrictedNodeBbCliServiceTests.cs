using Xunit;
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
    [InlineData("nodebb-plugin-spoilers\nnodebb-theme-modern", 2)]
    [InlineData("@nodebb/plugin-write-api\n@nodebb/theme-harmony", 2)]
    [InlineData("@scope/nodebb-plugin-example\n@scope/plugin-extra", 2)]
    [InlineData("No plugins are currently installed.", 0)]
    public void ExtractPluginPackageNames_RecognizesSupportedPackageFormats(string output, int expectedCount) =>
        Assert.Equal(expectedCount, RestrictedNodeBbCliService.ExtractPluginPackageNames(output).Count);

    [Theory]
    [InlineData("nodebb-plugin-spoilers", "תוסף")]
    [InlineData("@nodebb/plugin-write-api", "תוסף")]
    [InlineData("nodebb-theme-harmony", "ערכת עיצוב")]
    [InlineData("@nodebb/theme-harmony", "ערכת עיצוב")]
    [InlineData("@scope/nodebb-theme-modern", "ערכת עיצוב")]
    public void GetPackageKindLabel_ClassifiesPluginAndThemePackages(string packageName, string expected) =>
        Assert.Equal(expected, RestrictedNodeBbCliService.GetPackageKindLabel(packageName));

    [Fact]
    public void ExtractSuccessfulPackageNames_RecognizesOnlySuccessfulRequestedPackages()
    {
        const string output = "[הצלחה] nodebb-plugin-alpha\\ninstalled\\n[כישלון] nodebb-plugin-beta\\nerror\\n[הצלחה] nodebb-plugin-alpha";
        var result = RestrictedNodeBbCliService.ExtractSuccessfulPackageNames(
            output, new[] { "nodebb-plugin-alpha", "nodebb-plugin-beta", "nodebb-plugin-gamma" });
        Assert.Equal(new[] { "nodebb-plugin-alpha" }, result);
    }

    [Fact]
    public void ExtractSuccessfulPackageNames_IsCaseInsensitiveAndIgnoresUnrequestedPackages()
    {
        const string output = "[הצלחה] NODEBB-PLUGIN-ALPHA\\n[הצלחה] nodebb-plugin-outsider";
        var result = RestrictedNodeBbCliService.ExtractSuccessfulPackageNames(
            output, new[] { "nodebb-plugin-alpha" });
        Assert.Equal(new[] { "NODEBB-PLUGIN-ALPHA" }, result);
    }

    [Fact]
    public void ExtractPluginPackageNames_DeduplicatesAndSortsCaseInsensitively()
    {
        var names = RestrictedNodeBbCliService.ExtractPluginPackageNames(
            "nodebb-plugin-alpha\nnodebb-plugin-beta\nnodebb-plugin-alpha");
        Assert.Equal(new[] { "nodebb-plugin-alpha", "nodebb-plugin-beta" }, names);
    }

    [Theory]
    [InlineData("logs.txt")]
    [InlineData("../bad.zip")]
    [InlineData("bad name.zip")]
    public void ValidateZipName_RejectsUnsafeNames(string value) =>
        Assert.Throws<ArgumentException>(() => RestrictedNodeBbCliService.ValidateZipName(value));
}
