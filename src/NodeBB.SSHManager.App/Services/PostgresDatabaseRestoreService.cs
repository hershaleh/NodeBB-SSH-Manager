using System.Text;
using System.Text.RegularExpressions;

namespace NodeBB.SSHManager.Services;

/// <summary>
/// Restores a PostgreSQL custom dump into the configured NodeBB database. It validates the TOC,
/// creates a pre-restore dump, uses a single transaction, and attempts rollback on failure.
/// </summary>
public sealed class PostgresDatabaseRestoreService
{
    private readonly SshServerSession _session;
    public PostgresDatabaseRestoreService(SshServerSession session) => _session = session;

    public static async Task ValidateDumpAsync(string path, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new FileNotFoundException("קובץ גיבוי PostgreSQL לא נמצא.", path);
        if (new FileInfo(path).Length < 5)
            throw new InvalidDataException("קובץ הגיבוי קצר מדי.");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
        var header = new byte[5];
        await stream.ReadExactlyAsync(header, cancellationToken);
        if (!header.AsSpan().SequenceEqual(Encoding.ASCII.GetBytes("PGDMP")))
            throw new InvalidDataException("הקובץ אינו בפורמט PostgreSQL custom dump.");
    }

    public async Task RestoreAsync(NodeBbInstallation installation, string localDump,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(installation);
        await ValidateDumpAsync(localDump, cancellationToken);
        var work = "/tmp/nodebb-postgres-restore-" + Guid.NewGuid().ToString("N");
        var remoteDump = work + "/incoming.dump";
        var preserveRollback = false;
        var restoreScriptStarted = false;
        try
        {
            var init = await _session.ExecuteAsync($"umask 077 && mkdir -m 700 -- {ShellQuote(work)}", cancellationToken);
            if (init.ExitCode != 0)
                throw new InvalidOperationException("לא ניתן ליצור תיקיית עבודה זמנית ומוגנת בשרת.");
            using (var sftp = _session.CreateSftpClient())
            {
                await Task.Run(sftp.Connect, cancellationToken);
                await using var input = new FileStream(localDump, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
                await Task.Run(() => sftp.UploadFile(input, remoteDump, false), cancellationToken);
            }

            DiagnosticLogService.Instance.Warning("PostgresRestore", "RestoreStarted",
                "PostgreSQL restore started; a pre-restore dump is created before modifying database objects.", null, _session.Profile.Name);
            restoreScriptStarted = true;
            var result = await _session.ExecuteAsync(BuildRestoreScript(installation.Path, work), cancellationToken);
            if (result.ExitCode == 42)
            {
                preserveRollback = true;
                throw new InvalidOperationException(
                    $"השחזור נכשל וגם ניסיון ההחזרה נכשל. גיבוי החירום נשמר בשרת בתיקייה {work}; אין למחוק אותה. נדרשת התערבות מנהל.");
            }
            if (result.ExitCode == 41)
                throw new InvalidOperationException("השחזור נכשל. המערכת החזירה את אובייקטי מסד הנתונים לגיבוי שנוצר לפני הפעולה.");
            if (result.ExitCode != 0)
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.Error)
                    ? $"שחזור PostgreSQL נכשל (קוד {result.ExitCode})."
                    : SanitizeError(result.Error));

            DiagnosticLogService.Instance.Info("PostgresRestore", "RestoreCompleted",
                "PostgreSQL restore completed and the pre-restore dump was removed.", _session.Profile.Name);
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Instance.Error("PostgresRestore", "RestoreFailed", ex, _session.Profile.Name);
            throw;
        }
        finally
        {
            // The remote EXIT trap owns cleanup after the script starts. Never delete a possible
            // rollback dump from the client side after a timeout or SSH disconnect.
            if (!restoreScriptStarted && !preserveRollback)
            {
                try { await _session.ExecuteAsync($"rm -rf -- {ShellQuote(work)}", CancellationToken.None); }
                catch
                {
                    DiagnosticLogService.Instance.Warning("PostgresRestore", "RemoteCleanupFailed",
                        "Could not remove temporary PostgreSQL upload directory.", null, _session.Profile.Name);
                }
            }
        }
    }

    internal static string BuildRestoreScript(string installationPath, string workDirectory)
    {
        RemotePathSafety.ValidateNodeBbInstallationPath(installationPath);
        if (!Regex.IsMatch(workDirectory, @"^/tmp/nodebb-postgres-restore-[a-f0-9]{32}$"))
            throw new ArgumentException("נתיב העבודה הזמני אינו תקין.", nameof(workDirectory));

        return $$"""
set -eu
INSTALLATION={{ShellQuote(installationPath)}}
WORK={{ShellQuote(workDirectory)}}
KEEP_WORK=0
cleanup() { if [ "$KEEP_WORK" = 0 ]; then rm -rf -- "$WORK"; fi; }
trap cleanup EXIT
command -v node >/dev/null 2>&1 || { echo 'Node.js לא נמצא בשרת.' >&2; exit 127; }
command -v pg_restore >/dev/null 2>&1 || { echo 'pg_restore אינו מותקן בשרת.' >&2; exit 127; }
command -v pg_dump >/dev/null 2>&1 || { echo 'pg_dump אינו מותקן בשרת.' >&2; exit 127; }
node - "$INSTALLATION/config.json" "$WORK/connection.env" "$WORK/pgpass" "$WORK/database.name" <<'NODE'
const fs = require('fs');
const shell = value => "'" + String(value).replace(/'/g, "'\\''") + "'";
const pgpass = value => String(value).replace(/\\/g, '\\\\').replace(/:/g, '\\:');
try {
  const cfg = JSON.parse(fs.readFileSync(process.argv[2], 'utf8'));
  if (cfg.database !== 'postgres' || !cfg.postgres || typeof cfg.postgres !== 'object') throw new Error();
  const p = cfg.postgres;
  let host = typeof p.host === 'string' && p.host ? p.host : '127.0.0.1';
  let port = Number(p.port || 5432);
  let database = p.database || p.db || '';
  let username = p.username || p.user || '';
  let password = p.password || '';
  let sslmode = p.ssl ? 'require' : 'prefer';
  if (p.uri) {
    const u = new URL(p.uri);
    if (!['postgres:', 'postgresql:'].includes(u.protocol)) throw new Error();
    host = u.hostname;
    port = Number(u.port || 5432);
    database = decodeURIComponent(u.pathname.replace(/^\//, '')) || database;
    username = decodeURIComponent(u.username || username);
    password = decodeURIComponent(u.password || password);
    const requested = u.searchParams.get('sslmode');
    if (requested) sslmode = requested;
  }
  if (!host || /[\r\n\0]/.test(host) || !Number.isInteger(port) || port < 1 || port > 65535 ||
      !database || !username || /[\r\n\0]/.test(database) || /[\r\n\0]/.test(username) ||
      /[\r\n\0]/.test(password) ||
      !['disable','allow','prefer','require','verify-ca','verify-full'].includes(sslmode)) throw new Error();
  const env = [
    'PGHOST=' + shell(host), 'PGPORT=' + shell(port), 'PGDATABASE=' + shell(database),
    'PGUSER=' + shell(username), 'PGPASSFILE=' + shell(process.argv[4]), 'PGSSLMODE=' + shell(sslmode)
  ];
  fs.writeFileSync(process.argv[3], env.join('\n') + '\n', { mode: 0o600, flag: 'wx' });
  fs.writeFileSync(process.argv[4], [pgpass(host), port, pgpass(database), pgpass(username), pgpass(password)].join(':') + '\n',
    { mode: 0o600, flag: 'wx' });
  fs.writeFileSync(process.argv[5], database + '\n', { mode: 0o600, flag: 'wx' });
} catch {
  console.error('לא ניתן לקרוא בבטחה את הגדרות PostgreSQL מתוך config.json.'); process.exit(20);
}
NODE
chmod 600 "$WORK/connection.env" "$WORK/pgpass" "$WORK/database.name"
. "$WORK/connection.env"
export PGHOST PGPORT PGDATABASE PGUSER PGPASSFILE PGSSLMODE
[ -s "$WORK/incoming.dump" ] || { echo 'קובץ הגיבוי שהועלה ריק.' >&2; exit 22; }
pg_restore --list --file="$WORK/preflight.list" "$WORK/incoming.dump" || { echo 'לא ניתן לקרוא את תוכן ארכיון PostgreSQL.' >&2; exit 23; }
grep -F -x -- ";     dbname: $PGDATABASE" "$WORK/preflight.list" >/dev/null || { echo 'שם מסד הנתונים בארכיון אינו תואם למסד שהוגדר ב-NodeBB.' >&2; exit 24; }
awk '!/^;/ && NF { found=1 } END { exit !found }' "$WORK/preflight.list" || { echo 'הארכיון אינו מכיל אובייקטים לשחזור.' >&2; exit 25; }
pg_dump --no-password --format=custom --file="$WORK/rollback.dump" || { echo 'לא ניתן ליצור גיבוי חירום לפני השחזור.' >&2; exit 26; }
[ -s "$WORK/rollback.dump" ] || { echo 'גיבוי החירום ריק; השחזור בוטל.' >&2; exit 27; }
head -c 5 "$WORK/rollback.dump" | grep -q '^PGDMP$' || { echo 'גיבוי החירום אינו בפורמט תקין.' >&2; exit 28; }
KEEP_WORK=1
restore_dump() {
  pg_restore --no-password --clean --if-exists --exit-on-error --single-transaction --no-owner --no-privileges --dbname="$PGDATABASE" "$1"
}
if restore_dump "$WORK/incoming.dump"; then
  KEEP_WORK=0
  echo 'PostgreSQL restore completed.'
else
  echo 'Restore failed; attempting to restore the pre-restore dump.' >&2
  if restore_dump "$WORK/rollback.dump"; then
    KEEP_WORK=0
    echo 'Rollback completed.' >&2
    exit 41
  else
    echo 'CRITICAL: rollback failed; preserve this directory and its rollback dump:' "$WORK" >&2
    exit 42
  fi
fi
""";
    }

    private static string SanitizeError(string error)
    {
        var safe = string.Join(Environment.NewLine,
            error.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(line => !line.Contains("postgres://", StringComparison.OrdinalIgnoreCase)
                    && !line.Contains("postgresql://", StringComparison.OrdinalIgnoreCase)
                    && !line.Contains("password", StringComparison.OrdinalIgnoreCase)
                    && !line.Contains("credential", StringComparison.OrdinalIgnoreCase)).Take(10));
        return string.IsNullOrWhiteSpace(safe) ? "שחזור PostgreSQL נכשל; פרטי חיבור רגישים הוסתרו." : safe;
    }

    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''") + "'";
}
