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
        Assert.Equal(profile.Host, loaded.Host);
        Assert.Equal(profile.Port, loaded.Port);
        Assert.Equal(profile.Username, loaded.Username);
        Assert.Equal(profile.AuthenticationType, loaded.AuthenticationType);
        Assert.Equal(profile.PrivateKeyPath, loaded.PrivateKeyPath);
        Assert.Equal(profile.KnownHostFingerprint, loaded.KnownHostFingerprint);
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
        Host = "forum.example.org",
        Port = 22,
        Username = "nodebb",
        AuthenticationType = SshAuthenticationType.PrivateKey,
        PrivateKeyPath = Path.Combine(Path.GetTempPath(), "id_ed25519"),
        KnownHostFingerprint = "SHA256:example-fingerprint"
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


public sealed class CredentialVaultServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "NodeBB-SSH-Manager-Vault-Tests", Guid.NewGuid().ToString("N"));
    private string VaultPath => Path.Combine(_directory, "vault.dat");

    [Fact]
    public void SetGetAndRemove_StoresOnlyForCurrentWindowsUser()
    {
        var vault = new CredentialVaultService(VaultPath);
        var id = Guid.NewGuid();
        vault.Set(id, "test-secret-value");
        Assert.True(vault.Has(id));
        Assert.Equal("test-secret-value", vault.Get(id));
        Assert.DoesNotContain("test-secret-value", File.ReadAllText(VaultPath));
        vault.Remove(id);
        Assert.False(vault.Has(id));
        Assert.Null(vault.Get(id));
        Assert.False(File.Exists(VaultPath));
    }

    [Fact]
    public void Get_WhenVaultIsMissing_ReturnsNull()
        => Assert.Null(new CredentialVaultService(VaultPath).Get(Guid.NewGuid()));

    [Fact]
    public void Set_RejectsEmptySecret()
        => Assert.Throws<ArgumentException>(() => new CredentialVaultService(VaultPath).Set(Guid.NewGuid(), ""));

    [Fact]
    public void Get_WhenVaultIsOversized_FailsClosed()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllBytes(VaultPath, new byte[1024 * 1024 + 1]);
        Assert.Null(new CredentialVaultService(VaultPath).Get(Guid.NewGuid()));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
        catch { }
    }
}
