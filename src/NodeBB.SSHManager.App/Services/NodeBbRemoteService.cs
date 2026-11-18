namespace NodeBB.SSHManager.Services;

public sealed record NodeBbInstallation(string Path, string Version);

public sealed class NodeBbRemoteService
{
    private readonly SshServerSession _session;
    public NodeBbRemoteService(SshServerSession session) => _session = session;

    public async Task<IReadOnlyList<NodeBbInstallation>> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        const string script = """
for root in /var/www /opt /srv /home /usr/local /var/lib "$HOME"; do
  [ -d "$root" ] || continue
  find "$root" -maxdepth 5 -type f -name package.json -print 2>/dev/null
done | while IFS= read -r f; do
  grep -Eq '"name"[[:space:]]*:[[:space:]]*"nodebb"' "$f" || continue
  d="$(dirname "$f")"
  v="$(sed -n 's/.*"version"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$f" | head -n 1)"
  printf '%s|%s\n' "$d" "$v"
done | sort -u | head -n 40
""";
        DiagnosticLogService.Instance.Info("NodeBB", "DiscoveryStarted", "NodeBB discovery started.", _session.Profile.Name);
        var result = await _session.ExecuteAsync(script, cancellationToken);
        if (result.ExitCode != 0 && string.IsNullOrWhiteSpace(result.Output))
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.Error) ? "סריקת NodeBB נכשלה." : result.Error);

        var installations = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split('|', 2))
            .Where(parts => parts.Length > 0 && parts[0].StartsWith('/'))
            .Select(parts => new NodeBbInstallation(parts[0], parts.Length > 1 ? parts[1] : "לא ידועה"))
            .DistinctBy(x => x.Path)
            .ToArray();
        DiagnosticLogService.Instance.Info("NodeBB", "DiscoveryCompleted", $"NodeBB discovery completed; {installations.Length} installation(s) found.", _session.Profile.Name);
        return installations;
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
