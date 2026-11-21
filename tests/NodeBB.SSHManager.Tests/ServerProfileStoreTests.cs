using System.IO;
using System.Text.Json;
using NodeBB.SSHManager.Models;
using NodeBB.SSHManager.Services;
using Xunit;

namespace NodeBB.SSHManager.Tests;

public sealed class ServerProfileStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "NodeBB-SSH-Manager-Tests", Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_directory, "servers.json");

    public ServerProfileStoreTests()
    {
        Directory.CreateDirectory(_directory);
    }

    [Fact]
    public void Load_WhenFileDoesNotExist_ReturnsEmptyList()
    {
        Assert.Empty(new ServerProfileStore(FilePath).Load());
    }

    [Fact]
    public void SaveAndLoad_RoundTripsProfiles()
    {
        var store = new ServerProfileStore(FilePath);
        var profile = CreateProfile();

        store.Save([profile]);

        var loaded = Assert.Single(store.Load());
        Assert.Equal(profile.Id, loaded.Id);
        Assert.Equal(profile.Name, loaded.Name);
        Assert.Equal(profile.Group, loaded.Group);
        Assert.Equal(profile.Host, loaded.Host);
        Assert.Equal(profile.Port, loaded.Port);
        Assert.Equal(profile.Username, loaded.Username);
        Assert.Equal(profile.AuthenticationType, loaded.AuthenticationType);
        Assert.Equal(profile.PrivateKeyPath, loaded.PrivateKeyPath);
        Assert.Equal(profile.KnownHostFingerprint, loaded.KnownHostFingerprint);
        Assert.Equal(profile.IsFavorite, loaded.IsFavorite);
    }

    [Fact]
    public void FormatPowerShell_BuildsPasswordAuthenticationCommandWithoutSecrets()
    {
        var profile = CreateProfile();
        profile.AuthenticationType = SshAuthenticationType.Password;

        var command = SshCommandFormatter.FormatPowerShell(profile);

        Assert.Equal("ssh -p 22 'nodebb@forum.example.org'", command);
        Assert.DoesNotContain("password", command, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FormatPowerShell_IncludesQuotedPrivateKeyPath()
    {
        var profile = CreateProfile();
        profile.PrivateKeyPath = @"C:\Users\Test User\key's id_ed25519";

        var command = SshCommandFormatter.FormatPowerShell(profile);

        Assert.Equal("ssh -p 22 -i 'C:\\Users\\Test User\\key''s id_ed25519' 'nodebb@forum.example.org'", command);
    }

    [Fact]
    public void FormatPowerShell_RejectsInvalidConnectionDetails()
    {
        var profile = CreateProfile();
        profile.Port = 0;
        Assert.Throws<ArgumentException>(() => SshCommandFormatter.FormatPowerShell(profile));
        profile.Port = 22;
        profile.Host = " ";
        Assert.Throws<ArgumentException>(() => SshCommandFormatter.FormatPowerShell(profile));
    }

    [Fact]
    public void CreateDuplicate_GeneratesNewIdentityAndDoesNotCopyFavoriteOrRuntimeState()
    {
        var original = CreateProfile();
        original.IsConnected = true;
        original.HasSavedCredential = true;
        original.ConnectionCheckLabel = "נגיש · SSH תקין";

        var duplicate = original.CreateDuplicate("Duplicate");

        Assert.NotEqual(original.Id, duplicate.Id);
        Assert.Equal("Duplicate", duplicate.Name);
        Assert.Equal(original.Group, duplicate.Group);
        Assert.Equal(original.Host, duplicate.Host);
        Assert.Equal(original.Port, duplicate.Port);
        Assert.Equal(original.Username, duplicate.Username);
        Assert.Equal(original.AuthenticationType, duplicate.AuthenticationType);
        Assert.Equal(original.PrivateKeyPath, duplicate.PrivateKeyPath);
        Assert.Equal(original.KnownHostFingerprint, duplicate.KnownHostFingerprint);
        Assert.False(duplicate.IsFavorite);
        Assert.False(duplicate.IsConnected);
        Assert.False(duplicate.HasSavedCredential);
        Assert.Equal("טרם נבדק", duplicate.ConnectionCheckLabel);
    }

    [Fact]
    public void CreateDuplicate_RejectsInvalidName()
    {
        var profile = CreateProfile();
        Assert.Throws<ArgumentException>(() => profile.CreateDuplicate(" "));
        Assert.Throws<ArgumentException>(() => profile.CreateDuplicate(new string('x', 121)));
    }

    [Fact]
    public void Save_ReplacesExistingFileAndLeavesNoTemporaryFiles()
    {
        var store = new ServerProfileStore(FilePath);
        store.Save([CreateProfile("First")]);
        store.Save([CreateProfile("Second")]);

        Assert.Equal("Second", Assert.Single(store.Load()).Name);
        Assert.Empty(Directory.GetFiles(_directory, ".servers-*.tmp"));
    }

    [Fact]
    public void Save_RejectsInvalidProfileWithoutChangingExistingFile()
    {
        var store = new ServerProfileStore(FilePath);
        var valid = CreateProfile("Original");
        store.Save([valid]);

        var invalid = CreateProfile("Invalid");
        invalid.Port = 70000;

        Assert.Throws<InvalidDataException>(() => store.Save([invalid]));
        Assert.Equal("Original", Assert.Single(store.Load()).Name);
    }

    [Fact]
    public void Save_RejectsInvalidGroupName()
    {
        var profile = CreateProfile();
        profile.Group = new string('x', 81);
        Assert.Throws<InvalidDataException>(() => new ServerProfileStore(FilePath).Save([profile]));
        Assert.False(File.Exists(FilePath));
    }

    [Theory]
    [InlineData("forum.example.org/path")]
    [InlineData("forum.example.org\\\\share")]
    [InlineData("forum.example.org@evil.example")]
    public void Save_RejectsUriAndPathSyntaxInHost(string host)
    {
        var profile = CreateProfile();
        profile.Host = host;
        Assert.Throws<InvalidDataException>(() => new ServerProfileStore(FilePath).Save([profile]));
    }

    [Fact]
    public void Save_RejectsControlCharactersInProfileIdentityFields()
    {
        var store = new ServerProfileStore(FilePath);
        var name = CreateProfile();
        name.Name = "Forum" + '\n' + "Injected";
        Assert.Throws<InvalidDataException>(() => store.Save([name]));
        var username = CreateProfile();
        username.Username = "nodebb" + '\r' + "admin";
        Assert.Throws<InvalidDataException>(() => store.Save([username]));
    }

    [Fact]
    public void Save_RejectsDuplicateProfileIds()
    {
        var first = CreateProfile("First");
        var second = CreateProfile("Second");
        second.Id = first.Id;

        Assert.Throws<InvalidDataException>(() => new ServerProfileStore(FilePath).Save([first, second]));
        Assert.False(File.Exists(FilePath));
    }

    [Fact]
    public void Load_WhenJsonIsCorrupt_PreservesOriginalFileAndReportsBackup()
    {
        const string corruptJson = "{ this is not valid json";
        File.WriteAllText(FilePath, corruptJson);
        var store = new ServerProfileStore(FilePath);

        var exception = Assert.Throws<InvalidDataException>(() => store.Load());

        Assert.Contains("עותק הקובץ הפגום נשמר כאן", exception.Message);
        Assert.Equal(corruptJson, File.ReadAllText(FilePath));
        var backup = Assert.Single(Directory.GetFiles(_directory, "servers.json.corrupt-*.bak"));
        Assert.Equal(corruptJson, File.ReadAllText(backup));
    }

    [Fact]
    public void Load_WhenProfileDataIsInvalid_PreservesOriginalFile()
    {
        var invalid = CreateProfile();
        invalid.Port = 0;
        File.WriteAllText(FilePath, JsonSerializer.Serialize(new[] { invalid }));

        Assert.Throws<InvalidDataException>(() => new ServerProfileStore(FilePath).Load());

        Assert.Single(Directory.GetFiles(_directory, "servers.json.corrupt-*.bak"));
    }


    [Fact]
    public void Save_RejectsMoreThanMaximumProfiles()
    {
        var profiles = Enumerable.Range(0, 501).Select(i => CreateProfile($"Server {i}")).ToArray();
        Assert.Throws<InvalidDataException>(() => new ServerProfileStore(FilePath).Save(profiles));
        Assert.False(File.Exists(FilePath));
    }

    [Fact]
    public void Save_RejectsOverlongProfileFields()
    {
        var profile = CreateProfile();
        profile.Name = new string('x', 121);
        Assert.Throws<InvalidDataException>(() => new ServerProfileStore(FilePath).Save([profile]));
        Assert.False(File.Exists(FilePath));
    }

    private static ServerProfile CreateProfile(string name = "Test server") => new()
    {
        Name = name,
        Group = "Production",
        Host = "forum.example.org",
        Port = 22,
        Username = "nodebb",
        AuthenticationType = SshAuthenticationType.PrivateKey,
        PrivateKeyPath = Path.Combine(Path.GetTempPath(), "id_ed25519"),
        KnownHostFingerprint = "SHA256:example-fingerprint",
        IsFavorite = true
    };

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, recursive: true);
        }
        catch
        {
            // Test cleanup must not hide an assertion failure.
        }
    }
}
