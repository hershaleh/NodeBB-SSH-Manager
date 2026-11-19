using System.IO;
using NodeBB.SSHManager.Models;
using NodeBB.SSHManager.Services;
using Renci.SshNet;
using Xunit;

namespace NodeBB.SSHManager.Tests;

public sealed class SshSessionManagerTests
{
    [Fact]
    public void CreateAuthentication_UsesPasswordAuthenticationWithoutPersistingOrTransformingSecret()
    {
        const string secret = "correct horse battery staple";
        var profile = new ServerProfile
        {
            Host = "forum.example.org",
            Username = "nodebb",
            AuthenticationType = SshAuthenticationType.Password
        };

        using var authentication = SshSessionManager.CreateAuthentication(profile, secret);

        Assert.IsType<PasswordAuthenticationMethod>(authentication);
    }

    [Fact]
    public void CreateAuthentication_RejectsMissingPrivateKeyBeforeConnecting()
    {
        var profile = new ServerProfile
        {
            Host = "forum.example.org",
            Username = "nodebb",
            AuthenticationType = SshAuthenticationType.PrivateKey,
            PrivateKeyPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing-key")
        };

        Assert.Throws<FileNotFoundException>(() => SshSessionManager.CreateAuthentication(profile, ""));
    }

    [Fact]
    public void CreateAuthentication_RejectsBlankPrivateKeyPath()
    {
        var profile = new ServerProfile
        {
            Host = "forum.example.org",
            Username = "nodebb",
            AuthenticationType = SshAuthenticationType.PrivateKey,
            PrivateKeyPath = " "
        };

        Assert.Throws<FileNotFoundException>(() => SshSessionManager.CreateAuthentication(profile, ""));
    }

    [Fact]
    public void CreateAuthentication_RejectsUnknownAuthenticationType()
    {
        var profile = new ServerProfile
        {
            Host = "forum.example.org",
            Username = "nodebb",
            AuthenticationType = (SshAuthenticationType)999
        };

        Assert.Throws<ArgumentOutOfRangeException>(() => SshSessionManager.CreateAuthentication(profile, ""));
    }
}
