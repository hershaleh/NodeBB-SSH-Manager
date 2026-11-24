using System.Text;
using NodeBB.SSHManager.Models;

namespace NodeBB.SSHManager.Services;

public sealed record FleetMaintenanceRow(
    Guid ProfileId, string ServerName, string Endpoint, string InstallationPath,
    string Version, string Branch, string DatabaseType, string NodeVersion, string NpmVersion,
    string Status, string Findings, bool IsReachable, bool CanUpgrade);

/// <summary>
/// Read-only, cross-server NodeBB upgrade readiness inspection. Actual upgrades remain
/// per-installation operations in the existing backup-first upgrade wizard.
/// </summary>
public sealed class FleetMaintenanceService
{
    public async Task<IReadOnlyList<FleetMaintenanceRow>> CollectAsync(
        IEnumerable<ServerProfile> profiles, IEnumerable<SshServerSession> sessions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(sessions);
        var connected = sessions.Where(s => s.IsConnected)
            .GroupBy(s => s.Profile.Id).ToDictionary(g => g.Key, g => g.First());
        using var throttle = new SemaphoreSlim(3, 3);
        var tasks = profiles.Select(async profile =>
        {
            if (!connected.TryGetValue(profile.Id, out var session))
                return new[] { Unavailable(profile, "לא מחובר — התחבר כדי לבדוק התקנות") };
            await throttle.WaitAsync(cancellationToken);
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(60));
                var token = timeout.Token;
                var paths = new List<string>();
                string? discoveryWarning = null;
                try
                {
                    paths.AddRange(new NodeBbInstallationStore(profile.Id).Load());
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
                {
                    discoveryWarning = "רשימת הנתיבים השמורה לא נקראה: " + Safe(ex.Message);
                }

                try
                {
                    var discovered = await new NodeBbRemoteService(session).DiscoverAsync(token);
                    paths.AddRange(discovered.Select(x => x.Path));
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    discoveryWarning = string.Join("; ", new[] { discoveryWarning,
                        "גילוי אוטומטי נכשל: " + Safe(ex.Message) }.Where(x => !string.IsNullOrWhiteSpace(x)));
                }

                var unique = paths.Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();
                if (unique.Length == 0)
                    return new[] { NoInstallation(profile, discoveryWarning) };

                var rows = new List<FleetMaintenanceRow>();
                foreach (var path in unique)
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        var installation = new NodeBbInstallation(path, "לא ידועה");
                        var preflight = await new NodeBbUpgradeService(session).InspectAsync(installation, token);
                        var row = FromPreflight(profile, path, preflight);
                        if (!string.IsNullOrWhiteSpace(discoveryWarning))
                            row = row with { Findings = row.Findings + "; " + discoveryWarning };
                        rows.Add(row);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        rows.Add(new FleetMaintenanceRow(profile.Id, profile.Name, profile.HostDisplay,
                            path, "לא ידועה", "—", "—", "—", "—", "בדיקה נכשלה",
                            Safe(ex.Message), true, false));
                    }
                }
                return rows.ToArray();
            }
            finally { throttle.Release(); }
        }).ToArray();
        return (await Task.WhenAll(tasks)).SelectMany(x => x)
            .OrderBy(x => x.ServerName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(x => x.InstallationPath, StringComparer.Ordinal).ToArray();
    }

    public static FleetMaintenanceRow FromPreflight(
        ServerProfile profile, string path, NodeBbUpgradePreflight preflight)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(preflight);
        var findings = new List<string>();
        if (!preflight.IsGitRepository) findings.Add("ההתקנה אינה מאגר Git");
        else if (!preflight.IsClean) findings.Add("יש שינויים או קבצים לא מעוקבים — השדרוג חסום");
        if (!preflight.HasOrigin) findings.Add("remote בשם origin חסר");
        if (!preflight.HasNode) findings.Add("Node.js חסר");
        if (!preflight.HasNpm) findings.Add("npm חסר");
        if (preflight.DatabaseType is not ("mongo" or "redis" or "postgres"))
            findings.Add("סוג מסד הנתונים אינו נתמך במסלול השדרוג האוטומטי");
        if (findings.Count == 0) findings.Add("בדיקות המקדימות עברו; נדרש גיבוי מוצפן ואישור נוסף באשף השדרוג");
        return new FleetMaintenanceRow(profile.Id, profile.Name, profile.HostDisplay, path,
            preflight.CurrentVersion, string.IsNullOrWhiteSpace(preflight.CurrentBranch) ? "(detached)" : preflight.CurrentBranch,
            preflight.DatabaseType, preflight.NodeVersion, preflight.NpmVersion,
            preflight.CanUpgrade ? "מוכן לשדרוג לאחר גיבוי" : "נדרשת תחזוקה",
            string.Join("; ", findings), true, preflight.CanUpgrade);
    }

    private static FleetMaintenanceRow NoInstallation(ServerProfile profile, string? warning) =>
        new(profile.Id, profile.Name, profile.HostDisplay, "—", "—", "—", "—", "—", "—",
            "לא נמצאה התקנת NodeBB",
            warning ?? "לא נמצאה התקנה בנתיבים שנסרקו; אפשר להוסיף נתיב ידני במרכז ניהול NodeBB.",
            true, false);

    private static FleetMaintenanceRow Unavailable(ServerProfile profile, string reason) =>
        new(profile.Id, profile.Name, profile.HostDisplay, "—", "—", "—", "—", "—", "—",
            reason, reason, false, false);

    private static string Safe(string value)
    {
        var clean = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return clean.Length > 220 ? clean[..220] : clean;
    }

    public static string ExportCsv(IEnumerable<FleetMaintenanceRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var b = new StringBuilder();
        b.AppendLine("Server,Endpoint,InstallationPath,Version,Branch,Database,Node,Npm,Status,Findings,Reachable,CanUpgrade");
        foreach (var r in rows)
        {
            var fields = new[] { r.ServerName, r.Endpoint, r.InstallationPath, r.Version,
                r.Branch, r.DatabaseType, r.NodeVersion, r.NpmVersion, r.Status, r.Findings,
                r.IsReachable ? "true" : "false", r.CanUpgrade ? "true" : "false" };
            b.AppendLine(string.Join(",", fields.Select(Escape)));
        }
        return b.ToString();
    }

    private static string Escape(string? value)
    {
        var s = value ?? "";
        if (s.Length > 0 && (s[0] is '=' or '+' or '-' or '@' or '\t' or '\r' or '\n'))
            s = "'" + s;
        return "\"" + s.Replace("\"", "\"\"") + "\"";
    }
}
