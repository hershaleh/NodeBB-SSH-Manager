namespace NodeBB.SSHManager.Services;

public sealed record RemoteSecuritySnapshot(
    string RemoteUser,
    bool IsRoot,
    string Groups,
    bool HasNonInteractiveSudo,
    string SshPermitRootLogin,
    string SshPasswordAuthentication,
    string SshPublicKeyAuthentication,
    string SshPermitEmptyPasswords,
    string SshMaxAuthTries,
    string FirewallStatus,
    string ListeningPorts,
    string InstallationOwner,
    string InstallationPermissions,
    string ConfigOwner,
    string ConfigPermissions,
    bool ConfigReadable,
    bool ConfigWritable);

public sealed record SecurityAuditItem(string Severity, string Item, string CurrentValue, string Recommendation);

public sealed class RemoteSecurityAuditService
{
    private readonly SshServerSession _session;

    public RemoteSecurityAuditService(SshServerSession session) => _session = session;

    public async Task<IReadOnlyList<SecurityAuditItem>> RunAsync(
        NodeBbInstallation installation, CancellationToken cancellationToken = default)
    {
        var path = ShellQuote(installation.Path);
        var script = $$"""
cd {{path}} || exit 1
SSH_EFFECTIVE=""
if command -v sshd >/dev/null 2>&1; then SSH_EFFECTIVE="$(sshd -T 2>/dev/null)"; fi
SSH_ROOT="$(printf '%s\n' "$SSH_EFFECTIVE" | awk '$1=="permitrootlogin" {print $2; exit}')"
SSH_PASSWORD="$(printf '%s\n' "$SSH_EFFECTIVE" | awk '$1=="passwordauthentication" {print $2; exit}')"
SSH_KEY="$(printf '%s\n' "$SSH_EFFECTIVE" | awk '$1=="pubkeyauthentication" {print $2; exit}')"
SSH_EMPTY="$(printf '%s\n' "$SSH_EFFECTIVE" | awk '$1=="permitemptypasswords" {print $2; exit}')"
SSH_TRIES="$(printf '%s\n' "$SSH_EFFECTIVE" | awk '$1=="maxauthtries" {print $2; exit}')"
[ -n "$SSH_ROOT" ] || SSH_ROOT=לא-זמין
[ -n "$SSH_PASSWORD" ] || SSH_PASSWORD=לא-זמין
[ -n "$SSH_KEY" ] || SSH_KEY=לא-זמין
[ -n "$SSH_EMPTY" ] || SSH_EMPTY=לא-זמין
[ -n "$SSH_TRIES" ] || SSH_TRIES=לא-זמין
REMOTE_USER="$(id -un 2>/dev/null)"
ROOT_FLAG=לא
[ "$(id -u 2>/dev/null)" = "0" ] && ROOT_FLAG=כן
USER_GROUPS="$(id -nG 2>/dev/null)"
SUDO_FLAG=לא
if command -v sudo >/dev/null 2>&1 && sudo -n true >/dev/null 2>&1; then SUDO_FLAG=כן; fi
INSTALL_OWNER="$(stat -c '%U:%G' . 2>/dev/null || stat -f '%Su:%Sg' . 2>/dev/null)"
INSTALL_MODE="$(stat -c '%a' . 2>/dev/null || stat -f '%Lp' . 2>/dev/null)"
CONFIG_OWNER="$(stat -c '%U:%G' config.json 2>/dev/null || stat -f '%Su:%Sg' config.json 2>/dev/null)"
CONFIG_MODE="$(stat -c '%a' config.json 2>/dev/null || stat -f '%Lp' config.json 2>/dev/null)"
CONFIG_READABLE=לא
[ -r config.json ] && CONFIG_READABLE=כן
CONFIG_WRITABLE=לא
[ -w config.json ] && CONFIG_WRITABLE=כן
FIREWALL=לא-זוהה
if command -v ufw >/dev/null 2>&1; then if ufw status 2>/dev/null | grep -qi '^Status: active'; then FIREWALL=active; else FIREWALL=inactive; fi
elif command -v firewall-cmd >/dev/null 2>&1; then FIREWALL="$(firewall-cmd --state 2>/dev/null)"
elif command -v nft >/dev/null 2>&1; then
  if nft list ruleset 2>/dev/null | grep -q "chain"; then FIREWALL=nftables-active; else FIREWALL=nftables-empty; fi
elif command -v iptables >/dev/null 2>&1; then
  if iptables -S >/dev/null 2>&1; then FIREWALL=iptables-זמין; else FIREWALL=iptables-לא-נגיש; fi
fi
PORTS=לא-זמין
if command -v ss >/dev/null 2>&1; then PORTS="$(ss -tln 2>/dev/null | awk 'NR>1 {print $4}' | head -n 8 | tr '\n' ',')"; fi
printf 'USER|%s\n' "$REMOTE_USER"
printf 'ROOT|%s\n' "$ROOT_FLAG"
printf 'GROUPS|%s\n' "$USER_GROUPS"
printf 'SUDO|%s\n' "$SUDO_FLAG"
printf 'SSH_ROOT|%s\n' "$SSH_ROOT"
printf 'SSH_PASSWORD|%s\n' "$SSH_PASSWORD"
printf 'SSH_KEY|%s\n' "$SSH_KEY"
printf 'SSH_EMPTY|%s\n' "$SSH_EMPTY"
printf 'SSH_TRIES|%s\n' "$SSH_TRIES"
printf 'FIREWALL|%s\n' "$FIREWALL"
printf 'PORTS|%s\n' "$PORTS"
printf 'INSTALL_OWNER|%s\n' "$INSTALL_OWNER"
printf 'INSTALL_MODE|%s\n' "$INSTALL_MODE"
printf 'CONFIG_OWNER|%s\n' "$CONFIG_OWNER"
printf 'CONFIG_MODE|%s\n' "$CONFIG_MODE"
printf 'CONFIG_READABLE|%s\n' "$CONFIG_READABLE"
printf 'CONFIG_WRITABLE|%s\n' "$CONFIG_WRITABLE"
""";

        DiagnosticLogService.Instance.Info("SecurityAudit", "AuditStarted", "Read-only remote security audit started.", _session.Profile.Name);
        var result = await _session.ExecuteAsync(script, cancellationToken);
        if (result.ExitCode != 0 && string.IsNullOrWhiteSpace(result.Output))
        {
            var error = new InvalidOperationException(string.IsNullOrWhiteSpace(result.Error)
                ? "בדיקת האבטחה נכשלה."
                : result.Error);
            DiagnosticLogService.Instance.Error("SecurityAudit", "AuditFailed", error, _session.Profile.Name);
            throw error;
        }

        var values = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split('|', 2))
            .Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.OrdinalIgnoreCase);
        string Get(string key) => values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : "לא זמין";
        bool Yes(string key) => Get(key) == "כן";

        var snapshot = new RemoteSecuritySnapshot(Get("USER"), Yes("ROOT"), Get("GROUPS"), Yes("SUDO"),
            Get("SSH_ROOT"), Get("SSH_PASSWORD"), Get("SSH_KEY"), Get("SSH_EMPTY"), Get("SSH_TRIES"),
            Get("FIREWALL"), Get("PORTS"), Get("INSTALL_OWNER"), Get("INSTALL_MODE"),
            Get("CONFIG_OWNER"), Get("CONFIG_MODE"), Yes("CONFIG_READABLE"), Yes("CONFIG_WRITABLE"));

        var firewallActive = snapshot.FirewallStatus is "active" or "running" or "nftables-active";
        var sshRootSeverity = snapshot.SshPermitRootLogin == "yes" ? "קריטי" : snapshot.SshPermitRootLogin == "לא-זמין" ? "בדיקה נדרשת" : "תקין";
        var sshPasswordSeverity = snapshot.SshPasswordAuthentication == "yes" ? "אזהרה" : snapshot.SshPasswordAuthentication == "לא-זמין" ? "בדיקה נדרשת" : "תקין";
        var sshKeySeverity = snapshot.SshPublicKeyAuthentication == "no" ? "אזהרה" : snapshot.SshPublicKeyAuthentication == "לא-זמין" ? "בדיקה נדרשת" : "תקין";
        var sshEmptySeverity = snapshot.SshPermitEmptyPasswords == "yes" ? "קריטי" : snapshot.SshPermitEmptyPasswords == "לא-זמין" ? "בדיקה נדרשת" : "תקין";
        var findings = new List<SecurityAuditItem>
        {
            new(snapshot.IsRoot ? "אזהרה" : "תקין", "משתמש SSH", $"{snapshot.RemoteUser} · root: {(snapshot.IsRoot ? "כן" : "לא")}",
                snapshot.IsRoot ? "מומלץ לנהל את NodeBB ממשתמש ייעודי בעל הרשאות מצומצמות." : "החיבור אינו משתמש root."),
            new("מידע", "קבוצות המשתמש", snapshot.Groups, "ודא שהמשתמש אינו חבר בקבוצות בעלות הרשאות עודפות."),
            new(snapshot.HasNonInteractiveSudo ? "מידע" : "תקין", "הרשאות sudo ללא סיסמה", snapshot.HasNonInteractiveSudo ? "זמינות" : "לא זמינות",
                snapshot.HasNonInteractiveSudo ? "בדוק שהרשאות sudo מוגבלות לפקודות הנחוצות." : "לא זוהתה הרשאת sudo ללא סיסמה."),
            new(sshRootSeverity, "SSH PermitRootLogin", snapshot.SshPermitRootLogin,
                snapshot.SshPermitRootLogin == "yes" ? "מומלץ לבטל התחברות SSH ישירה כ-root." : "בדוק את המדיניות בפועל אם הגדרת SSH לא זוהתה."),
            new(sshPasswordSeverity, "SSH PasswordAuthentication", snapshot.SshPasswordAuthentication,
                snapshot.SshPasswordAuthentication == "yes" ? "שקול אימות מפתחות SSH בלבד, בהתאם למדיניות הגישה שלך." : "בדוק את המדיניות בפועל אם הגדרת SSH לא זוהתה."),
            new(sshKeySeverity, "SSH PubkeyAuthentication", snapshot.SshPublicKeyAuthentication,
                snapshot.SshPublicKeyAuthentication == "no" ? "הפעל אימות מפתחות אם הוא נדרש למדיניות ההתחברות." : "בדוק את המדיניות בפועל אם הגדרת SSH לא זוהתה."),
            new(sshEmptySeverity, "SSH PermitEmptyPasswords", snapshot.SshPermitEmptyPasswords,
                snapshot.SshPermitEmptyPasswords == "yes" ? "יש לבטל מיד אפשרות של סיסמאות ריקות." : "בדוק את המדיניות בפועל אם הגדרת SSH לא זוהתה."),
            new("מידע", "SSH MaxAuthTries", snapshot.SshMaxAuthTries, "בדוק שהערך מתאים למדיניות האבטחה שלך."),
            new(firewallActive ? "תקין" : "בדיקה נדרשת", "חומת אש", snapshot.FirewallStatus, "ודא שרק שירותים ויציאות נדרשים חשופים לרשת. מצב לא מזוהה אינו מעיד שאין חומת אש."),
            new(snapshot.ConfigWritable ? "אזהרה" : "תקין", "הרשאות כתיבה ל-config.json", snapshot.ConfigWritable ? "למשתמש SSH יש הרשאת כתיבה" : "אין הרשאת כתיבה או שהקובץ אינו נגיש לכתיבה",
                snapshot.ConfigWritable ? "הגן על קובץ התצורה המכיל פרטי גישה והגבל הרשאות ככל האפשר." : "בדוק שהבעלים וההרשאות מתאימים לתחזוקה."),
            new(snapshot.ConfigReadable ? "תקין" : "בדיקה נדרשת", "קריאות config.json", snapshot.ConfigReadable ? "קריא למשתמש SSH" : "אינו קריא או לא נמצא",
                "הגבל הרשאות קריאה לקובץ המכיל פרטי גישה."),
            new("מידע", "בעלות והרשאות config.json", $"{snapshot.ConfigOwner} · {snapshot.ConfigPermissions}", "ודא שקובץ התצורה בבעלות המשתמש המתאים ובהרשאות מצומצמות."),
            new("מידע", "בעלות והרשאות התקנה", $"{snapshot.InstallationOwner} · {snapshot.InstallationPermissions}", "ודא שהתיקייה בבעלות משתמש NodeBB ייעודי ושאין הרשאות כתיבה מיותרות."),
            new("מידע", "יציאות TCP מאזינות", snapshot.ListeningPorts, "בדוק שאין שירותים מיותרים החשופים לרשת.")
        };

        DiagnosticLogService.Instance.Info("SecurityAudit", "AuditCompleted", $"Read-only security audit completed; {findings.Count} findings.", _session.Profile.Name);
        return findings;
    }

    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''") + "'";
}
