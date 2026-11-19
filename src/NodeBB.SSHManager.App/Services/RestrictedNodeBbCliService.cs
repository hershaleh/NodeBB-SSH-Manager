using System.Text.RegularExpressions;
using System.IO.Compression;

namespace NodeBB.SSHManager.Services;

public sealed class RestrictedNodeBbCliService
{
    private static readonly Regex PackageTarget = new(
        @"^(?:@?[a-z0-9][a-z0-9._-]*(?:/[a-z0-9][a-z0-9._-]*)?)(?:@[a-z0-9._-]+)?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex GitHubTarget = new(
        @"^https://github\.com/[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+(?:#[A-Za-z0-9_./-]+)?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ZipName = new(
        @"^[A-Za-z0-9][A-Za-z0-9._-]{0,119}\.zip$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex PluginPackageName = new(
        @"(?<![A-Za-z0-9._/-])((?:@[A-Za-z0-9._-]+/)?(?:nodebb-(?:plugin|theme)-|(?:plugin|theme)-)[A-Za-z0-9._-]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private readonly SshServerSession _session;
    public RestrictedNodeBbCliService(SshServerSession session) => _session = session;

    public static IReadOnlyList<string> ExtractPluginPackageNames(string raw)
    {
        var found = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in (raw ?? string.Empty).Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            foreach (Match match in PluginPackageName.Matches(line))
                found.Add(match.Groups[1].Value.TrimEnd('.', ',', ':', ';'));
        return found.ToArray();
    }

    public Task<(int ExitCode, string Output, string Error)> HelpAsync(CancellationToken ct = default) => RunAsync("help", ct);
    public Task<(int ExitCode, string Output, string Error)> ListAsync(CancellationToken ct = default) => RunAsync("list", ct);
    public Task<(int ExitCode, string Output, string Error)> BackupAsync(CancellationToken ct = default) => RunAsync("backup", ct);
    public Task<(int ExitCode, string Output, string Error)> StatusAsync(CancellationToken ct = default) => RunAsync("status", ct);
    public Task<(int ExitCode, string Output, string Error)> BuildAsync(CancellationToken ct = default) => RunAsync("build", ct);
    public Task<(int ExitCode, string Output, string Error)> RestartAsync(CancellationToken ct = default) => RunAsync("restart", ct);
    public Task<(int ExitCode, string Output, string Error)> LogsAsync(int lines, CancellationToken ct = default)
    {
        if (lines is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(lines), "מספר שורות הלוג חייב להיות בין 1 ל־500.");
        return RunAsync($"logs {lines}", ct);
    }
    public Task<(int ExitCode, string Output, string Error)> ListZipsAsync(CancellationToken ct = default) => RunAsync("list-zips", ct);

    public async Task<(int ExitCode, string Output, string Error)> UploadZipAsync(
        string localPath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(localPath) || !File.Exists(localPath))
            throw new FileNotFoundException("לא נמצא קובץ ZIP שנבחר.", localPath);

        var info = new FileInfo(localPath);
        const long maxBytes = 25L * 1024 * 1024;
        if (info.Length <= 0 || info.Length > maxBytes)
            throw new InvalidDataException("גודל קובץ ZIP חייב להיות בין 1 בייט ל־25MB.");

        var fileName = ValidateZipName(Path.GetFileName(localPath));
        // Verify that the selected file is a readable ZIP before sending it.
        using (var archiveStream = File.OpenRead(localPath))
        using (var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read, leaveOpen: false))
        {
            if (archive.Entries.Count == 0)
                throw new InvalidDataException("קובץ ZIP אינו מכיל קבצים.");
        }

        DiagnosticLogService.Instance.Info("RestrictedCLI", "ZipUploadStarted",
            $"Uploading ZIP file ({info.Length} bytes) through the restricted SSH stdin channel.", _session.Profile.Name);
        await using var source = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var result = await _session.ExecuteWithInputAsync("upload-zip " + fileName, source, ct);
        if (result.ExitCode == 0)
            DiagnosticLogService.Instance.Info("RestrictedCLI", "ZipUploadCompleted",
                "ZIP upload completed through the restricted SSH stdin channel.", _session.Profile.Name);
        else
            DiagnosticLogService.Instance.Warning("RestrictedCLI", "ZipUploadFailed",
                $"ZIP upload failed with exit code {result.ExitCode}.", null, _session.Profile.Name);
        return result;
    }

    public Task<(int ExitCode, string Output, string Error)> InstallAsync(string target, CancellationToken ct = default)
    {
        target = ValidateInstallTarget(target);
        if (ZipName.IsMatch(target)) return RunAsync("install-zip " + target, ct);
        if (GitHubTarget.IsMatch(target)) return RunAsync("install-github " + target, ct);
        return RunAsync("install " + target, ct);
    }

    public Task<(int ExitCode, string Output, string Error)> InstallNpmAsync(string package, CancellationToken ct = default)
    { package = ValidatePackage(package); return RunAsync("install " + package, ct); }

    public Task<(int ExitCode, string Output, string Error)> InstallGitHubAsync(string url, CancellationToken ct = default)
    { url = ValidateGitHubTarget(url); return RunAsync("install-github " + url, ct); }

    public Task<(int ExitCode, string Output, string Error)> InstallZipAsync(string fileName, CancellationToken ct = default)
    { fileName = ValidateZipName(fileName); return RunAsync("install-zip " + fileName, ct); }

    public static string ValidateGitHubTarget(string? target)
    { target = target?.Trim() ?? ""; if (!GitHubTarget.IsMatch(target)) throw new ArgumentException("יש להזין כתובת HTTPS תקינה של GitHub בפורמט https://github.com/owner/repo או עם #branch."); return target; }
    public Task<(int ExitCode, string Output, string Error)> UpdateAsync(string package, CancellationToken ct = default)
    {
        package = ValidatePackage(package);
        return RunAsync("update " + package, ct);
    }
    public Task<(int ExitCode, string Output, string Error)> RemoveAsync(string package, CancellationToken ct = default)
    {
        package = ValidatePackage(package);
        return RunAsync("remove " + package, ct);
    }
    public Task<(int ExitCode, string Output, string Error)> ActivateAsync(string package, CancellationToken ct = default)
    {
        package = ValidatePackage(package);
        return RunAsync("activate " + package, ct);
    }
    public Task<(int ExitCode, string Output, string Error)> DisableAsync(string package, CancellationToken ct = default)
    {
        package = ValidatePackage(package);
        return RunAsync("disable " + package, ct);
    }
    public Task<(int ExitCode, string Output, string Error)> DeleteZipAsync(string fileName, CancellationToken ct = default)
    {
        fileName = ValidateZipName(fileName);
        return RunAsync("delete-zip " + fileName, ct);
    }

    public static string ValidateInstallTarget(string? target)
    {
        target = target?.Trim() ?? "";
        if (GitHubTarget.IsMatch(target)) return target;
        if (ZipName.IsMatch(target)) return target;
        return ValidatePackage(target);
    }

    public static string ValidatePackage(string? package)
    {
        package = package?.Trim() ?? "";
        if (!PackageTarget.IsMatch(package) || package.StartsWith("-", StringComparison.Ordinal))
            throw new ArgumentException("שם חבילה אינו תקין.", nameof(package));
        return package;
    }

    public static string ValidateZipName(string? fileName)
    {
        fileName = fileName?.Trim() ?? "";
        if (!ZipName.IsMatch(fileName) || fileName is "." or "..")
            throw new ArgumentException("שם קובץ ZIP אינו תקין.", nameof(fileName));
        return fileName;
    }

    private async Task<(int ExitCode, string Output, string Error)> RunAsync(string command, CancellationToken ct)
    {
        DiagnosticLogService.Instance.Info("RestrictedCLI", "CommandStarted", $"Restricted NodeBB command requested: {command.Split(' ')[0]}.", _session.Profile.Name);
        var result = await _session.ExecuteAsync(command, ct);
        if (result.ExitCode == 0)
            DiagnosticLogService.Instance.Info("RestrictedCLI", "CommandCompleted", $"Restricted NodeBB command completed: {command.Split(' ')[0]}.", _session.Profile.Name);
        else
            DiagnosticLogService.Instance.Warning("RestrictedCLI", "CommandFailed", $"Restricted NodeBB command failed: {command.Split(' ')[0]}, exit {result.ExitCode}.", null, _session.Profile.Name);
        return result;
    }
}
