using System.Security.Cryptography;
using System.Text.Json;

namespace NodeBB.SSHManager.Services;

public sealed class CredentialVaultService
{
    private static readonly byte[] Entropy = "NodeBB.SSHManager.CredentialVault.v1"u8.ToArray();
    private static readonly object Sync = new();
    public string StoragePath { get; }

    public CredentialVaultService(string? storagePath = null) => StoragePath = storagePath ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NodeBB SSH Manager", "Credentials", "vault.dat");

    public bool Has(Guid profileId) => Get(profileId) is not null;

    public string? Get(Guid profileId)
    {
        lock (Sync)
        {
            if (!File.Exists(StoragePath)) return null;
            try
            {
                var info = new FileInfo(StoragePath);
                if (info.Length is <= 0 or > 1024 * 1024) throw new InvalidDataException("Credential vault size is invalid.");
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                DiagnosticLogService.Instance.Warning("CredentialVault", "VaultMetadataReadFailed", "Encrypted credential vault metadata could not be read.");
                return null;
            }
            try
            {
                var clear = ProtectedData.Unprotect(File.ReadAllBytes(StoragePath), Entropy, DataProtectionScope.CurrentUser);
                try
                {
                    var entries = JsonSerializer.Deserialize<Dictionary<Guid, string>>(clear);
                    return entries is not null && entries.TryGetValue(profileId, out var secret) ? secret : null;
                }
                finally { CryptographicOperations.ZeroMemory(clear); }
            }
            catch (Exception ex) when (ex is CryptographicException or JsonException or IOException or InvalidDataException or UnauthorizedAccessException)
            {
                DiagnosticLogService.Instance.Warning("CredentialVault", "VaultReadFailed", "Encrypted credential vault could not be read.");
                return null;
            }
        }
    }

    public void Set(Guid profileId, string secret)
    {
        if (profileId == Guid.Empty) throw new ArgumentException("A valid profile ID is required.", nameof(profileId));
        ArgumentNullException.ThrowIfNull(secret);
        lock (Sync) { var entries = ReadAll(); entries[profileId] = secret; WriteAll(entries); }
    }

    public void Remove(Guid profileId)
    {
        lock (Sync)
        {
            if (!File.Exists(StoragePath)) return;
            var entries = ReadAll();
            if (!entries.Remove(profileId)) return;
            if (entries.Count == 0) { File.Delete(StoragePath); return; }
            WriteAll(entries);
        }
    }

    private Dictionary<Guid, string> ReadAll()
    {
        if (!File.Exists(StoragePath)) return [];
        var info = new FileInfo(StoragePath);
        if (info.Length is <= 0 or > 1024 * 1024) throw new InvalidDataException("Credential vault size is invalid.");
        var clear = ProtectedData.Unprotect(File.ReadAllBytes(StoragePath), Entropy, DataProtectionScope.CurrentUser);
        try { return JsonSerializer.Deserialize<Dictionary<Guid, string>>(clear) ?? []; }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }

    private void WriteAll(Dictionary<Guid, string> entries)
    {
        var directory = Path.GetDirectoryName(StoragePath) ?? throw new InvalidOperationException("Credential vault path has no directory.");
        Directory.CreateDirectory(directory);
        var clear = JsonSerializer.SerializeToUtf8Bytes(entries);
        byte[]? protectedBytes = null;
        var temporary = StoragePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            protectedBytes = ProtectedData.Protect(clear, Entropy, DataProtectionScope.CurrentUser);
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(protectedBytes); stream.Flush(true); }
            File.Move(temporary, StoragePath, true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clear);
            if (protectedBytes is not null) CryptographicOperations.ZeroMemory(protectedBytes);
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }
}
