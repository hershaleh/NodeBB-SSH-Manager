using System.Text;
using NodeBB.SSHManager.Services;
using Xunit;

namespace NodeBB.SSHManager.Tests;

public sealed class NodeBbRemoteServiceTests
{
    private static readonly (int ExitCode, string Output, string Error) Success = (0, "command completed", "");

    [Fact]
    public async Task StartRequiresSuccessfulHttpHealthCheck()
    {
        var checkedHealth = false;
        var result = await NodeBbRemoteService.VerifyLifecycleOutcomeAsync(
            "start", Success,
            () => Task.FromResult(false),
            () => { checkedHealth = true; return Task.FromResult(true); });

        Assert.True(checkedHealth);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("ממשק HTTP", result.Output);
    }

    [Theory]
    [InlineData("start")]
    [InlineData("restart")]
    public async Task StartAndRestartFailWhenHttpHealthCheckFails(string action)
    {
        var result = await NodeBbRemoteService.VerifyLifecycleOutcomeAsync(
            action, Success,
            () => Task.FromResult(false),
            () => Task.FromResult(false));

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("לא סיפק תגובת API תקינה", result.Error);
    }

    [Fact]
    public async Task StopRequiresConfirmedPortRelease()
    {
        var checkedStopped = false;
        var result = await NodeBbRemoteService.VerifyLifecycleOutcomeAsync(
            "stop", Success,
            () => { checkedStopped = true; return Task.FromResult(true); },
            () => Task.FromResult(false));

        Assert.True(checkedStopped);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("אין תהליך שמאזין", result.Output);
    }

    [Fact]
    public async Task StopFailsWhenPortStillAppearsActive()
    {
        var result = await NodeBbRemoteService.VerifyLifecycleOutcomeAsync(
            "stop", Success,
            () => Task.FromResult(false),
            () => Task.FromResult(true));

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("עדיין לא ניתן לאמת", result.Error);
    }

    [Theory]
    [InlineData("start")]
    [InlineData("stop")]
    [InlineData("restart")]
    [InlineData("build")]
    public async Task FailedCommandDoesNotRunVerification(string action)
    {
        var called = false;
        var failed = (17, "command failed", "remote error");
        var result = await NodeBbRemoteService.VerifyLifecycleOutcomeAsync(
            action, failed,
            () => { called = true; return Task.FromResult(true); },
            () => { called = true; return Task.FromResult(true); });

        Assert.False(called);
        Assert.Equal(failed, result);
    }

    [Fact]
    public async Task BuildDoesNotRunHttpLifecycleVerification()
    {
        var called = false;
        var result = await NodeBbRemoteService.VerifyLifecycleOutcomeAsync(
            "build", Success,
            () => { called = true; return Task.FromResult(true); },
            () => { called = true; return Task.FromResult(true); });

        Assert.False(called);
        Assert.Equal(Success, result);
    }

    [Fact]
    public void ManualInstallationParserAcceptsNodeBbManifest()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(
            "{\"name\":\"nodebb\",\"version\":\"4.16.7\"}"));

        var installation = NodeBbRemoteService.ParseInstallationManifest("/srv/forums/nodebb/", stream);

        Assert.Equal("/srv/forums/nodebb", installation.Path);
        Assert.Equal("4.16.7", installation.Version);
    }

    [Fact]
    public void ManualInstallationParserAcceptsNodeBbNameCaseInsensitively()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(
            "{\"name\":\"NodeBB\"}"));

        var installation = NodeBbRemoteService.ParseInstallationManifest("/opt/nodebb", stream);

        Assert.Equal("לא ידועה", installation.Version);
    }

    [Theory]
    [InlineData("{\"name\":\"other-app\",\"version\":\"1.0\"}")]
    [InlineData("{\"version\":\"1.0\"}")]
    [InlineData("[]")]
    public void ManualInstallationParserRejectsNonNodeBbManifest(string json)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));

        Assert.Throws<InvalidDataException>(() =>
            NodeBbRemoteService.ParseInstallationManifest("/srv/nodebb", stream));
    }

    [Fact]
    public void ManualInstallationParserRejectsMalformedJson()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("{not-json"));

        Assert.Throws<System.Text.Json.JsonException>(() =>
            NodeBbRemoteService.ParseInstallationManifest("/srv/nodebb", stream));
    }

    [Theory]
    [InlineData("relative/nodebb")]
    [InlineData("/")]
    [InlineData("/srv/nodebb/../other")]
    [InlineData("/srv/nodebb\\other")]
    public void ManualInstallationParserRejectsUnsafePaths(string path)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("{\"name\":\"nodebb\"}"));

        Assert.Throws<ArgumentException>(() =>
            NodeBbRemoteService.ParseInstallationManifest(path, stream));
    }

    [Theory]
    [InlineData("relative/nodebb")]
    [InlineData("/")]
    [InlineData("/srv/nodebb/../other")]
    [InlineData("/srv/nodebb\\other")]
    public void LifecycleRejectsUnsafeInstallationPaths(string path)
    {
        Assert.Throws<ArgumentException>(() =>
            RemotePathSafety.ValidateNodeBbInstallationPath(path));
    }
}
