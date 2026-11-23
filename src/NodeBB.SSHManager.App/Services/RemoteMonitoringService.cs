namespace NodeBB.SSHManager.Services;

public sealed record RemoteServerMetrics(
    string Uptime,
    string LoadAverage,
    string Memory,
    int? MemoryUsagePercent,
    string Disk,
    string TopProcesses,
    int CpuCount,
    DateTimeOffset CollectedAt,
    string Swap,
    int? SwapUsagePercent,
    string DiskInodes,
    string TopMemoryProcesses,
    int? CpuUsagePercent,
    string NetworkThroughput);

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
printf 'CPU_COUNT|'
getconf _NPROCESSORS_ONLN 2>/dev/null || nproc 2>/dev/null || echo 1
CPU_BEFORE=$(awk '/^cpu / {print $2+$3+$4+$5+$6+$7+$8, $5+$6}' /proc/stat 2>/dev/null)
NET_BEFORE=$(awk 'NR>2 {name=$1; sub(/:$/, "", name); if(name!="lo"){rx+=$2; tx+=$10}} END {printf "%.0f %.0f", rx, tx}' /proc/net/dev 2>/dev/null)
sleep 1
CPU_AFTER=$(awk '/^cpu / {print $2+$3+$4+$5+$6+$7+$8, $5+$6}' /proc/stat 2>/dev/null)
NET_AFTER=$(awk 'NR>2 {name=$1; sub(/:$/, "", name); if(name!="lo"){rx+=$2; tx+=$10}} END {printf "%.0f %.0f", rx, tx}' /proc/net/dev 2>/dev/null)
printf 'CPU_USAGE_PERCENT|'
awk -v before="$CPU_BEFORE" -v after="$CPU_AFTER" 'BEGIN {split(before,b); split(after,a); total=a[1]-b[1]; idle=a[2]-b[2]; if(total>0 && idle>=0 && idle<=total) printf "%.0f\n", 100*(total-idle)/total; else print "לא זמין"}'
printf 'NETWORK|'
awk -v before="$NET_BEFORE" -v after="$NET_AFTER" 'function human(v) {if(v>=1073741824) return sprintf("%.1f GiB",v/1073741824); if(v>=1048576) return sprintf("%.1f MiB",v/1048576); if(v>=1024) return sprintf("%.1f KiB",v/1024); return sprintf("%.0f B",v)} BEGIN {split(before,b); split(after,a); rx=a[1]-b[1]; tx=a[2]-b[2]; if(rx<0) rx=0; if(tx<0) tx=0; if(after=="" || before=="") print "לא זמין"; else printf "RX %s/s; TX %s/s\n",human(rx),human(tx)}'
printf 'MEMORY|'
if command -v free >/dev/null 2>&1; then free -h | awk 'NR==2 {print $3 " / " $2 " בשימוש; זמין " $7}'; else echo 'לא זמין'; fi
printf 'MEMORY_PERCENT|'
if command -v free >/dev/null 2>&1; then free -b | awk 'NR==2 && $2>0 {printf "%.0f\n", 100*$3/$2}'; else echo 'לא זמין'; fi
printf 'SWAP|'
if command -v free >/dev/null 2>&1; then free -h | awk '$1=="Swap:" {print $3 " / " $2 " בשימוש"}'; else echo 'לא זמין'; fi
printf 'SWAP_PERCENT|'
if command -v free >/dev/null 2>&1; then free -b | awk '$1=="Swap:" && $2>0 {printf "%.0f\n", 100*$3/$2}'; else echo 'לא זמין'; fi
printf 'DISK|'
if command -v df >/dev/null 2>&1; then df -hP 2>/dev/null | awk 'NR>1 && $6 !~ /tmpfs|devtmpfs/ {printf "%s: %s/%s (%s); ", $6, $3, $2, $5}' ; else echo 'לא זמין'; fi
printf '\nDISK_INODES|'
if command -v df >/dev/null 2>&1; then df -iP 2>/dev/null | awk 'NR>1 && $6 !~ /tmpfs|devtmpfs/ {printf "%s: (%s); ", $6, $5}'; else echo 'לא זמין'; fi
printf '\nPROCESSES|'
if command -v ps >/dev/null 2>&1; then ps -eo pid,comm,%cpu,%mem --sort=-%cpu 2>/dev/null | head -n 8 | tr '\n' ';'; else echo 'לא זמין'; fi
printf '\nMEMORY_PROCESSES|'
if command -v ps >/dev/null 2>&1; then ps -eo pid,comm,%cpu,%mem --sort=-%mem 2>/dev/null | head -n 8 | tr '\n' ';'; else echo 'לא זמין'; fi
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

        var metrics = RemoteMonitoringOutputParser.Parse(result.Output);
        DiagnosticLogService.Instance.Info("Monitoring", "CollectionCompleted", "Remote server metrics collection completed.", _session.Profile.Name);
        return metrics;
    }
}
