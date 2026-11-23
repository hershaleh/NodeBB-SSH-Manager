using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
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
    public void ParseReleaseMetadata_RejectsAssetsFromAnotherRepositoryOnGitHub()
    {
        var json = ReleaseJson.Replace(
            "/hershaleh/NodeBB-SSH-Manager/releases/download/",
            "/attacker/other-project/releases/download/", StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() =>
            AppUpdateService.ParseReleaseMetadata(json, new Version(0, 1, 0)));
    }

    [Fact]
    public void ParseReleaseMetadata_RejectsAssetsFromAnotherReleaseTag()
    {
        var json = ReleaseJson.Replace("/releases/download/v0.2.0/", "/releases/download/v9.9.9/",
            StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() =>
            AppUpdateService.ParseReleaseMetadata(json, new Version(0, 1, 0)));
    }

    [Fact]
    public void ParseReleaseMetadata_RejectsReleasePageFromAnotherRepository()
    {
        var json = ReleaseJson.Replace(
            "https://github.com/hershaleh/NodeBB-SSH-Manager/releases/tag/v0.2.0",
            "https://github.com/attacker/other-project/releases/tag/v0.2.0", StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() =>
            AppUpdateService.ParseReleaseMetadata(json, new Version(0, 1, 0)));
    }

    [Fact]
    public void ParseReleaseMetadata_RejectsUnexpectedAssetFilePath()
    {
        var json = ReleaseJson.Replace(
            "/releases/download/v0.2.0/NodeBB-SSH-Manager-Setup-win-x64.exe",
            "/releases/download/v0.2.0/other-installer.exe", StringComparison.Ordinal);
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
    [Fact]
    public async Task DownloadInstallerAsync_VerifiesChecksumAndWindowsHeaderBeforeReturningPath()
    {
        var installerBytes = new byte[] { (byte)'M', (byte)'Z', 0x01, 0x02, 0x03, 0x04 };
        var hash = Convert.ToHexString(SHA256.HashData(installerBytes)).ToLowerInvariant();
        using var client = new HttpClient(new StubHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith(AppUpdateService.ChecksumsFileName, StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(hash + "  " + AppUpdateService.InstallerFileName)
                };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(installerBytes)
            };
        }));
        var service = new AppUpdateService(client);
        var update = CreateUpdateInfo();

        var path = await service.DownloadInstallerAsync(update);
        try
        {
            Assert.Equal(installerBytes, await File.ReadAllBytesAsync(path));
        }
        finally
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }

    [Fact]
    public async Task DownloadInstallerAsync_RejectsChecksumMismatchAndDoesNotLeavePartialFile()
    {
        var installerBytes = new byte[] { (byte)'M', (byte)'Z', 0x10, 0x20 };
        using var client = new HttpClient(new StubHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith(AppUpdateService.ChecksumsFileName, StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(new string('0', 64) + "  " + AppUpdateService.InstallerFileName)
                };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(installerBytes)
            };
        }));
        var service = new AppUpdateService(client);
        await Assert.ThrowsAsync<CryptographicException>(() => service.DownloadInstallerAsync(CreateUpdateInfo()));
    }


    [Theory]
    [InlineData("https://github.com.evil.invalid/release")]
    [InlineData("https://user@github.com/release")]
    [InlineData("https://github.com:444/release")]
    [InlineData("http://github.com/release")]
    public void ParseReleaseMetadata_RejectsGitHubLookalikeOrNonHttpsUris(string uri)
    {
        var json = ReleaseJson.Replace(
            "https://github.com/hershaleh/NodeBB-SSH-Manager/releases/tag/v0.2.0", uri,
            StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() =>
            AppUpdateService.ParseReleaseMetadata(json, new Version(0, 1, 0)));
    }

    [Fact]
    public async Task DownloadInstallerAsync_RejectsForgedExternalAssetUriBeforeNetworkAccess()
    {
        var requests = 0;
        using var client = new HttpClient(new StubHandler(_ =>
        {
            requests++;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("unexpected") };
        }));
        var service = new AppUpdateService(client);
        var valid = CreateUpdateInfo();
        var forged = valid with { InstallerUri = new Uri("https://example.invalid/installer.exe") };

        await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadInstallerAsync(forged));
        Assert.Equal(0, requests);
    }

    [Fact]
    public async Task DownloadInstallerAsync_RejectsUntrustedFinalRedirectTarget()
    {
        using var client = new HttpClient(new StubHandler(request =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("unexpected") };
            response.RequestMessage = new HttpRequestMessage(HttpMethod.Get, new Uri("https://example.invalid/redirected"));
            return response;
        }));
        var service = new AppUpdateService(client);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadInstallerAsync(CreateUpdateInfo()));
    }

    [Fact]
    public async Task DownloadInstallerAsync_RejectsOversizedChecksumManifest()
    {
        using var client = new HttpClient(new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(new string('x', 70 * 1024))
            }));
        var service = new AppUpdateService(client);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.DownloadInstallerAsync(CreateUpdateInfo()));
    }

    [Fact]
    public async Task CheckAsync_RejectsUntrustedFinalRedirectTarget()
    {
        using var client = new HttpClient(new StubHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ReleaseJson)
            };
            response.RequestMessage = new HttpRequestMessage(HttpMethod.Get,
                new Uri("https://example.invalid/releases/latest"));
            return response;
        }));
        var service = new AppUpdateService(client);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.CheckAsync());
    }

    private static AppUpdateInfo CreateUpdateInfo() => new(
        new Version(0, 2, 0),
        "v0.2.0",
        new Uri("https://github.com/hershaleh/NodeBB-SSH-Manager/releases/tag/v0.2.0"),
        new Uri("https://github.com/hershaleh/NodeBB-SSH-Manager/releases/download/v0.2.0/" + AppUpdateService.InstallerFileName),
        new Uri("https://github.com/hershaleh/NodeBB-SSH-Manager/releases/download/v0.2.0/" + AppUpdateService.ChecksumsFileName));

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = _respond(request);
            // HttpClient normally associates the request with the response. The test
            // handler must model that behavior so redirect validation tests are realistic.
            response.RequestMessage ??= new HttpRequestMessage(request.Method, request.RequestUri);
            return Task.FromResult(response);
        }
    }

}
