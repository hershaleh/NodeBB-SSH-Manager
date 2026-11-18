namespace NodeBB.SSHManager.Services;

public sealed record RemoteServerMetrics(
    string Uptime,
    string LoadAverage,
    string Memory,
    string Disk,
    string TopProcesses,
    DateTimeOffset CollectedAt);

public sealed class RemoteMonitoringService
{
    private readonly SshServerSession _session;

    public RemoteMonitoringService(SshServerSession session) => _session = session;

    public async Task<RemoteServerMetrics> CollectAsync(CancellationToken cancellationToken = default)
    {
        const string script = """
set +e
printf 'UPTIME|'
uptime -p 2>/dev/null || uptime 2>/dev/null
printf 'LOAD|'
cat /proc/loadavg 2>/dev/null || echo 'לא זמין'
printf 'MEMORY|'
if command -v free >/dev/null 2>&1; then free -h | awk 'NR==2 {print $3 " / " $2 " בשימוש; זמין " $7}'; else echo 'לא זמין'; fi
printf 'DISK|'
if command -v df >/dev/null 2>&1; then df -hP 2>/dev/null | awk 'NR>1 && $6 !~ /tmpfs|devtmpfs/ {printf "%s: %s/%s (%s); ", $6, $3, $2, $5}' ; else echo 'לא זמין'; fi
printf '\nPROCESSES|'
if command -v ps >/dev/null 2>&1; then ps -eo pid,comm,%cpu,%mem --sort=-%cpu 2>/dev/null | head -n 8 | tr '\n' ';'; else echo 'לא זמין'; fi
printf '\n'
""";

        DiagnosticLogService.Instance.Info("Monitoring", "CollectionStarted", "Remote server metrics collection started.", _session.Profile.Name);
        var result = await _session.ExecuteAsync(script, cancellationToken);
        if (result.ExitCode != 0 && string.IsNullOrWhiteSpace(result.Output))
        {
            var error = new InvalidOperationException(string.IsNullOrWhiteSpace(result.Error) ? "איסוף נתוני הניטור נכשל." : result.Error);
            DiagnosticLogService.Instance.Error("Monitoring", "CollectionFailed", error, _session.Profile.Name);
            throw error;
        }

        var values = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split('|', 2))
            .Where(parts => parts.Length == 2)
            .GroupBy(parts => parts[0], StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last()[1], StringComparer.OrdinalIgnoreCase);

        string Get(string key) => values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : "לא זמין";
        var metrics = new RemoteServerMetrics(Get("UPTIME"), Get("LOAD"), Get("MEMORY"), Get("DISK"), Get("PROCESSES"), DateTimeOffset.UtcNow);
        DiagnosticLogService.Instance.Info("Monitoring", "CollectionCompleted", "Remote server metrics collection completed.", _session.Profile.Name);
        return metrics;
    }
}
