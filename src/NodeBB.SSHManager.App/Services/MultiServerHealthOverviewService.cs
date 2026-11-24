using System.Text;
using NodeBB.SSHManager.Models;

namespace NodeBB.SSHManager.Services;

public sealed record ServerHealthOverviewRow(
    string ServerName,
    string Endpoint,
    string ConnectionState,
    string Cpu,
    string Memory,
    string Swap,
    string Disk,
    string Network,
    string Alerts,
    string CollectedAt,
    bool IsReachable);

public sealed class MultiServerHealthOverviewService
{
    private readonly MonitoringSettingsStore _settingsStore;

    public MultiServerHealthOverviewService(MonitoringSettingsStore? settingsStore = null) =>
        _settingsStore = settingsStore ?? new MonitoringSettingsStore();

    public async Task<IReadOnlyList<ServerHealthOverviewRow>> CollectAsync(
        IEnumerable<ServerProfile> profiles,
        IEnumerable<SshServerSession> sessions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(sessions);
        var connected = sessions.Where(session => session.IsConnected)
            .GroupBy(session => session.Profile.Id)
            .ToDictionary(group => group.Key, group => group.First());
        using var throttle = new SemaphoreSlim(3, 3);
        var tasks = profiles.Select(async profile =>
        {
            if (!connected.TryGetValue(profile.Id, out var session))
                return Unavailable(profile, "לא מחובר");

            await throttle.WaitAsync(cancellationToken);
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(25));
                var metrics = await new RemoteMonitoringService(session).CollectAsync(timeout.Token);
                var thresholds = _settingsStore.Load(profile.Id);
                var alerts = ServerHealthAssessment.Evaluate(metrics.LoadAverage, metrics.Disk,
                    metrics.MemoryUsagePercent, metrics.CpuCount, metrics.SwapUsagePercent,
                    metrics.DiskInodes, metrics.CpuUsagePercent, thresholds);
                return new ServerHealthOverviewRow(
                    profile.Name, profile.HostDisplay, "נגיש",
                    FormatPercent(metrics.CpuUsagePercent), FormatPercent(metrics.MemoryUsagePercent),
                    FormatPercent(metrics.SwapUsagePercent), metrics.Disk,
                    metrics.NetworkThroughput, alerts.Count == 0 ? "תקין" : string.Join(" | ", alerts),
                    metrics.CollectedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), true);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return Unavailable(profile, "פג זמן ההמתנה");
            }
            catch (Exception ex)
            {
                DiagnosticLogService.Instance.Warning("MultiServerMonitoring", "ServerCollectionFailed",
                    "A server could not be included in the health overview.", ex, profile.Name);
                return Unavailable(profile, "איסוף נכשל: " + SshConnectionService.GetFriendlyErrorMessage(ex));
            }
            finally { throttle.Release(); }
        }).ToArray();

        return await Task.WhenAll(tasks);
    }

    public static string ExportCsv(IEnumerable<ServerHealthOverviewRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var builder = new StringBuilder();
        builder.AppendLine("Server,Endpoint,ConnectionState,CPU,Memory,Swap,Disk,Network,Alerts,CollectedAt");
        foreach (var row in rows)
        {
            builder.Append(Csv(row.ServerName)).Append(',')
                .Append(Csv(row.Endpoint)).Append(',')
                .Append(Csv(row.ConnectionState)).Append(',')
                .Append(Csv(row.Cpu)).Append(',')
                .Append(Csv(row.Memory)).Append(',')
                .Append(Csv(row.Swap)).Append(',')
                .Append(Csv(row.Disk)).Append(',')
                .Append(Csv(row.Network)).Append(',')
                .Append(Csv(row.Alerts)).Append(',')
                .Append(Csv(row.CollectedAt)).AppendLine();
        }
        return builder.ToString();
    }

    private static string Csv(string? value)
    {
        value ??= string.Empty;
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
            value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    private static string FormatPercent(int? value) => value.HasValue ? value.Value + "%" : "לא זמין";

    private static ServerHealthOverviewRow Unavailable(ServerProfile profile, string state) =>
        new(profile.Name, profile.HostDisplay, state, "—", "—", "—", "—", "—", "—", "—", false);
}
