using System;
using System.IO;
using NodeBB.SSHManager.Services;
using Xunit;

namespace NodeBB.SSHManager.Tests;

public sealed class AppUpdateServiceTests
{
    private const string ReleaseJson = """
    {
      "tag_name": "v0.2.0",
      "html_url": "https://github.com/hershaleh/NodeBB-SSH-Manager/releases/tag/v0.2.0",
      "prerelease": false,
      "assets": [
        {
          "name": "NodeBB-SSH-Manager-Setup-win-x64.exe",
          "browser_download_url": "https://github.com/hershaleh/NodeBB-SSH-Manager/releases/download/v0.2.0/NodeBB-SSH-Manager-Setup-win-x64.exe"
        },
        {
          "name": "SHA256SUMS.txt",
          "browser_download_url": "https://github.com/hershaleh/NodeBB-SSH-Manager/releases/download/v0.2.0/SHA256SUMS.txt"
        }
      ]
    }
    """;

    [Fact]
    public void ParseReleaseMetadata_RecognizesNewStableReleaseAndTrustedAssets()
    {
        var result = AppUpdateService.ParseReleaseMetadata(ReleaseJson, new Version(0, 1, 0, 0));
        Assert.True(result.UpdateAvailable);
        Assert.Equal(new Version(0, 2, 0), result.LatestVersion);
        Assert.Equal("v0.2.0", result.Update!.TagName);
        Assert.Equal("github.com", result.Update.InstallerUri.Host);
        Assert.Equal("github.com", result.Update.ChecksumsUri.Host);
    }

    [Fact]
    public void ParseReleaseMetadata_DoesNotOfferSameOrOlderVersion()
    {
        var same = AppUpdateService.ParseReleaseMetadata(ReleaseJson, new Version(0, 2, 0));
        var newerInstalled = AppUpdateService.ParseReleaseMetadata(ReleaseJson, new Version(0, 3, 0));
        Assert.False(same.UpdateAvailable);
        Assert.False(newerInstalled.UpdateAvailable);
    }

    [Fact]
    public void ParseReleaseMetadata_RejectsUntrustedAssetHosts()
    {
        var json = ReleaseJson.Replace(
            "https://github.com/hershaleh/NodeBB-SSH-Manager/releases/download/v0.2.0/NodeBB-SSH-Manager-Setup-win-x64.exe",
            "https://example.invalid/installer.exe", StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() =>
            AppUpdateService.ParseReleaseMetadata(json, new Version(0, 1, 0)));
    }

    [Fact]
    public void ParseReleaseMetadata_RejectsUpdateWithoutChecksumManifest()
    {
        var json = ReleaseJson.Replace("SHA256SUMS.txt", "checksums.txt", StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() =>
            AppUpdateService.ParseReleaseMetadata(json, new Version(0, 1, 0)));
    }

    [Fact]
    public void ParseChecksumManifest_ExtractsExactInstallerHash()
    {
        const string manifest = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa  NodeBB-SSH-Manager-Setup-win-x64.exe\n";
        Assert.Equal(new string('a', 64),
            AppUpdateService.ParseChecksumManifest(manifest, AppUpdateService.InstallerFileName));
    }

    [Theory]
    [InlineData("not-a-hash  NodeBB-SSH-Manager-Setup-win-x64.exe")]
    [InlineData("aaaaaaaa  NodeBB-SSH-Manager-Setup-win-x64.exe")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa  Other.exe")]
    public void ParseChecksumManifest_RejectsMissingOrMalformedHash(string manifest)
    {
        Assert.Throws<InvalidDataException>(() =>
            AppUpdateService.ParseChecksumManifest(manifest, AppUpdateService.InstallerFileName));
    }

    [Fact]
    public void ParseChecksumManifest_RejectsDuplicateInstallerEntries()
    {
        var line = new string('b', 64) + "  " + AppUpdateService.InstallerFileName;
        Assert.Throws<InvalidDataException>(() =>
            AppUpdateService.ParseChecksumManifest(line + "\n" + line, AppUpdateService.InstallerFileName));
    }
}
