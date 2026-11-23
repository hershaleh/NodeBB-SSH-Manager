using System.Text.Json;
using System.IO;
using NodeBB.SSHManager.Models;
using NodeBB.SSHManager.Services;
using Xunit;

namespace NodeBB.SSHManager.Tests;

public sealed class ServerProfileTransferServiceTests
{
    private readonly ServerProfileTransferService _service = new();

    [Fact]
    public void ExportAndImport_RoundTripsConnectionSettingsButNotTrustOrIdentity()
    {
        var original = CreateProfile();
        var json = _service.Export([original]);
        var imported = Assert.Single(_service.Import(json));

        Assert.NotEqual(original.Id, imported.Id);
        Assert.Null(imported.KnownHostFingerprint);
        Assert.Equal(original.Name, imported.Name);
        Assert.Equal(original.Host, imported.Host);
        Assert.Equal(original.Port, imported.Port);
        Assert.Equal(original.Username, imported.Username);
        Assert.Equal(original.AuthenticationType, imported.AuthenticationType);
        Assert.Equal(original.PrivateKeyPath, imported.PrivateKeyPath);
        Assert.DoesNotContain("SHA256:", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Export_ProducesJsonThatCanBeImportedWithinTheConfiguredLimit()
    {
        var profiles = Enumerable.Range(0, 500).Select(i =>
        {
            var profile = CreateProfile();
            profile.Id = Guid.NewGuid();
            profile.Name = $"Server {i}";
            profile.Host = $"server{i}.example.org";
            profile.PrivateKeyPath = new string('k', 2048);
            return profile;
        }).ToArray();

        var json = _service.Export(profiles);
        var imported = _service.Import(json);

        Assert.Equal(500, imported.Count);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(json) <= 2 * 1024 * 1024);
    }

    [Fact]
    public void Import_RejectsInvalidPort()
    {
        var profile = CreateProfile();
        profile.Port = 70000;
        Assert.Throws<InvalidDataException>(() => _service.Import(JsonSerializer.Serialize(new[] { profile })));
    }

    [Fact]
    public void Import_RejectsShellInjectionInHost()
    {
        var profile = CreateProfile();
        profile.Host = "host.example;touch /tmp/pwned";
        Assert.Throws<InvalidDataException>(() => _service.Import(JsonSerializer.Serialize(new[] { profile })));
    }

    [Theory]
    [InlineData("forum.example.org/path")]
    [InlineData("forum.example.org\\\\share")]
    [InlineData("forum.example.org@evil.example")]
    public void Import_RejectsUriAndPathSyntaxInHost(string host)
    {
        var profile = CreateProfile();
        profile.Host = host;
        Assert.Throws<InvalidDataException>(() => _service.Import(JsonSerializer.Serialize(new[] { profile })));
    }

    [Fact]
    public void Import_RejectsControlCharactersInProfileIdentityFields()
    {
        var profile = CreateProfile();
        profile.Name = "Forum" + '\n' + "Injected";
        Assert.Throws<InvalidDataException>(() => _service.Import(JsonSerializer.Serialize(new[] { profile })));
        profile = CreateProfile();
        profile.Username = "nodebb" + '\r' + "admin";
        Assert.Throws<InvalidDataException>(() => _service.Import(JsonSerializer.Serialize(new[] { profile })));
    }

    [Fact]
    public void Import_RejectsDuplicateIds()
    {
        var profile = CreateProfile();
        var duplicate = CreateProfile();
        duplicate.Id = profile.Id;
        Assert.Throws<InvalidDataException>(() => _service.Import(JsonSerializer.Serialize(new[] { profile, duplicate })));
    }

    [Fact]
    public void Import_RejectsOversizedInput()
    {
        Assert.Throws<InvalidDataException>(() => _service.Import(new string(' ', 2 * 1024 * 1024 + 1)));
    }

    [Fact]
    public void Import_RejectsInvalidJson()
    {
        Assert.Throws<InvalidDataException>(() => _service.Import("{not-json"));
    }


    [Fact]
    public void Import_RejectsMoreThanMaximumProfiles()
    {
        var profiles = Enumerable.Range(0, 501).Select(i =>
        {
            var profile = CreateProfile();
            profile.Id = Guid.NewGuid();
            profile.Name = $"Server {i}";
            profile.Host = $"server{i}.example.org";
            return profile;
        }).ToArray();
        Assert.Throws<InvalidDataException>(() => _service.Import(JsonSerializer.Serialize(profiles)));
    }

    [Fact]
    public void Import_RejectsUnknownAuthenticationType()
    {
        var profile = CreateProfile();
        profile.AuthenticationType = (SshAuthenticationType)999;
        Assert.Throws<InvalidDataException>(() => _service.Import(JsonSerializer.Serialize(new[] { profile })));
    }

    [Fact]
    public void Import_RejectsOverlongProfileFields()
    {
        var profile = CreateProfile();
        profile.Name = new string('n', 121);
        Assert.Throws<InvalidDataException>(() => _service.Import(JsonSerializer.Serialize(new[] { profile })));
    }

    private static ServerProfile CreateProfile() => new()
    {
        Name = "Production forum",
        Host = "forum.example.org",
        Port = 2222,
        Username = "nodebb",
        AuthenticationType = SshAuthenticationType.PrivateKey,
        PrivateKeyPath = @"C:\keys\id_ed25519",
        KnownHostFingerprint = "SHA256:trusted-fingerprint"
    };
}
