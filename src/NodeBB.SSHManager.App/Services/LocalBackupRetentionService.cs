namespace NodeBB.SSHManager.Services;

public static class LocalBackupRetentionService
{
    public static int ApplyRetention(string directory, string filePrefix, int keepCount)
    {
        if (string.IsNullOrWhiteSpace(directory))
            throw new ArgumentException("יש לבחור תיקיית גיבויים.", nameof(directory));
        if (string.IsNullOrWhiteSpace(filePrefix) || filePrefix.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            filePrefix.Contains('*') || filePrefix.Contains('?'))
            throw new ArgumentException("קידומת קובצי הגיבוי אינה תקינה.", nameof(filePrefix));
        if (keepCount < 0)
            throw new ArgumentOutOfRangeException(nameof(keepCount), "מספר הגיבויים לשמירה אינו יכול להיות שלילי.");
        if (keepCount == 0 || !Directory.Exists(directory)) return 0;

        var files = Directory.EnumerateFiles(directory, filePrefix + "-*", SearchOption.TopDirectoryOnly)
            .Where(path => path.EndsWith(".nbbbackup", StringComparison.OrdinalIgnoreCase) ||
                           path.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
            .Select(path => new FileInfo(path))
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