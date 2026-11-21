namespace NodeBB.SSHManager.Services;

public static class RemotePathSafety
{
    public static void ValidateNodeBbInstallationPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            !path.StartsWith("/", StringComparison.Ordinal) ||
            path.TrimEnd('/').Length == 0 ||
            path.Contains('\\') ||
            path.Any(char.IsControl) ||
            path.Split('/').Any(segment => segment is "." or ".."))
        {
            throw new ArgumentException(
                "נתיב התקנת NodeBB חייב להיות נתיב מוחלט ובטוח במערכת Linux.",
                nameof(path));
        }
    }
}
