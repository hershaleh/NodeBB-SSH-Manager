namespace NodeBB.SSHManager.Services;

/// <summary>
/// Validates POSIX paths received from or sent to an SFTP server.
/// This is path hygiene, not a sandbox: the user may intentionally browse any
/// directory permitted by the SSH account.
/// </summary>
public static class SftpPathSafety
{
    public static string ValidateName(string name)
    {
        if (string.IsNullOrEmpty(name) ||
            name is "." or ".." ||
            name.Contains('/') ||
            name.Contains('\0') ||
            name.Any(char.IsControl))
        {
            throw new ArgumentException("שם פריט מרוחק אינו תקין.", nameof(name));
        }

        return name;
    }

    public static string NormalizeAbsolutePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path[0] != '/' || path.Contains('\0') ||
            path.Any(char.IsControl))
        {
            throw new ArgumentException("נתיב SFTP חייב להיות נתיב מוחלט ותקין.", nameof(path));
        }

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => segment is "." or ".."))
            throw new ArgumentException("הנתיב מכיל רכיב ניווט שאינו מותר.", nameof(path));

        return segments.Length == 0 ? "/" : "/" + string.Join('/', segments);
    }

    public static string Combine(string directory, string name)
    {
        var safeDirectory = NormalizeAbsolutePath(directory);
        var safeName = ValidateName(name);
        return safeDirectory == "/" ? "/" + safeName : safeDirectory + "/" + safeName;
    }
}
