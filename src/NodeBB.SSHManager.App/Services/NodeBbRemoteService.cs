using System.Text.Json;
using Renci.SshNet.Sftp;

namespace NodeBB.SSHManager.Services;

public sealed record NodeBbInstallation(string Path, string Version);

public sealed class NodeBbRemoteService
{
    private const int MaxDepth = 3;
    private const int MaxDirectories = 200;
    private const int MaxPackageFiles = 1000;
    private const long MaxPackageJsonBytes = 2 * 1024 * 1024;
    private static readonly string[] CommonRoots =
        ["/home", "/var/www", "/opt", "/srv", "/usr/local"];

    private readonly SshServerSession _session;
    public NodeBbRemoteService(SshServerSession session) => _session = session;

    public async Task<IReadOnlyList<NodeBbInstallation>> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        DiagnosticLogService.Instance.Info("NodeBB", "DiscoveryStarted",
            "Bounded NodeBB discovery started using SFTP; filesystem root recursion is disabled.", _session.Profile.Name);

        using var client = _session.CreateSftpClient();
        await Task.Run(client.Connect, cancellationToken);
        try
        {
            return await Task.Run(() => DiscoverWithSftp(client, cancellationToken), cancellationToken);
        }
        finally
        {
            if (client.IsConnected) client.Disconnect();
        }
    }

    private IReadOnlyList<NodeBbInstallation> DiscoverWithSftp(
        Renci.SshNet.SftpClient client, CancellationToken cancellationToken)
    {
        var installations = new Dictionary<string, NodeBbInstallation>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<(string Path, int Depth)>();
        var roots = new List<string>();

        // Use the SFTP account's starting directory first. Do not recursively enumerate
        // "/" on shared/restricted hosts: it can close the provider's SFTP channel.
        try
        {
            var home = NormalizeRemotePath(client.WorkingDirectory);
            if (!string.IsNullOrWhiteSpace(home)) roots.Add(home);
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Instance.Warning("NodeBB", "WorkingDirectoryUnavailable",
                "The SFTP server did not provide a working directory; trying standard application roots.", ex, _session.Profile.Name);
        }

        roots.AddRange(CommonRoots);
        foreach (var root in roots.Distinct(StringComparer.Ordinal))
            queue.Enqueue((root, 0));

        var directoriesVisited = 0;
        var packageFilesVisited = 0;
        var channelFailure = (Exception?)null;

        while (queue.Count > 0 && directoriesVisited < MaxDirectories && packageFilesVisited < MaxPackageFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!client.IsConnected)
            {
                channelFailure = new IOException("שרת ה־SFTP סגר את ערוץ הקבצים במהלך הסריקה.");
                break;
            }

            var (path, depth) = queue.Dequeue();
            if (!visited.Add(path)) continue;
            directoriesVisited++;

            ISftpFile[] entries;
            try
            {
                entries = client.ListDirectory(path)
                    .Where(x => x.Name is not ("." or ".."))
                    .ToArray();
            }
            catch (Exception ex)
            {
                DiagnosticLogService.Instance.Warning("NodeBB", "DiscoveryDirectorySkipped",
                    $"Could not list SFTP directory {path}; continuing with other roots.", ex, _session.Profile.Name);
                if (!client.IsConnected)
                {
                    channelFailure = ex;
                    break;
                }
                continue;
            }

            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var childPath = NormalizeRemotePath(entry.FullName);
                if (entry.IsDirectory)
                {
                    if (depth < MaxDepth && !IsLikelyNoiseDirectory(entry.Name))
                        queue.Enqueue((childPath, depth + 1));
                    continue;
                }

                if (!string.Equals(entry.Name, "package.json", StringComparison.OrdinalIgnoreCase))
                    continue;

                packageFilesVisited++;
                if (entry.Attributes.Size < 2 || entry.Attributes.Size > MaxPackageJsonBytes)
                    continue;

                try
                {
                    using var stream = client.OpenRead(childPath);
                    using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 32 });
                    var manifest = document.RootElement;
                    if (!manifest.TryGetProperty("name", out var nameElement)
                        || !string.Equals(nameElement.GetString(), "nodebb", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var version = manifest.TryGetProperty("version", out var versionElement)
                        ? versionElement.GetString() ?? "לא ידועה"
                        : "לא ידועה";
                    var installPath = NormalizeRemotePath(GetParentPath(childPath));
                    installations[installPath] = new NodeBbInstallation(installPath, version);
                }
                catch (Exception ex)
                {
                    DiagnosticLogService.Instance.Warning("NodeBB", "PackageManifestSkipped",
                        $"Could not read or parse {childPath}; continuing discovery.", ex, _session.Profile.Name);
                    if (!client.IsConnected)
                    {
                        channelFailure = ex;
                        break;
                    }
                }
            }

            if (channelFailure is not null) break;
        }

        if (channelFailure is not null)
        {
            DiagnosticLogService.Instance.Error("NodeBB", "DiscoveryChannelClosed", channelFailure, _session.Profile.Name);
            throw new IOException(
                "שרת ה־SFTP סגר את ערוץ הקבצים בזמן סריקת הנתיבים. הסריקה צומצמה ואינה עוברת עוד על כל מערכת הקבצים. אפשר לנסות שוב או להזין נתיב התקנה ידני.",
                channelFailure);
        }

        var result = installations.Values.OrderBy(x => x.Path, StringComparer.Ordinal).ToArray();
        DiagnosticLogService.Instance.Info("NodeBB", "DiscoveryCompleted",
            $"Bounded SFTP discovery completed; {result.Length} installation(s) found after visiting {directoriesVisited} directories and {packageFilesVisited} package manifests.",
            _session.Profile.Name);
        return result;
    }

    private static bool IsLikelyNoiseDirectory(string name) =>
        name is ".git" or "node_modules" or ".cache" or ".npm" or ".local" or "proc" or "sys" or "dev" or "run" or "snap" or "tmp";

    private static string GetParentPath(string path)
    {
        var normalized = NormalizeRemotePath(path).TrimEnd('/');
        var index = normalized.LastIndexOf('/');
        return index <= 0 ? "/" : normalized[..index];
    }

    private static string NormalizeRemotePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "/";
        var parts = new List<string>();
        foreach (var part in path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == "." || part.Length == 0) continue;
            if (part == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); continue; }
            parts.Add(part);
        }
        return "/" + string.Join("/", parts);
    }

    public async Task<(int ExitCode, string Output, string Error)> RunLifecycleAsync(
        NodeBbInstallation installation, string action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(installation);
        if (action is not ("start" or "stop" or "restart" or "build"))
            throw new ArgumentOutOfRangeException(nameof(action));
        RemotePathSafety.ValidateNodeBbInstallationPath(installation.Path);

        DiagnosticLogService.Instance.Info("NodeBB", "LifecycleStarted", $"NodeBB {action} requested for {installation.Path}.", _session.Profile.Name);
        if (action == "build")
        {
            var buildPath = ShellQuote(installation.Path);
            var buildResult = await _session.ExecuteAsync($"cd {buildPath} && ./nodebb build", cancellationToken);
            if (buildResult.ExitCode == 0)
                DiagnosticLogService.Instance.Info("NodeBB", "LifecycleCompleted", "NodeBB build completed.", _session.Profile.Name);
            else
                DiagnosticLogService.Instance.Error("NodeBB", "LifecycleFailed", $"NodeBB build failed with exit code {buildResult.ExitCode}: {buildResult.Error}", _session.Profile.Name);
            return buildResult;
        }

        var runtimeService = new NodeBbRuntimeService(_session);
        var runtime = await runtimeService.DetectAsync(installation, cancellationToken);
        var result = await runtimeService.ExecuteLifecycleAsync(installation, runtime, action, cancellationToken);
        var described = (result.ExitCode, $"מנהל הפעלה שזוהה: {runtime.Manager} ({runtime.Identifier}){Environment.NewLine}{result.Output}", result.Error);
        return await VerifyLifecycleOutcomeAsync(
            action,
            described,
            () => runtimeService.VerifyHttpStoppedAsync(installation, cancellationToken),
            () => runtimeService.VerifyHttpHealthAsync(installation, cancellationToken));
    }

    internal static async Task<(int ExitCode, string Output, string Error)> VerifyLifecycleOutcomeAsync(
        string action,
        (int ExitCode, string Output, string Error) commandResult,
        Func<Task<bool>> verifyStopped,
        Func<Task<bool>> verifyHealthy)
    {
        ArgumentNullException.ThrowIfNull(verifyStopped);
        ArgumentNullException.ThrowIfNull(verifyHealthy);
        if (commandResult.ExitCode != 0 || action == "build")
            return commandResult;

        try
        {
            if (action == "stop")
            {
                if (await verifyStopped())
                    return (commandResult.ExitCode, commandResult.Output + Environment.NewLine + "אומת: אין תהליך שמאזין ביציאת NodeBB.", commandResult.Error);
                return (1, commandResult.Output, "פקודת העצירה הסתיימה בהצלחה, אך עדיין לא ניתן לאמת שיציאת NodeBB הפסיקה להאזין. בדוק את מצב השירות לפני ניסיון נוסף.");
            }

            if (action is "start" or "restart")
            {
                if (await verifyHealthy())
                    return (commandResult.ExitCode, commandResult.Output + Environment.NewLine + "אומת: ממשק HTTP של NodeBB מגיב באופן תקין.", commandResult.Error);
                return (1, commandResult.Output, "פקודת ההפעלה הסתיימה, אך NodeBB לא סיפק תגובת API תקינה. בדוק את יומני NodeBB ואת מצב מסד הנתונים.");
            }

            throw new ArgumentOutOfRangeException(nameof(action));
        }
        catch (Exception ex)
        {
            return (1, commandResult.Output, $"הפקודה הסתיימה, אך אימות מצב NodeBB נכשל: {ex.Message}");
        }
    }

    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''") + "'";
}
