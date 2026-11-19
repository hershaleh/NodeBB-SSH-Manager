using System.Text.Json;
using Renci.SshNet.Sftp;

namespace NodeBB.SSHManager.Services;

public sealed record NodeBbInstallation(string Path, string Version);

public sealed class NodeBbRemoteService
{
    private const int MaxDepth = 5;
    private const int MaxDirectories = 2500;
    private const int MaxPackageFiles = 10000;
    private const long MaxPackageJsonBytes = 2 * 1024 * 1024;
    private static readonly string[] CommonRoots =
        ["/var/www", "/opt", "/srv", "/home", "/usr/local", "/var/lib"];

    private readonly SshServerSession _session;
    public NodeBbRemoteService(SshServerSession session) => _session = session;

    public async Task<IReadOnlyList<NodeBbInstallation>> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        DiagnosticLogService.Instance.Info("NodeBB", "DiscoveryStarted",
            "NodeBB discovery started using SFTP directory and file access.", _session.Profile.Name);

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
        var roots = new HashSet<string>(CommonRoots, StringComparer.Ordinal);

        try
        {
            var workingDirectory = NormalizeRemotePath(client.WorkingDirectory);
            if (!string.IsNullOrWhiteSpace(workingDirectory)) roots.Add(workingDirectory);
        }
        catch { /* Some restricted SFTP servers do not expose a working directory. */ }

        // Start at the filesystem root as well: this finds non-standard installation
        // locations while remaining bounded by depth and directory-count limits.
        roots.Add("/");
        foreach (var root in roots) queue.Enqueue((root, 0));

        var directoriesVisited = 0;
        var packageFilesVisited = 0;
        while (queue.Count > 0 && directoriesVisited < MaxDirectories && packageFilesVisited < MaxPackageFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (path, depth) = queue.Dequeue();
            if (!visited.Add(path)) continue;
            directoriesVisited++;

            IEnumerable<ISftpFile> entries;
            try
            {
                entries = client.ListDirectory(path).Where(x => x.Name is not ("." or "..")).ToArray();
            }
            catch
            {
                // Permission-denied and unsupported directories are normal on shared hosts.
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
                    var rootElement = document.RootElement;
                    if (!rootElement.TryGetProperty("name", out var nameElement)
                        || !string.Equals(nameElement.GetString(), "nodebb", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var version = rootElement.TryGetProperty("version", out var versionElement)
                        ? versionElement.GetString() ?? "לא ידועה"
                        : "לא ידועה";
                    var installPath = NormalizeRemotePath(GetParentPath(childPath));
                    installations[installPath] = new NodeBbInstallation(installPath, version);
                }
                catch
                {
                    // Ignore unreadable, malformed, or unrelated package manifests.
                }
            }
        }

        var result = installations.Values.OrderBy(x => x.Path, StringComparer.Ordinal).ToArray();
        DiagnosticLogService.Instance.Info("NodeBB", "DiscoveryCompleted",
            $"SFTP discovery completed; {result.Length} installation(s) found after visiting {directoriesVisited} directories and {packageFilesVisited} package manifests.",
            _session.Profile.Name);
        return result;
    }

    private static bool IsLikelyNoiseDirectory(string name) =>
        name is ".git" or "node_modules" or ".cache" or ".npm" or ".local" or "proc" or "sys" or "dev" or "run" or "snap";

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
        if (action is not ("start" or "stop" or "restart" or "build"))
            throw new ArgumentOutOfRangeException(nameof(action));

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
        return (result.ExitCode, $"מנהל הפעלה שזוהה: {runtime.Manager} ({runtime.Identifier}){Environment.NewLine}{result.Output}", result.Error);
    }

    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''") + "'";
}
