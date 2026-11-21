using System;
using Xunit;
using NodeBB.SSHManager.Services;

namespace NodeBB.SSHManager.Tests;

public sealed class RemotePathSafetyTests
{
    [Theory]
    [InlineData("/srv/nodebb")]
    [InlineData("/srv/nodebb/")]
    [InlineData("/srv/nodebb's forum")]
    [InlineData("/home/user/.local/share/nodebb")]
    public void ValidateNodeBbInstallationPath_AcceptsSafeAbsolutePaths(string path) =>
        RemotePathSafety.ValidateNodeBbInstallationPath(path);

    [Theory]
    [InlineData("")]
    [InlineData("relative/nodebb")]
    [InlineData("/")]
    [InlineData("///")]
    [InlineData("/srv/nodebb/../other")]
    [InlineData("/srv/nodebb/./forum")]
    [InlineData("/srv/nodebb\\forum")]
    [InlineData("/srv/nodebb\nforum")]
    public void ValidateNodeBbInstallationPath_RejectsUnsafePaths(string path) =>
        Assert.Throws<ArgumentException>(() => RemotePathSafety.ValidateNodeBbInstallationPath(path));
}
