namespace NodeBB.SSHManager.Services;

public static class LocalBackupRetentionService
{
    public static string BuildFilePrefix(string serverName, Guid profileId, RemoteBackupKind kind)
    {
        if (profileId == Guid.Empty)
            throw new ArgumentException("מזהה פרופיל שרת תקין נדרש לצורך הפרדת גיבויים.", nameof(profileId));

        var safeName = BuildSafeServerName(serverName);
        var kindToken = kind switch
        {
            RemoteBackupKind.FullInstallation => "full",
            RemoteBackupKind.ConfigurationAndUploads => "config-uploads",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };

        // A stable profile identifier isolates retention even when display names collide.
        return "NodeBB-" + safeName + "-" + profileId.ToString("N") + "-" + kindToken;
    }

    public static string BuildDatabaseFilePrefix(string serverName, Guid profileId, string databaseType)
    {
        if (profileId == Guid.Empty)
            throw new ArgumentException("מזהה פרופיל שרת תקין נדרש לצורך הפרדת גיבויים.", nameof(profileId));
        var type = (databaseType ?? string.Empty).Trim().ToLowerInvariant();
        if (type is not ("mongo" or "postgres" or "redis"))
            throw new ArgumentOutOfRangeException(nameof(databaseType), "סוג מסד הנתונים אינו נתמך לגיבוי מתוזמן.");
        return "NodeBB-" + BuildSafeServerName(serverName) + "-" + profileId.ToString("N") + "-database-" + type;
    }

    private static string BuildSafeServerName(string? serverName)
    {
        var safeName = string.Concat((serverName ?? string.Empty)
            .Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-'))
            .Trim('-', '_');
        if (string.IsNullOrWhiteSpace(safeName)) safeName = "server";
        if (safeName.Length > 48) safeName = safeName[..48].Trim('-', '_');
        return string.IsNullOrWhiteSpace(safeName) ? "server" : safeName;
    }

    public static int ApplyRetention(string directory, string filePrefix, int keepCount)
    {
        if (string.IsNullOrWhiteSpace(directory))
            throw new ArgumentException("יש לבחור תיקיית גיבויים.", nameof(directory));
        if (string.IsNullOrWhiteSpace(filePrefix) ||
            filePrefix.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            filePrefix.Contains('*') || filePrefix.Contains('?') ||
            filePrefix.Contains(Path.DirectorySeparatorChar) ||
            filePrefix.Contains(Path.AltDirectorySeparatorChar) ||
            !string.Equals(Path.GetFileName(filePrefix), filePrefix, StringComparison.Ordinal))
            throw new ArgumentException("קידומת קובצי הגיבוי אינה תקינה.", nameof(filePrefix));
        if (keepCount < 0)
            throw new ArgumentOutOfRangeException(nameof(keepCount), "מספר הגיבויים לשמירה אינו יכול להיות שלילי.");
        if (keepCount == 0 || !Directory.Exists(directory)) return 0;

        var fullDirectory = Path.GetFullPath(directory);
        var files = Directory.EnumerateFiles(fullDirectory, filePrefix + "-*", SearchOption.TopDirectoryOnly)
            .Where(path => path.EndsWith(".nbbbackup", StringComparison.OrdinalIgnoreCase) ||
                           path.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
            .Select(path => new FileInfo(path))
            .Where(file => (file.Attributes & FileAttributes.Directory) == 0)
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .ThenByDescending(file => file.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var deleted = 0;
        foreach (var file in files.Skip(keepCount))
        {
            try { file.Delete(); deleted++; }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return deleted;
    }
}