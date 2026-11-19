using System.Text.RegularExpressions;

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

    private readonly SshServerSession _session;
    public RestrictedNodeBbCliService(SshServerSession session) => _session = session;

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

    public Task<(int ExitCode, string Output, string Error)> InstallAsync(string target, CancellationToken ct = default)
    {
        target = ValidateInstallTarget(target);
        return RunAsync("install " + target, ct);
    }
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
