namespace NodeBB.SSHManager.Services;
public sealed class MongoDatabaseBackupService
{
    private readonly SshServerSession _session;
    public MongoDatabaseBackupService(SshServerSession session) => _session = session;
    public async Task DownloadBackupAsync(NodeBbInstallation installation, string localDestination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(installation);
        if (string.IsNullOrWhiteSpace(localDestination)) throw new ArgumentException("יש לבחור נתיב לשמירת הגיבוי.", nameof(localDestination));
        var work = "/tmp/nodebb-mongo-backup-" + Guid.NewGuid().ToString("N");
        var archive = work + "/database.archive.gz";
        var partial = localDestination + ".partial-" + Guid.NewGuid().ToString("N");
        DiagnosticLogService.Instance.Info("MongoBackup", "BackupStarted", "MongoDB backup started; credentials are read remotely.", _session.Profile.Name);
        try
        {
            var result = await _session.ExecuteAsync(BuildBackupScript(installation.Path, work), cancellationToken);
            if (result.ExitCode != 0) throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.Error) ? $"יצירת גיבוי MongoDB נכשלה (קוד {result.ExitCode}). בדוק mongodump בשרת." : SanitizeError(result.Error));
            using var sftp = _session.CreateSftpClient();
            await Task.Run(sftp.Connect, cancellationToken);
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true))
            {
                await Task.Run(() => sftp.DownloadFile(archive, output), cancellationToken);
                await output.FlushAsync(cancellationToken);
            }
            if (new FileInfo(partial).Length == 0) throw new InvalidDataException("קובץ גיבוי MongoDB שהתקבל ריק.");
            File.Move(partial, localDestination, true);
            DiagnosticLogService.Instance.Info("MongoBackup", "BackupCompleted", $"MongoDB archive downloaded; {new FileInfo(localDestination).Length} bytes.", _session.Profile.Name);
        }
        catch (Exception ex) { DiagnosticLogService.Instance.Error("MongoBackup", "BackupFailed", ex, _session.Profile.Name); throw; }
        finally
        {
            try { if (File.Exists(partial)) File.Delete(partial); } catch { }
            try { await _session.ExecuteAsync($"rm -rf -- {ShellQuote(work)}", CancellationToken.None); }
            catch { DiagnosticLogService.Instance.Warning("MongoBackup", "RemoteCleanupFailed", "Could not remove temporary MongoDB backup directory.", null, _session.Profile.Name); }
        }
    }
    internal static string BuildBackupScript(string installationPath, string workDirectory)
    {
        if (string.IsNullOrWhiteSpace(installationPath) || installationPath.IndexOf('\0') >= 0) throw new ArgumentException("נתיב התקנת NodeBB אינו תקין.", nameof(installationPath));
        if (!System.Text.RegularExpressions.Regex.IsMatch(workDirectory, @"^/tmp/nodebb-mongo-backup-[a-f0-9]{32}$")) throw new ArgumentException("נתיב העבודה הזמני אינו תקין.", nameof(workDirectory));
        return $$"""
set -eu
INSTALLATION={{ShellQuote(installationPath)}}
WORK={{ShellQuote(workDirectory)}}
umask 077
mkdir -m 700 "$WORK"
command -v node >/dev/null 2>&1 || { echo 'Node.js לא נמצא בשרת.' >&2; exit 127; }
command -v mongodump >/dev/null 2>&1 || { echo 'mongodump אינו מותקן. נדרשת התקנת MongoDB Database Tools.' >&2; exit 127; }
node - "$INSTALLATION/config.json" "$WORK/mongo.yml" <<'NODE'
const fs = require('fs');
try {
  const cfg = JSON.parse(fs.readFileSync(process.argv[2], 'utf8'));
  if (cfg.database !== 'mongo' || !cfg.mongo || typeof cfg.mongo !== 'object') { console.error('התקנת NodeBB אינה מוגדרת להשתמש ב-MongoDB.'); process.exit(20); }
  const m = cfg.mongo;
  let uri = typeof m.uri === 'string' && m.uri.trim() ? m.uri.trim() : '';
  if (!uri) {
    const host = typeof m.host === 'string' && m.host.trim() ? m.host.trim() : '127.0.0.1';
    const port = Number(m.port || 27017);
    if (!Number.isInteger(port) || port < 1 || port > 65535) throw new Error();
    const username = m.username || m.user || '';
    const password = m.password || '';
    const auth = username ? encodeURIComponent(username) + ':' + encodeURIComponent(password) + '@' : '';
    const authSource = m.authenticationDatabase || m.authSource || '';
    uri = 'mongodb://' + auth + host + ':' + port + '/';
    if (authSource) uri += '?authSource=' + encodeURIComponent(authSource);
  }
  if (!uri.startsWith('mongodb://') && !uri.startsWith('mongodb+srv://')) throw new Error();
  const parsed = new URL(uri);
  const uriDatabase = parsed.pathname.replace(/^\/+/, '');
  const db = uriDatabase ? decodeURIComponent(uriDatabase) : (typeof m.database === 'string' ? m.database : (typeof m.db === 'string' ? m.db : ''));
  if (!db || /[\r\n\0]/.test(db)) throw new Error();
  parsed.pathname = '/' + encodeURIComponent(db);
  uri = parsed.toString();
  fs.writeFileSync(process.argv[3], 'uri: ' + JSON.stringify(uri) + '\n', { mode: 0o600, flag: 'wx' });
  fs.writeFileSync(process.argv[3] + '.db', db + '\n', { mode: 0o600, flag: 'wx' });
NODE
mongodump --config="$WORK/mongo.yml" --archive="$WORK/database.archive.gz" --gzip
[ -s "$WORK/database.archive.gz" ] || { echo 'mongodump לא יצר קובץ גיבוי.' >&2; exit 22; }
""";
    }
    private static string SanitizeError(string error)
    {
        var safe = string.Join(Environment.NewLine, error.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !line.Contains("mongodb://", StringComparison.OrdinalIgnoreCase) && !line.Contains("mongodb+srv://", StringComparison.OrdinalIgnoreCase)
                && !line.Contains("password", StringComparison.OrdinalIgnoreCase) && !line.Contains("credential", StringComparison.OrdinalIgnoreCase)).Take(8));
        return string.IsNullOrWhiteSpace(safe) ? "גיבוי MongoDB נכשל; פרטי חיבור רגישים הוסתרו." : safe;
    }
    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''") + "'";
}