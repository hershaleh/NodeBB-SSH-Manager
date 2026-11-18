using System.Text;
using System.Text.RegularExpressions;

namespace NodeBB.SSHManager.Services;

/// <summary>
/// Creates a PostgreSQL custom-format dump using credentials stored in a mode-600 pgpass file.
/// </summary>
public sealed class PostgresDatabaseBackupService
{
    private readonly SshServerSession _session;
    public PostgresDatabaseBackupService(SshServerSession session) => _session = session;

    public async Task DownloadBackupAsync(NodeBbInstallation installation, string localDestination,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(installation);
        if (string.IsNullOrWhiteSpace(localDestination))
            throw new ArgumentException("יש לבחור נתיב לשמירת הגיבוי.", nameof(localDestination));
        var work = "/tmp/nodebb-postgres-backup-" + Guid.NewGuid().ToString("N");
        var remoteArchive = work + "/database.dump";
        var partial = localDestination + ".partial-" + Guid.NewGuid().ToString("N");
        DiagnosticLogService.Instance.Info("PostgresBackup", "BackupStarted", "PostgreSQL backup started; credentials are read remotely.", _session.Profile.Name);
        try
        {
            var result = await _session.ExecuteAsync(BuildBackupScript(installation.Path, work), cancellationToken);
            if (result.ExitCode != 0)
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.Error)
                    ? $"יצירת גיבוי PostgreSQL נכשלה (קוד {result.ExitCode}). בדוק pg_dump בשרת."
                    : SanitizeError(result.Error));
            using var sftp = _session.CreateSftpClient();
            await Task.Run(sftp.Connect, cancellationToken);
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true))
            {
                await Task.Run(() => sftp.DownloadFile(remoteArchive, output), cancellationToken);
                await output.FlushAsync(cancellationToken);
            }
            await ValidateDumpAsync(partial, cancellationToken);
            File.Move(partial, localDestination, true);
            DiagnosticLogService.Instance.Info("PostgresBackup", "BackupCompleted", $"PostgreSQL dump downloaded; {new FileInfo(localDestination).Length} bytes.", _session.Profile.Name);
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Instance.Error("PostgresBackup", "BackupFailed", ex, _session.Profile.Name);
            throw;
        }
        finally
        {
            try { if (File.Exists(partial)) File.Delete(partial); } catch { }
            try { await _session.ExecuteAsync($"rm -rf -- {ShellQuote(work)}", CancellationToken.None); }
            catch { DiagnosticLogService.Instance.Warning("PostgresBackup", "RemoteCleanupFailed", "Could not remove temporary PostgreSQL backup directory.", null, _session.Profile.Name); }
        }
    }

    public static async Task ValidateDumpAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path) || new FileInfo(path).Length < 5)
            throw new InvalidDataException("קובץ גיבוי PostgreSQL חסר או קצר מדי.");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
        var header = new byte[5];
        await stream.ReadExactlyAsync(header, cancellationToken);
        if (!header.AsSpan().SequenceEqual(Encoding.ASCII.GetBytes("PGDMP")))
            throw new InvalidDataException("קובץ הגיבוי אינו בפורמט PostgreSQL custom dump.");
    }

    internal static string BuildBackupScript(string installationPath, string workDirectory)
    {
        if (string.IsNullOrWhiteSpace(installationPath) || installationPath.IndexOf('\0') >= 0)
            throw new ArgumentException("נתיב התקנת NodeBB אינו תקין.", nameof(installationPath));
        if (!Regex.IsMatch(workDirectory, @"^/tmp/nodebb-postgres-backup-[a-f0-9]{32}$"))
            throw new ArgumentException("נתיב העבודה הזמני אינו תקין.", nameof(workDirectory));
        return $$"""
set -eu
INSTALLATION={{ShellQuote(installationPath)}}
WORK={{ShellQuote(workDirectory)}}
umask 077
mkdir -m 700 "$WORK"
command -v node >/dev/null 2>&1 || { echo 'Node.js לא נמצא בשרת.' >&2; exit 127; }
command -v pg_dump >/dev/null 2>&1 || { echo 'pg_dump אינו מותקן בשרת.' >&2; exit 127; }
node - "$INSTALLATION/config.json" "$WORK/connection.env" "$WORK/pgpass" <<'NODE'
const fs = require('fs');
const shell = value => "'" + String(value).replace(/'/g, "'\\''") + "'";
const pgpass = value => String(value).replace(/\\/g, '\\\\').replace(/:/g, '\\:').replace(/[\r\n]/g, '');
try {
  const cfg = JSON.parse(fs.readFileSync(process.argv[2], 'utf8'));
  if (cfg.database !== 'postgres' || !cfg.postgres || typeof cfg.postgres !== 'object') throw new Error();
  const p = cfg.postgres;
  let host = typeof p.host === 'string' && p.host ? p.host : '127.0.0.1';
  let port = Number(p.port || 5432);
  let database = p.database || p.db || '';
  let username = p.username || p.user || '';
  let password = p.password || '';
  let ssl = !!p.ssl;
  if (p.uri) {
    const u = new URL(p.uri);
    if (!['postgres:', 'postgresql:'].includes(u.protocol)) throw new Error();
    host = u.hostname;
    port = Number(u.port || 5432);
    database = decodeURIComponent(u.pathname.replace(/^\\//, '')) || database;
    username = decodeURIComponent(u.username || username);
    password = decodeURIComponent(u.password || password);
    ssl = ssl || u.searchParams.get('sslmode') === 'require';
  }
  if (!host || /[\r\n\0]/.test(host) || !Number.isInteger(port) || port < 1 || port > 65535 ||
      !database || !username || /[\r\n\0]/.test(database) || /[\r\n\0]/.test(username)) throw new Error();
  const env = [
    'PGHOST=' + shell(host), 'PGPORT=' + shell(port), 'PGDATABASE=' + shell(database),
    'PGUSER=' + shell(username), 'PGPASSFILE=' + shell(process.argv[4]),
    'PGSSLMODE=' + (ssl ? 'require' : 'prefer')
  ];
  fs.writeFileSync(process.argv[3], env.join('\n') + '\n', { mode: 0o600, flag: 'wx' });
  fs.writeFileSync(process.argv[4], [host, port, database, username, pgpass(password)].join(':') + '\n',
    { mode: 0o600, flag: 'wx' });
} catch {
  console.error('לא ניתן לקרוא בבטחה את הגדרות PostgreSQL מתוך config.json.'); process.exit(20);
}
NODE
chmod 600 "$WORK/connection.env" "$WORK/pgpass"
. "$WORK/connection.env"
export PGHOST PGPORT PGDATABASE PGUSER PGPASSFILE PGSSLMODE
pg_dump --no-password --format=custom --file="$WORK/database.dump"
[ -s "$WORK/database.dump" ] || { echo 'pg_dump לא יצר קובץ גיבוי.' >&2; exit 22; }
""";
    }

    private static string SanitizeError(string error)
    {
        var safe = string.Join(Environment.NewLine,
            error.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(line => !line.Contains("postgres://", StringComparison.OrdinalIgnoreCase)
                    && !line.Contains("postgresql://", StringComparison.OrdinalIgnoreCase)
                    && !line.Contains("password", StringComparison.OrdinalIgnoreCase)
                    && !line.Contains("credential", StringComparison.OrdinalIgnoreCase)).Take(8));
        return string.IsNullOrWhiteSpace(safe) ? "גיבוי PostgreSQL נכשל; פרטי חיבור רגישים הוסתרו." : safe;
    }

    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''") + "'";
}
