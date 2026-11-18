using System.Text.RegularExpressions;

namespace NodeBB.SSHManager.Services;

public sealed record NodeBbUpgradePreflight(
    bool IsGitRepository, bool IsClean, bool HasOrigin, bool HasNode, bool HasNpm,
    string CurrentVersion, string CurrentBranch, string CurrentCommit, string NodeVersion,
    string NpmVersion, string DatabaseType, string Message)
{
    public bool CanUpgrade => IsGitRepository && IsClean && HasOrigin && HasNode && HasNpm
        && DatabaseType is "mongo" or "redis" or "postgres";
}

public sealed record NodeBbUpgradeResult(bool Success, bool Restarted, string Message, string Output);

/// <summary>
/// Conservative Git-tag based NodeBB upgrader. It never discards a dirty working tree,
/// never changes branches to an unvalidated ref, and never claims database rollback.
/// </summary>
public sealed class NodeBbUpgradeService
{
    private static readonly Regex VersionTag = new(@"^v[0-9]+\.[0-9]+\.[0-9]+(?:-[A-Za-z0-9.-]+)?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private readonly SshServerSession _session;

    public NodeBbUpgradeService(SshServerSession session) => _session = session;

    public static bool IsValidTargetTag(string? tag) => !string.IsNullOrWhiteSpace(tag)
        && VersionTag.IsMatch(tag) && !tag.Contains("..", StringComparison.Ordinal);

    public async Task<NodeBbUpgradePreflight> InspectAsync(NodeBbInstallation installation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(installation);
        var result = await _session.ExecuteAsync(BuildPreflightScript(installation.Path), cancellationToken);
        if (result.ExitCode != 0)
            throw new InvalidOperationException(Sanitize(result.Error, "בדיקת המוכנות לשדרוג נכשלה."));
        var values = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split('|', 2))
            .Where(parts => parts.Length == 2)
            .GroupBy(parts => parts[0], StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last()[1], StringComparer.Ordinal);
        string Get(string key) => values.TryGetValue(key, out var value) ? value : "";
        bool Yes(string key) => Get(key) == "yes";
        var info = new NodeBbUpgradePreflight(Yes("GIT"), Yes("CLEAN"), Yes("ORIGIN"),
            Yes("NODE"), Yes("NPM"), Get("VERSION"), Get("BRANCH"), Get("COMMIT"),
            Get("NODE_VERSION"), Get("NPM_VERSION"), Get("DB"), Get("MESSAGE"));
        DiagnosticLogService.Instance.Info("NodeBBUpgrade", "PreflightCompleted",
            $"Upgrade preflight completed; ready={info.CanUpgrade}; database={info.DatabaseType}.", _session.Profile.Name);
        return info;
    }

    public async Task<NodeBbUpgradeResult> UpgradeAsync(NodeBbInstallation installation, string targetTag,
        bool databaseBackupConfirmed, bool filesBackupConfirmed, CancellationToken cancellationToken = default)
    {
        if (!IsValidTargetTag(targetTag))
            throw new ArgumentException("יש להזין תגית גרסה תקינה, לדוגמה v4.19.1.", nameof(targetTag));
        if (!databaseBackupConfirmed || !filesBackupConfirmed)
            throw new InvalidOperationException("לפני שדרוג חובה לאשר שנוצרו גיבוי מסד נתונים וגיבוי קבצים, הגדרות והעלאות.");
        var preflight = await InspectAsync(installation, cancellationToken);
        if (!preflight.CanUpgrade)
            throw new InvalidOperationException("השרת אינו מוכן לשדרוג. נדרשים Git, עץ עבודה נקי, remote בשם origin, Node.js, npm ומסד נתונים נתמך. בדוק את פירוט הבדיקות.");
        var runtimeService = new NodeBbRuntimeService(_session);
        var runtime = await runtimeService.DetectAsync(installation, cancellationToken);
        var stopped = await runtimeService.ExecuteLifecycleAsync(installation, runtime, "stop", cancellationToken);
        if (stopped.ExitCode != 0)
            throw new InvalidOperationException(Sanitize(stopped.Error, "לא ניתן לעצור את NodeBB. השדרוג בוטל לפני שינוי הקבצים."));
        DiagnosticLogService.Instance.Info("NodeBBUpgrade", "UpgradeStopped",
            $"NodeBB stopped before upgrading to {targetTag}.", _session.Profile.Name);

        var update = await _session.ExecuteAsync(BuildUpgradeScript(installation.Path, targetTag, preflight.CurrentCommit), cancellationToken);
        if (update.ExitCode != 0)
        {
            DiagnosticLogService.Instance.Error("NodeBBUpgrade", "UpgradeFailed",
                $"Upgrade to {targetTag} failed with exit code {update.ExitCode}. NodeBB was left stopped to protect data; database migrations may have partially run.", _session.Profile.Name);
            return new NodeBbUpgradeResult(false, false,
                "השדרוג נכשל. NodeBB הושאר במצב עצור כדי למנוע הפעלה במצב לא עקבי. ייתכן שפעולת מיגרציית מסד הנתונים כבר התחילה; יש לבדוק את הלוגים ולשחזר גיבוי מתאים לפני הפעלה מחדש.",
                Sanitize(update.Output + Environment.NewLine + update.Error, "לא התקבל פירוט מהשרת."));
        }

        var started = await runtimeService.ExecuteLifecycleAsync(installation, runtime, "start", cancellationToken);
        if (started.ExitCode != 0)
        {
            DiagnosticLogService.Instance.Error("NodeBBUpgrade", "UpgradeStartFailed",
                $"Upgrade to {targetTag} completed, but NodeBB did not start; exit code {started.ExitCode}.", _session.Profile.Name);
            return new NodeBbUpgradeResult(true, false,
                "קוד NodeBB עודכן ומיגרציית השדרוג הסתיימה, אך הפעלת השירות נכשלה. יש לבדוק לוגים ולפתור את בעיית ההפעלה.",
                Sanitize(started.Output + Environment.NewLine + started.Error, "לא התקבל פירוט מהשרת."));
        }

        DiagnosticLogService.Instance.Info("NodeBBUpgrade", "UpgradeCompleted",
            $"NodeBB upgraded to {targetTag} and start command succeeded.", _session.Profile.Name);
        return new NodeBbUpgradeResult(true, true,
            $"השדרוג ל-{targetTag} הסתיים ופקודת ההפעלה הצליחה. מומלץ לבדוק את הפורום בדפדפן.", Sanitize(update.Output, ""));
    }

    internal static string BuildPreflightScript(string installationPath)
    {
        if (string.IsNullOrWhiteSpace(installationPath) || installationPath.IndexOf('\0') >= 0)
            throw new ArgumentException("נתיב התקנת NodeBB אינו תקין.", nameof(installationPath));
        var path = ShellQuote(installationPath);
        return $$"""
set +e
cd {{path}} 2>/dev/null || exit 2
if git rev-parse --is-inside-work-tree >/dev/null 2>&1; then
  echo 'GIT|yes'
  [ -z "$(git status --porcelain --untracked-files=all 2>/dev/null)" ] && echo 'CLEAN|yes' || echo 'CLEAN|no'
  git remote get-url origin >/dev/null 2>&1 && echo 'ORIGIN|yes' || echo 'ORIGIN|no'
  printf 'BRANCH|%s
' "$(git branch --show-current 2>/dev/null)"
  printf 'COMMIT|%s
' "$(git rev-parse HEAD 2>/dev/null)"
else
  echo 'GIT|no'; echo 'CLEAN|no'; echo 'ORIGIN|no'
fi
command -v node >/dev/null 2>&1 && { echo 'NODE|yes'; printf 'NODE_VERSION|%s
' "$(node --version 2>/dev/null)"; } || { echo 'NODE|no'; echo 'NODE_VERSION|לא מותקן'; }
command -v npm >/dev/null 2>&1 && { echo 'NPM|yes'; printf 'NPM_VERSION|%s
' "$(npm --version 2>/dev/null)"; } || { echo 'NPM|no'; echo 'NPM_VERSION|לא מותקן'; }
if command -v node >/dev/null 2>&1 && [ -r package.json ]; then
  printf 'VERSION|%s
' "$(node -e "try{console.log(require('./package.json').version||'לא ידועה')}catch(e){console.log('לא ידועה')}")"
  printf 'DB|%s
' "$(node -e "try{console.log(require('./config.json').database||'unknown')}catch(e){console.log('unknown')}")"
else
  echo 'VERSION|לא ידועה'; echo 'DB|unknown'
fi
""";
    }

    internal static string BuildUpgradeScript(string installationPath, string targetTag, string expectedCommit)
    {
        if (string.IsNullOrWhiteSpace(installationPath) || installationPath.IndexOf('\0') >= 0)
            throw new ArgumentException("נתיב התקנת NodeBB אינו תקין.", nameof(installationPath));
        if (!IsValidTargetTag(targetTag)) throw new ArgumentException("תגית הגרסה אינה תקינה.", nameof(targetTag));
        if (!Regex.IsMatch(expectedCommit, "^[a-fA-F0-9]{40,64}$"))
            throw new ArgumentException("מזהה commit קודם אינו תקין.", nameof(expectedCommit));
        return $$"""
set -eu
cd {{ShellQuote(installationPath)}}
EXPECTED={{ShellQuote(expectedCommit)}}
TARGET={{ShellQuote(targetTag)}}
CURRENT="$(git rev-parse HEAD)"
[ "$CURRENT" = "$EXPECTED" ] || { echo 'הקוד השתנה מאז בדיקת המוכנות; השדרוג בוטל.' >&2; exit 31; }
[ -z "$(git status --porcelain --untracked-files=all)" ] || { echo 'עץ העבודה אינו נקי; השדרוג בוטל.' >&2; exit 32; }
git fetch --tags origin
git rev-parse --verify "refs/tags/$TARGET^{commit}" >/dev/null 2>&1 || { echo 'תגית הגרסה המבוקשת אינה קיימת ב-origin.' >&2; exit 33; }
git checkout --detach "refs/tags/$TARGET"
./nodebb upgrade
""";
    }

    private static string Sanitize(string value, string fallback)
    {
        var safe = string.Join(Environment.NewLine, value.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !line.Contains("password", StringComparison.OrdinalIgnoreCase)
                && !line.Contains("credential", StringComparison.OrdinalIgnoreCase)
                && !line.Contains("mongodb://", StringComparison.OrdinalIgnoreCase)
                && !line.Contains("redis://", StringComparison.OrdinalIgnoreCase)
                && !line.Contains("postgres://", StringComparison.OrdinalIgnoreCase)).Take(30));
        return string.IsNullOrWhiteSpace(safe) ? fallback : safe;
    }

    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''") + "'";
}
