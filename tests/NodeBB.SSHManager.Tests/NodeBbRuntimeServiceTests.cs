using NodeBB.SSHManager.Services;
using Xunit;

namespace NodeBB.SSHManager.Tests;

public sealed class NodeBbRuntimeServiceTests
{


    [Fact]
    public void BuildHttpStoppedCheckScript_InspectsListeningPortAndFailsClosedWithoutTools()
    {
        var script = NodeBbRuntimeService.BuildHttpStoppedCheckScript("/srv/nodebb");
        Assert.Contains("ss", script);
        Assert.Contains("netstat", script);
        Assert.Contains("NODEBB_STOPPED", script);
        Assert.Contains("attempts >= 10", script);
        Assert.Contains("לא ניתן לאמת בבטחה", script);
    }

    [Theory]
    [InlineData("relative/nodebb")]
    [InlineData("/")]
    [InlineData("/srv/nodebb/../other")]
    public void BuildHttpStoppedCheckScript_RejectsUnsafeInstallationPaths(string path)
    {
        Assert.Throws<ArgumentException>(() => NodeBbRuntimeService.BuildHttpStoppedCheckScript(path));
    }

    [Fact]
    public void BuildHttpHealthCheckScript_UsesLocalHttpProbeAndRetriesUntilHealthy()
    {
        var script = NodeBbRuntimeService.BuildHttpHealthCheckScript("/srv/nodebb");
        Assert.Contains("config.json", script);
        Assert.Contains("http.get", script);
        Assert.Contains("NODEBB_HEALTHY", script);
        Assert.Contains("maxAttempts = 30", script);
        Assert.Contains("path: apiPath", script);
        Assert.Contains("const apiPath = relativePath + '/api/'", script);
        Assert.Contains("response.statusCode < 200 || response.statusCode >= 300", script);
        Assert.Contains("JSON.parse(Buffer.concat(chunks).toString('utf8'))", script);
        Assert.Contains("typeof payload.loggedIn !== 'boolean'", script);
        Assert.Contains("typeof payload.relative_path !== 'string'", script);
        Assert.Contains("bytes > 1024 * 1024", script);
        Assert.DoesNotContain("path: '/',", script);
        Assert.Contains("127.0.0.1", script);
        Assert.Contains("4567", script);
    }

    [Theory]
    [InlineData("relative/nodebb")]
    [InlineData("/")]
    [InlineData("/srv/nodebb/../other")]
    public void BuildHttpHealthCheckScript_RejectsUnsafeInstallationPaths(string path)
    {
        Assert.Throws<ArgumentException>(() => NodeBbRuntimeService.BuildHttpHealthCheckScript(path));
    }

    [Theory]
    [InlineData("SYSTEMD", "nodebb.service", true)]
    [InlineData("OPENRC", "nodebb", true)]
    [InlineData("SYSV", "nodebb", true)]
    [InlineData("DOCKER", "0123456789ab", true)]
    [InlineData("PODMAN", "0123456789abcdef0123456789abcdef", true)]
    [InlineData("PM2", "12345678", true)]
    [InlineData("MANUAL", "CLI", true)]
    [InlineData("DOCKER", "nodebb", false)]
    [InlineData("PODMAN", "nodebb", false)]
    [InlineData("SYSTEMD", "nodebb;touch /tmp/pwned", false)]
    [InlineData("PM2", "1;id", false)]
    [InlineData("MANUAL", "anything-else", false)]
    [InlineData("UNKNOWN", "nodebb", false)]
    public void IsSafeIdentifier_ValidatesManagerSpecificIdentifiers(string manager, string identifier, bool expected)
    {
        Assert.Equal(expected, NodeBbRuntimeService.IsSafeIdentifier(manager, identifier));
    }
}
