using NodeBB.SSHManager.Services;
using Xunit;

namespace NodeBB.SSHManager.Tests;

public sealed class RemoteServiceManagerServiceTests
{
    [Theory]
    [InlineData("ssh", true)]
    [InlineData("nodebb.service", true)]
    [InlineData("redis-server", true)]
    [InlineData("dbus-org.freedesktop.service", true)]
    [InlineData("worker@2.service", true)]
    [InlineData("_custom.service", true)]
    [InlineData("-help", false)]
    [InlineData("--system", false)]
    [InlineData("", false)]
    [InlineData(" ", false)]
    [InlineData("bad name", false)]
    [InlineData("name;reboot", false)]
    [InlineData("name/other", false)]
    [InlineData("name\nother", false)]
    [InlineData("name" + new string('a', 128), false)]
    public void IsSafeServiceName_RejectsOptionInjectionAndInvalidNames(string? name, bool expected)
    {
        Assert.Equal(expected, RemoteServiceManagerService.IsSafeServiceName(name));
    }

    [Theory]
    [InlineData("SYSTEMD", true)]
    [InlineData("OPENRC", true)]
    [InlineData("SYSV", true)]
    [InlineData("systemd", false)]
    [InlineData("--help", false)]
    [InlineData("UNKNOWN", false)]
    public void IsSupportedManager_RequiresKnownManager(string? manager, bool expected)
    {
        Assert.Equal(expected, RemoteServiceManagerService.IsSupportedManager(manager));
    }
}
