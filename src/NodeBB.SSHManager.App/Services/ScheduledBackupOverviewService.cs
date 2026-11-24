using System.Text;
using NodeBB.SSHManager.Models;

namespace NodeBB.SSHManager.Services;

public sealed record ScheduledBackupOverviewRow(
    Guid ScheduleId, Guid ProfileId, string ServerName, string Endpoint,
    string InstallationPath, string BackupType, string Cadence, string Destination,
    string NextRunLocal, string LastRunLocal, string LastResult, string LastSummary,
    string Retention, bool IncludesDatabase, bool IsAttentionRequired);

public sealed class ScheduledBackupOverviewService
{
    private readonly ScheduledBackupStore _store;

    public ScheduledBackupOverviewService(ScheduledBackupStore? store = null) =>
        _store = store ?? new ScheduledBackupStore();

    public IReadOnlyList<ScheduledBackupOverviewRow> Collect(
        IEnumerable<ServerProfile> profiles, DateTime localNow)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        return BuildRows(profiles, _store.LoadAll(), localNow);
    }

    public static IReadOnlyList<ScheduledBackupOverviewRow> BuildRows(
        IEnumerable<ServerProfile> profiles,
        IEnumerable<ScheduledBackupDefinition> schedules,
        DateTime localNow)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(schedules);
        var profileMap = profiles.ToDictionary(p => p.Id);
        return schedules.Select(schedule =>
        {
            schedule.Validate();
            profileMap.TryGetValue(schedule.ProfileId, out var profile);
            var attention = profile is null ||
                schedule.LastRunStatus == ScheduledBackupRunStatus.Failed ||
                schedule.LastRunStatus == ScheduledBackupRunStatus.Running &&
                    schedule.LastRunUtc is { } started && DateTime.UtcNow - started > TimeSpan.FromHours(24);
            var last = schedule.LastRunUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "—";
            var next = schedule.GetNextRunLocal(localNow).ToString("yyyy-MM-dd HH:mm");
            var result = schedule.LastRunStatus switch
            {
                ScheduledBackupRunStatus.NeverRun => "טרם הופעל",
                ScheduledBackupRunStatus.Running when attention => "ריצה נקטעה / דורשת בדיקה",
                ScheduledBackupRunStatus.Running => "פועל כעת",
                ScheduledBackupRunStatus.Succeeded => "הצליח",
                ScheduledBackupRunStatus.Failed => "נכשל",
                _ => "לא ידוע"
            };
            var type = schedule.BackupKind == RemoteBackupKind.FullInstallation ? "התקנה מלאה" : "הגדרות והעלאות";
            if (schedule.IncludeDatabase) type += " + מסד נתונים";
            var cadence = schedule.Frequency == ScheduledBackupFrequency.Daily
                ? "יומי · " + schedule.LocalTime
                : "שבועי · " + schedule.DayOfWeek + " · " + schedule.LocalTime;
            var retention = schedule.RetentionCount == 0 ? "ללא מחיקה אוטומטית" : "שמירת " + schedule.RetentionCount;
            return new ScheduledBackupOverviewRow(schedule.Id, schedule.ProfileId,
                profile?.Name ?? schedule.ProfileName + " (פרופיל חסר)",
                profile?.HostDisplay ?? "—", schedule.InstallationPath, type, cadence,
                schedule.DestinationDirectory, next, last, result, schedule.LastRunSummary,
                retention, schedule.IncludeDatabase, attention);
        }).OrderBy(row => row.IsAttentionRequired ? 0 : 1)
          .ThenBy(row => row.NextRunLocal, StringComparer.Ordinal)
          .ThenBy(row => row.ServerName, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static string ExportCsv(IEnumerable<ScheduledBackupOverviewRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var builder = new StringBuilder();
        builder.AppendLine("Server,Endpoint,InstallationPath,BackupType,Cadence,Destination,NextRunLocal,LastRunLocal,LastResult,LastSummary,Retention,IncludesDatabase,NeedsAttention,ScheduleId");
        foreach (var row in rows)
        {
            var values = new[] { row.ServerName, row.Endpoint, row.InstallationPath,
                row.BackupType, row.Cadence, row.Destination, row.NextRunLocal,
                row.LastRunLocal, row.LastResult, row.LastSummary, row.Retention,
                row.IncludesDatabase ? "true" : "false", row.IsAttentionRequired ? "true" : "false",
                row.ScheduleId.ToString("D") };
            builder.AppendLine(string.Join(",", values.Select(EscapeCsv)));
        }
        return builder.ToString();
    }

    private static string EscapeCsv(string? value)
    {
        var text = value ?? string.Empty;
        var first = text.TrimStart();
        if (first.StartsWith('=') || first.StartsWith('+') || first.StartsWith('-') || first.StartsWith('@'))
            text = "'" + text;
        return "\"" + text.Replace("\"", "\"\"") + "\"";
    }
}