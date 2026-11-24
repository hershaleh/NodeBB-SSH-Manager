using System.Text;
using NodeBB.SSHManager.Models;

namespace NodeBB.SSHManager.Services;

public sealed record FleetReadinessRow(
    string ServerName, string Endpoint, string State, string OperatingSystem,
    string Node, string Npm, string Git, string Mongo, string Redis,
    string InitSystem, string PackageManager, string RemoteUser,
    string Findings, string CheckedAt, bool IsReachable, bool NeedsReview);

public sealed class FleetReadinessService
{
    public async Task<IReadOnlyList<FleetReadinessRow>> CollectAsync(
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
                return Unavailable(profile, "לא מחובר");
            await throttle.WaitAsync(cancellationToken);
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                var inventory = await new RemoteCapabilityService(session).DetectAsync(timeout.Token);
                return FromInventory(profile, inventory);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                DiagnosticLogService.Instance.Warning("FleetReadiness", "InventoryFailed",
                    "Remote inventory could not be collected: " + ex.Message, null, profile.Name);
                return Unavailable(profile, "בדיקה נכשלה: " + SafeError(ex.Message));
            }
            finally { throttle.Release(); }
        }).ToArray();
        return await Task.WhenAll(tasks);
    }

    public static FleetReadinessRow FromInventory(ServerProfile profile, RemoteServerInventory inventory)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(inventory);
        var findings = new List<string>();
        var nodeMissing = IsMissing(inventory.NodeVersion);
        var npmMissing = IsMissing(inventory.NpmVersion);
        var gitMissing = IsMissing(inventory.GitVersion);
        if (nodeMissing) findings.Add("Node.js לא זוהה — נדרשת בדיקה לפני הפעלת NodeBB");
        if (npmMissing) findings.Add("npm לא זוהה — נדרשת בדיקה לפני התקנה או תחזוקה");
        if (gitMissing) findings.Add("Git לא זוהה — פעולות המבוססות על Git עשויות שלא להיות זמינות");
        if (inventory.IsRoot) findings.Add("החיבור משתמש בחשבון root — מומלץ חשבון ייעודי עם הרשאות מצומצמות");
        if (IsMissing(inventory.PackageManager)) findings.Add("מנהל חבילות לא זוהה");
        if (findings.Count == 0) findings.Add("הכלים הבסיסיים זוהו; יש לאמת בנפרד תאימות לגרסת NodeBB ולמסד הנתונים");
        var needsReview = nodeMissing || npmMissing || gitMissing || inventory.IsRoot || IsMissing(inventory.PackageManager);
        return new FleetReadinessRow(profile.Name, profile.HostDisplay,
            needsReview ? "דורש בדיקה" : "כלים בסיסיים זמינים",
            inventory.OperatingSystem, inventory.NodeVersion, inventory.NpmVersion,
            inventory.GitVersion, inventory.MongoVersion, inventory.RedisVersion,
            inventory.InitSystem, inventory.PackageManager, inventory.RemoteUser,
            string.Join("; ", findings), inventory.DetectedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
            true, needsReview);
    }

    private static FleetReadinessRow Unavailable(ServerProfile profile, string reason) =>
        new(profile.Name, profile.HostDisplay, reason, "—", "—", "—", "—", "—", "—",
            "—", "—", "—", reason, "—", false, true);

    private static bool IsMissing(string? value) =>
        string.IsNullOrWhiteSpace(value) ||
        value.Contains("לא מותקן", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("לא זמין", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("לא-זוהה", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("לא ידוע", StringComparison.OrdinalIgnoreCase);

    private static string SafeError(string value)
    {
        var clean = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return clean.Length > 180 ? clean[..180] : clean;
    }

    public static string ExportCsv(IEnumerable<FleetReadinessRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var builder = new StringBuilder();
        builder.AppendLine("Server,Endpoint,State,OperatingSystem,Node,Npm,Git,Mongo,Redis,InitSystem,PackageManager,RemoteUser,Findings,CheckedAt,Reachable,NeedsReview");
        foreach (var row in rows)
        {
            var values = new[] { row.ServerName, row.Endpoint, row.State, row.OperatingSystem,
                row.Node, row.Npm, row.Git, row.Mongo, row.Redis, row.InitSystem,
                row.PackageManager, row.RemoteUser, row.Findings, row.CheckedAt,
                row.IsReachable ? "true" : "false", row.NeedsReview ? "true" : "false" };
            builder.AppendLine(string.Join(",", values.Select(EscapeCsv)));
        }
        return builder.ToString();
    }

    private static string EscapeCsv(string? value)
    {
        var text = value ?? string.Empty;
        if (text.Length > 0 && (text[0] is '=' or '+' or '-' or '@' or '\t' or '\r' or '\n' ||
            (char.IsWhiteSpace(text[0]) && (text.TrimStart().StartsWith('=') ||
             text.TrimStart().StartsWith('+') || text.TrimStart().StartsWith('@')))))
            text = "'" + text;
        return "\"" + text.Replace("\"", "\"\"") + "\"";
    }
}