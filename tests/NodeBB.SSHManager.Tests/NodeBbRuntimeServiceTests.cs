using NodeBB.SSHManager.Services;
using Xunit;

namespace NodeBB.SSHManager.Tests;

public sealed class NodeBbRuntimeServiceTests
{

    [Fact]
    public void BuildHttpHealthCheckScript_UsesLocalHttpProbeAndRetriesUntilHealthy()
    {
        var script = NodeBbRuntimeService.BuildHttpHealthCheckScript("/srv/nodebb");
        Assert.Contains("config.json", script);
        Assert.Contains("http.get", script);
        Assert.Contains("NODEBB_HEALTHY", script);
        Assert.Contains("maxAttempts = 30", script);
        Assert.Contains("response.statusCode < 400", script);
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
