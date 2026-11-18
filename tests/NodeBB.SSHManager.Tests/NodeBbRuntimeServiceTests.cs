using NodeBB.SSHManager.Services;
using Xunit;

namespace NodeBB.SSHManager.Tests;

public sealed class NodeBbRuntimeServiceTests
{
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
