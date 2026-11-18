using System.IO.Compression;
using System.Text.RegularExpressions;
using Renci.SshNet;

namespace NodeBB.SSHManager.Services;

public sealed class MongoDatabaseRestoreService
{
    private readonly SshServerSession _session;
    public MongoDatabaseRestoreService(SshServerSession session) => _session = session;

    public static async Task ValidateArchiveAsync(string archivePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(archivePath) || !File.Exists(archivePath))
            throw new FileNotFoundException("קובץ ארכיון MongoDB לא נמצא.", archivePath);
        if (new FileInfo(archivePath).Length < 20) throw new InvalidDataException("קובץ הארכיון קצר מדי.");
        await using var file = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
        var header = new byte[2];
        await file.ReadExactlyAsync(header, cancellationToken);
        if (header[0] != 0x1f || header[1] != 0x8b) throw new InvalidDataException("הקובץ אינו ארכיון gzip תקין.");
        file.Position = 0;
        using var gzip = new GZipStream(file, CompressionMode.Decompress, true);
        var buffer = new byte[131072];
        long expanded = 0;
        int read;
        while ((read = await gzip.ReadAsync(buffer, cancellationToken)) > 0)
        {
            expanded += read;
            if (expanded > 20L * 1024 * 1024 * 1024) throw new InvalidDataException("הארכיון גדול מדי לבדיקה מקומית (מעל 20GB לאחר פתיחה).");
        }
        if (expanded == 0) throw new InvalidDataException("הארכיון ריק.");
    }

    public async Task RestoreAsync(NodeBbInstallation installation, string localArchive, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(installation);
        await ValidateArchiveAsync(localArchive, cancellationToken);
        var work = "/tmp/nodebb-mongo-restore-" + Guid.NewGuid().ToString("N");
        var remoteArchive = work + "/incoming.archive.gz";
        var preserveRollback = false;
        try
        {
            var init = await _session.ExecuteAsync($"umask 077 && mkdir -m 700 -- {ShellQuote(work)}", cancellationToken);
            if (init.ExitCode != 0) throw new InvalidOperationException("לא ניתן ליצור תיקיית עבודה זמנית ומוגנת בשרת.");
            using (var sftp = _session.CreateSftpClient())
            {
                await Task.Run(sftp.Connect, cancellationToken);
                await using var input = new FileStream(localArchive, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
                await Task.Run(() => sftp.UploadFile(input, remoteArchive, false), cancellationToken);
            }

            DiagnosticLogService.Instance.Warning("MongoRestore", "RestoreStarted",
                "MongoDB restore started. A remote rollback dump is created before modifying collections.", null, _session.Profile.Name);
            var result = await _session.ExecuteAsync(BuildRestoreScript(installation.Path, work), cancellationToken);
            if (result.ExitCode == 42)
            {
                preserveRollback = true;
                throw new InvalidOperationException($"השחזור נכשל וגם השחזור לאחור נכשל. קובץ הגיבוי המקדים נשמר בשרת בתיקייה {work}; אין למחוק אותה. נדרשת התערבות מנהל.");
            }
            if (result.ExitCode != 0)
                throw new InvalidOperationException(result.ExitCode == 41
                    ? "השחזור נכשל. המערכת החזירה את מסד הנתונים לגיבוי שנוצר לפני הפעולה."
                    : string.IsNullOrWhiteSpace(result.Error) ? $"שחזור MongoDB נכשל (קוד {result.ExitCode})." : SanitizeError(result.Error));
            DiagnosticLogService.Instance.Info("MongoRestore", "RestoreCompleted",
                "MongoDB restore completed; the pre-restore rollback dump was removed after success.", _session.Profile.Name);
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Instance.Error("MongoRestore", "RestoreFailed", ex, _session.Profile.Name);
            throw;
        }
        finally
        {
            if (!preserveRollback)
            {
                try { await _session.ExecuteAsync($"rm -rf -- {ShellQuote(work)}", CancellationToken.None); }
                catch { DiagnosticLogService.Instance.Warning("MongoRestore", "RemoteCleanupFailed", "Could not remove temporary MongoDB restore directory.", null, _session.Profile.Name); }
            }
        }
    }

    internal static string BuildRestoreScript(string installationPath, string workDirectory)
    {
        if (string.IsNullOrWhiteSpace(installationPath) || installationPath.IndexOf('\0') >= 0)
            throw new ArgumentException("נתיב התקנת NodeBB אינו תקין.", nameof(installationPath));
        if (!Regex.IsMatch(workDirectory, @"^/tmp/nodebb-mongo-restore-[a-f0-9]{32}$"))
            throw new ArgumentException("נתיב העבודה הזמני אינו תקין.", nameof(workDirectory));
        return $$"""
set -eu
INSTALLATION={{ShellQuote(installationPath)}}
WORK={{ShellQuote(workDirectory)}}
KEEP_WORK=0
cleanup() { if [ "$KEEP_WORK" = 0 ]; then rm -rf -- "$WORK"; fi; }
trap cleanup EXIT
command -v node >/dev/null 2>&1 || { echo 'Node.js לא נמצא בשרת.' >&2; exit 127; }
command -v mongodump >/dev/null 2>&1 || { echo 'mongodump אינו מותקן.' >&2; exit 127; }
command -v mongorestore >/dev/null 2>&1 || { echo 'mongorestore אינו מותקן.' >&2; exit 127; }
node - "$INSTALLATION/config.json" "$WORK/mongo.yml" <<'NODE'
const fs = require('fs');
try {
  const cfg = JSON.parse(fs.readFileSync(process.argv[2], 'utf8'));
  if (cfg.database !== 'mongo' || !cfg.mongo || typeof cfg.mongo !== 'object') throw new Error();
  const m = cfg.mongo;
  let uri = typeof m.uri === 'string' && m.uri.trim() ? m.uri.trim() : '';
  if (!uri) {
    const host = typeof m.host === 'string' && m.host.trim() ? m.host.trim() : '127.0.0.1';
    const port = Number(m.port || 27017);
    if (!Number.isInteger(port) || port < 1 || port > 65535) throw new Error();
    const db = typeof m.database === 'string' ? m.database : (typeof m.db === 'string' ? m.db : '');
    const username = m.username || m.user || '';
    const password = m.password || '';
    const auth = username ? encodeURIComponent(username) + ':' + encodeURIComponent(password) + '@' : '';
    const authSource = m.authenticationDatabase || m.authSource || '';
    uri = 'mongodb://' + auth + host + ':' + port + '/' + encodeURIComponent(db);
    if (authSource) uri += '?authSource=' + encodeURIComponent(authSource);
  }
  if (!uri.startsWith('mongodb://') && !uri.startsWith('mongodb+srv://')) throw new Error();
  fs.writeFileSync(process.argv[3], 'uri: ' + JSON.stringify(uri) + '\n', { mode: 0o600, flag: 'wx' });
} catch { console.error('לא ניתן להכין הגדרת חיבור MongoDB מתוך config.json. פרטי החיבור לא יוצגו.'); process.exit(21); }
NODE
[ -s "$WORK/incoming.archive.gz" ] || { echo 'קובץ הגיבוי שהועלה ריק.' >&2; exit 22; }
mongorestore --dryRun --config="$WORK/mongo.yml" --archive="$WORK/incoming.archive.gz" --gzip
mongodump --config="$WORK/mongo.yml" --archive="$WORK/rollback.archive.gz" --gzip
[ -s "$WORK/rollback.archive.gz" ] || { echo 'לא נוצר גיבוי מקדים; השחזור בוטל.' >&2; exit 23; }
if mongorestore --config="$WORK/mongo.yml" --archive="$WORK/incoming.archive.gz" --gzip --drop; then
  echo 'MongoDB restore completed.'
else
  echo 'Restore failed; attempting rollback from the pre-restore dump.' >&2
  if mongorestore --config="$WORK/mongo.yml" --archive="$WORK/rollback.archive.gz" --gzip --drop; then
    echo 'Rollback completed.' >&2
    exit 41
  else
    KEEP_WORK=1
    echo 'CRITICAL: rollback failed; preserve this directory and its rollback archive:' "$WORK" >&2
    exit 42
  fi
fi
""";
    }

    private static string SanitizeError(string error)
    {
        var safe = string.Join(Environment.NewLine, error.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !line.Contains("mongodb://", StringComparison.OrdinalIgnoreCase)
                && !line.Contains("mongodb+srv://", StringComparison.OrdinalIgnoreCase)
                && !line.Contains("password", StringComparison.OrdinalIgnoreCase)
                && !line.Contains("credential", StringComparison.OrdinalIgnoreCase)).Take(8));
        return string.IsNullOrWhiteSpace(safe) ? "שחזור MongoDB נכשל; פרטי חיבור רגישים הוסתרו." : safe;
    }

    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''") + "'";
}
