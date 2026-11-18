using NodeBB.SSHManager.Services;
using Xunit;

namespace NodeBB.SSHManager.Tests;

public sealed class CredentialVaultServiceTests
{
    [Fact]
    public void SetGetAndRemove_UsesCurrentWindowsUserProtectedStorage()
    {
        if (!OperatingSystem.IsWindows()) return;
        var directory = Path.Combine(Path.GetTempPath(), "NodeBBVaultTests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "vault.dat");
        var vault = new CredentialVaultService(path);
        var profileId = Guid.NewGuid();
        const string secret = "Vault-Test-Secret-!234";
        try
        {
            Assert.Null(vault.Get(profileId));
            vault.Set(profileId, secret);
            Assert.Equal(secret, vault.Get(profileId));
            Assert.DoesNotContain(secret, File.ReadAllText(path, System.Text.Encoding.UTF8));
            vault.Remove(profileId);
            Assert.Null(vault.Get(profileId));
            Assert.False(File.Exists(path));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void Set_RejectsEmptySecrets()
    {
        var vault = new CredentialVaultService(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "vault.dat"));
        Assert.Throws<ArgumentException>(() => vault.Set(Guid.NewGuid(), ""));
    }
}
