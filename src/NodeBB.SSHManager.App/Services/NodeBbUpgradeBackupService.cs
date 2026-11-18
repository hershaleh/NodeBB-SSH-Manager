using System.IO.Compression;
using System.Security.Cryptography;

namespace NodeBB.SSHManager.Services;

public sealed record UpgradeBackupArtifact(string Description, string FilePath, long SizeBytes);
public sealed record UpgradeBackupSet(string DirectoryPath, IReadOnlyList<UpgradeBackupArtifact> Artifacts);

/// <summary>Creates and verifies encrypted database and configuration/uploads backups before an upgrade.</summary>
public sealed class NodeBbUpgradeBackupService
{
    private readonly SshServerSession _session;
    public NodeBbUpgradeBackupService(SshServerSession session) => _session = session;

    public async Task<UpgradeBackupSet> CreateAsync(NodeBbInstallation installation, string databaseType,
        string destinationRoot, string password, IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(installation);
        ValidatePassword(password);
        var db = NormalizeDatabaseType(databaseType);
        if (string.IsNullOrWhiteSpace(destinationRoot))
            throw new ArgumentException("יש לבחור תיקייה לשמירת הגיבויים.", nameof(destinationRoot));

        var root = Path.GetFullPath(destinationRoot);
        Directory.CreateDirectory(root);
        var backupDirectory = Path.Combine(root, $"NodeBB-PreUpgrade-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(backupDirectory);
        var workDirectory = Path.Combine(backupDirectory, ".working");
        Directory.CreateDirectory(workDirectory);
        var artifacts = new List<UpgradeBackupArtifact>();

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var databasePlain = Path.Combine(workDirectory, db switch
            {
                "mongo" => "database.archive.gz",
                "postgres" => "database.dump",
                "redis" => "database.rdb",
                _ => throw new InvalidOperationException("סוג מסד נתונים לא נתמך.")
            });
            var databaseEncrypted = Path.Combine(backupDirectory, $"database-{db}.nbbbackup");
            progress?.Report($"יוצר גיבוי מסד נתונים ({db})...");

            switch (db)
            {
                case "mongo":
                    await new MongoDatabaseBackupService(_session).DownloadBackupAsync(installation, databasePlain, cancellationToken);
                    await ValidateGzipAsync(databasePlain, cancellationToken);
                    break;
                case "postgres":
                    await new PostgresDatabaseBackupService(_session).DownloadBackupAsync(installation, databasePlain, cancellationToken);
                    await PostgresDatabaseBackupService.ValidateDumpAsync(databasePlain, cancellationToken);
                    break;
                case "redis":
                    await new RedisDatabaseBackupService(_session).DownloadBackupAsync(installation, databasePlain, cancellationToken);
                    await RedisDatabaseBackupService.ValidateRdbAsync(databasePlain, cancellationToken);
                    break;
            }

            await EncryptAndVerifyAsync(databasePlain, databaseEncrypted, password, cancellationToken);
            artifacts.Add(new UpgradeBackupArtifact($"מסד נתונים ({db})", databaseEncrypted, new FileInfo(databaseEncrypted).Length));
            File.Delete(databasePlain);

            cancellationToken.ThrowIfCancellationRequested();
            var filesPlain = Path.Combine(workDirectory, "configuration-uploads.tar.gz");
            var filesEncrypted = Path.Combine(backupDirectory, "configuration-uploads.nbbbackup");
            progress?.Report("יוצר גיבוי config.json, קובצי התצורה והעלאות...");
            await new RemoteBackupService(_session).DownloadBackupAsync(
                installation, RemoteBackupKind.ConfigurationAndUploads, filesPlain, cancellationToken);
            await ValidateGzipAsync(filesPlain, cancellationToken);
            await EncryptAndVerifyAsync(filesPlain, filesEncrypted, password, cancellationToken);
            artifacts.Add(new UpgradeBackupArtifact("קובצי תצורה והעלאות", filesEncrypted, new FileInfo(filesEncrypted).Length));
            File.Delete(filesPlain);

            progress?.Report("שני הגיבויים נוצרו, אומתו והוצפנו בהצלחה.");
            return new UpgradeBackupSet(backupDirectory, artifacts);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"יצירת גיבויי טרום-השדרוג נכשלה. השדרוג לא בוצע. גיבויים שכבר הושלמו נשמרו בתיקייה: {backupDirectory}. פרטים: {ex.Message}", ex);
        }
        finally
        {
            try { if (Directory.Exists(workDirectory)) Directory.Delete(workDirectory, true); }
            catch (Exception ex)
            {
                DiagnosticLogService.Instance.Warning("UpgradeBackup", "LocalWorkCleanupFailed",
                    "Could not remove the temporary plaintext backup work directory.", ex, _session.Profile.Name);
            }
        }
    }

    internal static string NormalizeDatabaseType(string databaseType)
    {
        var normalized = (databaseType ?? string.Empty).Trim().ToLowerInvariant();
        return normalized is "mongo" or "postgres" or "redis"
            ? normalized
            : throw new ArgumentException("סוג מסד הנתונים אינו נתמך לגיבוי אוטומטי.", nameof(databaseType));
    }

    internal static void ValidatePassword(string password)
    {
        if (string.IsNullOrWhiteSpace(password) || password.Length < 12)
            throw new ArgumentException("סיסמת הגיבוי חייבת להכיל לפחות 12 תווים.", nameof(password));
    }

    internal static async Task ValidateGzipAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path) || new FileInfo(path).Length < 20)
            throw new InvalidDataException("ארכיון הגיבוי חסר או קצר מדי.");
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
        await using var gzip = new GZipStream(input, CompressionMode.Decompress, false);
        var buffer = new byte[131072];
        while (await gzip.ReadAsync(buffer, cancellationToken) > 0) { }
    }

    private static async Task EncryptAndVerifyAsync(string plainPath, string encryptedPath, string password,
        CancellationToken cancellationToken)
    {
        var expectedHash = await HashFileAsync(plainPath, cancellationToken);
        await BackupEncryptionService.EncryptFileAsync(plainPath, encryptedPath, password, cancellationToken);
        var verifyPath = encryptedPath + ".verify-" + Guid.NewGuid().ToString("N");
        try
        {
            await BackupEncryptionService.DecryptFileAsync(encryptedPath, verifyPath, password, cancellationToken);
            var actualHash = await HashFileAsync(verifyPath, cancellationToken);
            if (!CryptographicOperations.FixedTimeEquals(expectedHash, actualHash))
                throw new CryptographicException("אימות הגיבוי המוצפן נכשל: התוכן שפוענח אינו זהה למקור.");
            CryptographicOperations.ZeroMemory(actualHash);
        }
        catch
        {
            try { if (File.Exists(encryptedPath)) File.Delete(encryptedPath); } catch { }
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(expectedHash);
            try { if (File.Exists(verifyPath)) File.Delete(verifyPath); } catch { }
        }
    }

    private static async Task<byte[]> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
        return await SHA256.HashDataAsync(stream, cancellationToken);
    }
}
