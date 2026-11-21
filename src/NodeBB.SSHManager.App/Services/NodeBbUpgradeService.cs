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


    public async Task VerifyTargetTagAsync(NodeBbInstallation installation, string targetTag,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(installation);
        if (!IsValidTargetTag(targetTag))
            throw new ArgumentException("יש להזין תגית גרסה תקינה, לדוגמה v4.19.1.", nameof(targetTag));
        var check = await _session.ExecuteAsync(BuildVerifyTargetTagScript(installation.Path, targetTag), cancellationToken);
        if (check.ExitCode != 0)
            throw new InvalidOperationException(Sanitize(check.Error,
                "תגית הגרסה לא נמצאה ב-origin. NodeBB לא נעצר ולא בוצע שינוי בקבצים."));
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
        await VerifyTargetTagAsync(installation, targetTag, cancellationToken);
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

        DiagnosticLogService.Instance.Info("NodeBBUpgrade", "UpgradeStarted",
            $"NodeBB start command succeeded after upgrade to {targetTag}; checking HTTP health.", _session.Profile.Name);

        var health = await _session.ExecuteAsync(BuildHealthCheckScript(installation.Path), cancellationToken);
        if (health.ExitCode != 0)
        {
            DiagnosticLogService.Instance.Error("NodeBBUpgrade", "PostUpgradeHealthCheckFailed",
                $"NodeBB upgrade to {targetTag} completed and start command succeeded, but the HTTP health check failed: {Sanitize(health.Error + Environment.NewLine + health.Output, "No health-check details.")}",
                _session.Profile.Name);
            return new NodeBbUpgradeResult(false, true,
                $"קוד NodeBB עודכן ופקודת ההפעלה הצליחה, אך הפורום לא עבר בדיקת HTTP בתוך 45 שניות. הגיבויים המוצפנים נשמרו. אין לבצע שחזור אוטומטי: ייתכן שמיגרציות מסד הנתונים כבר הושלמו. יש לבדוק את לוגי NodeBB, הגדרות bind/port, reverse proxy וחיבור למסד הנתונים.",
                Sanitize(health.Output + Environment.NewLine + health.Error, "לא התקבל פירוט מבדיקת הבריאות."));
        }

        DiagnosticLogService.Instance.Info("NodeBBUpgrade", "UpgradeCompleted",
            $"NodeBB upgraded to {targetTag}; start command and HTTP health check succeeded.", _session.Profile.Name);
        return new NodeBbUpgradeResult(true, true,
            $"השדרוג ל-{targetTag} הסתיים בהצלחה. השירות הופעל והפורום השיב לבקשת HTTP.", Sanitize(update.Output + Environment.NewLine + health.Output, ""));
    }

    internal static string BuildHealthCheckScript(string installationPath, int timeoutSeconds = 45)
    {
        RemotePathSafety.ValidateNodeBbInstallationPath(installationPath);
        if (timeoutSeconds is < 10 or > 120)
            throw new ArgumentOutOfRangeException(nameof(timeoutSeconds), "זמן בדיקת הבריאות חייב להיות בין 10 ל-120 שניות.");

        return $$"""
set -eu
cd {{ShellQuote(installationPath)}}
command -v node >/dev/null 2>&1 || { echo 'Node.js is unavailable for the HTTP health check.' >&2; exit 70; }
node <<'NODE'
'use strict';
const fs = require('fs');
const http = require('http');

let config;
try {
  config = JSON.parse(fs.readFileSync('./config.json', 'utf8'));
} catch (error) {
  console.error('Cannot read or parse NodeBB config.json for the HTTP health check.');
  process.exit(71);
}

const port = Number(config.port || 4567);
if (!Number.isInteger(port) || port < 1 || port > 65535) {
  console.error('NodeBB config.json contains an invalid HTTP port.');
  process.exit(72);
}

let host = String(config.bind_address || '127.0.0.1').trim();
if (!host || host === '0.0.0.0' || host === '*' || host === '::') host = host === '::' ? '::1' : '127.0.0.1';
const timeoutAt = Date.now() + ({{timeoutSeconds}} * 1000);

function probe() {
  return new Promise(resolve => {
    const request = http.get({ hostname: host, port, path: '/', method: 'GET', timeout: 2500 }, response => {
      const status = response.statusCode || 0;
      response.resume();
      resolve({ ok: (status >= 200 && status < 400) || status === 401 || status === 403, status });
    });
    request.on('timeout', () => request.destroy(new Error('HTTP probe timed out')));
    request.on('error', () => resolve({ ok: false, status: 0 }));
  });
}

(async () => {
  let lastStatus = 0;
  while (Date.now() < timeoutAt) {
    const result = await probe();
    lastStatus = result.status;
    if (result.ok) {
      console.log('NODEBB_HTTP_HEALTH|ok|' + result.status + '|' + host + '|' + port);
      process.exit(0);
    }
    await new Promise(resolve => setTimeout(resolve, 1500));
  }
  console.error('NODEBB_HTTP_HEALTH|failed|last_status=' + lastStatus + '|host=' + host + '|port=' + port + '|timeout_seconds={{timeoutSeconds}}');
  process.exit(1);
})().catch(() => {
  console.error('NodeBB HTTP health check encountered an unexpected error.');
  process.exit(73);
});
NODE
""";
    }

    internal static string BuildPreflightScript(string installationPath)
    {
        RemotePathSafety.ValidateNodeBbInstallationPath(installationPath);
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

    internal static string BuildVerifyTargetTagScript(string installationPath, string targetTag)
    {
        RemotePathSafety.ValidateNodeBbInstallationPath(installationPath);
        if (!IsValidTargetTag(targetTag)) throw new ArgumentException("תגית הגרסה אינה תקינה.", nameof(targetTag));
        return $"set -eu\ncd {ShellQuote(installationPath)}\ngit fetch --tags origin\nif ! git rev-parse --verify {ShellQuote("refs/tags/" + targetTag + "^{commit}")} >/dev/null 2>&1; then echo \"Tag not found on origin.\" >&2; exit 33; fi\n";
    }

    internal static string BuildUpgradeScript(string installationPath, string targetTag, string expectedCommit)
    {
        RemotePathSafety.ValidateNodeBbInstallationPath(installationPath);
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
