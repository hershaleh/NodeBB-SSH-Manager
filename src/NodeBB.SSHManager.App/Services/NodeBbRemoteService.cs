namespace NodeBB.SSHManager.Services;

public sealed record NodeBbInstallation(string Path, string Version);

public sealed class NodeBbRemoteService
{
    private readonly SshServerSession _session;
    public NodeBbRemoteService(SshServerSession session) => _session = session;

    public async Task<IReadOnlyList<NodeBbInstallation>> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        const string script = """
for root in /var/www /opt /srv /home; do
  [ -d "$root" ] || continue
  find "$root" -maxdepth 5 -type f -name package.json -print 2>/dev/null
done | while IFS= read -r f; do
  grep -Eq '"name"[[:space:]]*:[[:space:]]*"nodebb"' "$f" || continue
  d="$(dirname "$f")"
  v="$(sed -n 's/.*"version"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$f" | head -n 1)"
  printf '%s|%s\n' "$d" "$v"
done | sort -u | head -n 40
""";
        var result = await _session.ExecuteAsync(script, cancellationToken);
        if (result.ExitCode != 0 && string.IsNullOrWhiteSpace(result.Output))
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.Error) ? "סריקת NodeBB נכשלה." : result.Error);

        return result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split('|', 2))
            .Where(parts => parts.Length > 0 && parts[0].StartsWith('/'))
            .Select(parts => new NodeBbInstallation(parts[0], parts.Length > 1 ? parts[1] : "לא ידועה"))
            .DistinctBy(x => x.Path)
            .ToArray();
    }

    public async Task<(int ExitCode, string Output, string Error)> RunLifecycleAsync(
        NodeBbInstallation installation, string action, CancellationToken cancellationToken = default)
    {
        if (action is not ("start" or "stop" or "restart" or "build"))
            throw new ArgumentOutOfRangeException(nameof(action));
        var path = ShellQuote(installation.Path);
        return await _session.ExecuteAsync($"cd {path} && ./nodebb {action}", cancellationToken);
    }

    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''") + "'";
}
