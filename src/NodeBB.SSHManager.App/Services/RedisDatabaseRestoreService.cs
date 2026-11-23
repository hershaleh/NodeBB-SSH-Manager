using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace NodeBB.SSHManager.Services;

/// <summary>
/// Restores a complete Redis RDB snapshot. Since RDB files contain the whole Redis server,
/// automatic restore is restricted to one positively identified local systemd Redis service.
/// </summary>
public sealed class RedisDatabaseRestoreService
{
    private readonly SshServerSession _session;
    public RedisDatabaseRestoreService(SshServerSession session) => _session = session;

    public async Task RestoreAsync(NodeBbInstallation installation, string localRdb, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(installation);
        await RedisDatabaseBackupService.ValidateRdbAsync(localRdb, cancellationToken);
        var work = "/tmp/nodebb-redis-restore-" + Guid.NewGuid().ToString("N");
        var remoteRdb = work + "/incoming.rdb";
        var scriptStarted = false;
        try
        {
            var init = await _session.ExecuteAsync($"umask 077 && mkdir -m 700 -- {ShellQuote(work)}", cancellationToken);
            if (init.ExitCode != 0) throw new InvalidOperationException("לא ניתן ליצור תיקיית עבודה זמנית ומוגנת בשרת.");
            using (var sftp = _session.CreateSftpClient())
            {
                await Task.Run(sftp.Connect, cancellationToken);
                await using var input = new FileStream(localRdb, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
                await Task.Run(() => sftp.UploadFile(input, remoteRdb, false), cancellationToken);
            }

            var hash = await ComputeSha256Async(localRdb, cancellationToken);
            DiagnosticLogService.Instance.Warning("RedisRestore", "RestoreStarted",
                "Full Redis RDB restore started; the existing RDB is preserved before replacement.", null, _session.Profile.Name);
            scriptStarted = true;
            var result = await _session.ExecuteAsync(BuildRestoreScript(installation.Path, work, hash), cancellationToken);
            if (result.ExitCode == 42)
                throw new InvalidOperationException($"שחזור Redis נכשל וגם ההחזרה לאחור נכשלה. קובצי ההתאוששות נשמרו בשרת בתיקייה {work}. אין למחוק אותה; נדרשת התערבות מנהל.");
            if (result.ExitCode == 41)
                throw new InvalidOperationException("שחזור Redis נכשל. המערכת החזירה את קובץ RDB הקודם והפעילה מחדש את Redis.");
            if (result.ExitCode != 0)
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.Error)
                    ? $"שחזור Redis נכשל (קוד {result.ExitCode})."
                    : SanitizeError(result.Error));
            DiagnosticLogService.Instance.Info("RedisRestore", "RestoreCompleted",
                "Full Redis RDB restore completed and Redis answered PING.", _session.Profile.Name);
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Instance.Error("RedisRestore", "RestoreFailed", ex, _session.Profile.Name);
            throw;
        }
        finally
        {
            // After the remote script starts, it owns rollback and cleanup. Never delete
            // a possible recovery copy from the client after timeout or SSH disconnect.
            if (!scriptStarted)
            {
                try { await _session.ExecuteAsync($"rm -rf -- {ShellQuote(work)}", CancellationToken.None); }
                catch { DiagnosticLogService.Instance.Warning("RedisRestore", "RemoteCleanupFailed", "Could not remove the temporary Redis upload directory.", null, _session.Profile.Name); }
            }
        }
    }

    internal static string BuildRestoreScript(string installationPath, string workDirectory, string expectedSha256)
    {
        RemotePathSafety.ValidateNodeBbInstallationPath(installationPath);
        if (!Regex.IsMatch(workDirectory, @"^/tmp/nodebb-redis-restore-[a-f0-9]{32}$"))
            throw new ArgumentException("נתיב העבודה הזמני אינו תקין.", nameof(workDirectory));
        if (!Regex.IsMatch(expectedSha256, @"^[a-f0-9]{64}$"))
            throw new ArgumentException("SHA-256 אינו תקין.", nameof(expectedSha256));

        return $$"""
set -eu
INSTALLATION={{ShellQuote(installationPath)}}
WORK={{ShellQuote(workDirectory)}}
EXPECTED_SHA={{ShellQuote(expectedSha256)}}
KEEP_WORK=0
CHANGES_STARTED=0
SERVICE_UNIT=''
RDB_PATH=''
HAD_RDB=0
SERVICE_IDS=''

redis_cmd() {
  if [ "$REDIS_TLS" = "1" ]; then
    if [ -n "$REDIS_USER" ]; then redis-cli --tls -h "$REDIS_HOST" -p "$REDIS_PORT" --user "$REDIS_USER" "$@"
    else redis-cli --tls -h "$REDIS_HOST" -p "$REDIS_PORT" "$@"; fi
  else
    if [ -n "$REDIS_USER" ]; then redis-cli -h "$REDIS_HOST" -p "$REDIS_PORT" --user "$REDIS_USER" "$@"
    else redis-cli -h "$REDIS_HOST" -p "$REDIS_PORT" "$@"; fi
  fi
}
redis_ping() { [ "$(redis_cmd PING 2>/dev/null || true)" = "PONG" ]; }

cleanup() {
  RESULT=$?
  trap - EXIT
  if [ "$CHANGES_STARTED" = "1" ]; then
    set +e
    ROLLBACK_OK=1
    SERVICE_STOPPED=0
    systemctl stop "$SERVICE_UNIT" >/dev/null 2>&1
    for attempt in $(seq 1 15); do
      if ! systemctl is-active --quiet "$SERVICE_UNIT"; then SERVICE_STOPPED=1; break; fi
      sleep 1
    done
    [ "$SERVICE_STOPPED" = "1" ] || ROLLBACK_OK=0
    # Never replace an RDB file while Redis may still be writing to or loading it.
    if [ "$ROLLBACK_OK" = "1" ]; then
      if [ "$HAD_RDB" = "1" ]; then
        if [ -s "$WORK/original.rdb" ]; then
          RESTORE_STAGE="$RDB_PATH.nodebb-rollback-$(basename "$WORK")"
          cp -p -- "$WORK/original.rdb" "$RESTORE_STAGE" && mv -f -- "$RESTORE_STAGE" "$RDB_PATH" || ROLLBACK_OK=0
        else ROLLBACK_OK=0; fi
      else
        rm -f -- "$RDB_PATH" || ROLLBACK_OK=0
      fi
    fi
    if [ "$ROLLBACK_OK" = "1" ]; then
      systemctl start "$SERVICE_UNIT" >/dev/null 2>&1 || ROLLBACK_OK=0
    fi
    ROLLBACK_READY=0
    if [ "$ROLLBACK_OK" = "1" ]; then
      for attempt in $(seq 1 30); do
        if redis_ping; then ROLLBACK_READY=1; break; fi
        sleep 1
      done
      [ "$ROLLBACK_READY" = "1" ] || ROLLBACK_OK=0
    fi
    if [ "$ROLLBACK_OK" = "1" ]; then
      CHANGES_STARTED=0
      RESULT=41
      echo 'Redis restore failed; previous RDB restored and Redis is responding.' >&2
    else
      KEEP_WORK=1
      RESULT=42
      echo "CRITICAL: Redis rollback failed or the service could not be stopped safely. Preserve recovery directory: $WORK" >&2
      echo "Redis RDB target: $RDB_PATH" >&2
    fi
  fi
  if [ "$KEEP_WORK" = "0" ]; then rm -rf -- "$WORK"; fi
  exit "$RESULT"
}
trap cleanup EXIT

umask 077
[ -s "$WORK/incoming.rdb" ] || { echo 'קובץ RDB שהועלה חסר או ריק.' >&2; exit 20; }
command -v node >/dev/null 2>&1 || { echo 'Node.js לא נמצא בשרת.' >&2; exit 127; }
command -v redis-cli >/dev/null 2>&1 || { echo 'redis-cli אינו מותקן.' >&2; exit 127; }
command -v systemctl >/dev/null 2>&1 || { echo 'שחזור אוטומטי נתמך כרגע רק בשירות systemd מזוהה.' >&2; exit 127; }
command -v ss >/dev/null 2>&1 || { echo 'לא ניתן לאמת את שיוך יציאת Redis לתהליך השירות (ss חסר).' >&2; exit 127; }
ACTUAL_SHA="$(sha256sum "$WORK/incoming.rdb" | awk '{print $1}')"
[ "$ACTUAL_SHA" = "$EXPECTED_SHA" ] || { echo 'אימות SHA-256 לאחר העלאת קובץ RDB נכשל.' >&2; exit 21; }

node - "$INSTALLATION/config.json" "$WORK/redis.env" <<'NODE'
const fs = require('fs');
const shell = value => "'" + String(value).replace(/'/g, "'\\''") + "'";
try {
  const cfg = JSON.parse(fs.readFileSync(process.argv[2], 'utf8'));
  if (cfg.database !== 'redis' || !cfg.redis || typeof cfg.redis !== 'object') throw new Error();
  const r = cfg.redis;
  let host = typeof r.host === 'string' && r.host ? r.host : '127.0.0.1';
  let port = Number(r.port || 6379), username = r.username || r.user || '', password = r.password || '', tls = !!r.tls;
  if (r.uri) {
    const u = new URL(r.uri);
    if (!['redis:', 'rediss:'].includes(u.protocol)) throw new Error();
    host = u.hostname; port = Number(u.port || 6379);
    username = decodeURIComponent(u.username || username);
    password = decodeURIComponent(u.password || password);
    tls = tls || u.protocol === 'rediss:';
  }
  if (!['127.0.0.1', '::1', 'localhost'].includes(host.toLowerCase()) ||
      !Number.isInteger(port) || port < 1 || port > 65535 ||
      /[\r\n\0]/.test(username) || /[\r\n\0]/.test(password)) throw new Error();
  fs.writeFileSync(process.argv[3], [
    'REDIS_HOST=' + shell(host), 'REDIS_PORT=' + shell(port),
    'REDIS_USER=' + shell(username), 'REDISCLI_AUTH=' + shell(password),
    'REDIS_TLS=' + (tls ? '1' : '0')
  ].join('\n') + '\n', {mode: 0o600, flag: 'wx'});
} catch {
  console.error('לא ניתן לאמת בבטחה את הגדרות Redis המקומיות של NodeBB.');
  process.exit(20);
}
NODE
chmod 600 "$WORK/redis.env"
. "$WORK/redis.env"
export REDISCLI_AUTH
redis_ping || { echo 'שרת Redis המקומי אינו זמין או שפרטי הגישה אינם תקינים.' >&2; exit 22; }
ROLE="$(redis_cmd INFO replication 2>/dev/null | tr -d '\r' | sed -n 's/^role://p' | head -n 1)"
[ "$ROLE" = "master" ] || { echo 'השחזור בוטל: שרת Redis אינו master עצמאי.' >&2; exit 23; }
redis_cmd CONFIG GET dir dbfilename > "$WORK/config-values" || { echo 'אין הרשאה לקרוא CONFIG GET dir/dbfilename; השחזור בוטל.' >&2; exit 24; }

node - "$WORK/config-values" "$WORK/target.env" <<'NODE'
const fs = require('fs');
const shell = value => "'" + String(value).replace(/'/g, "'\\''") + "'";
try {
  const lines = fs.readFileSync(process.argv[2], 'utf8').replace(/\r/g, '').trim().split('\n');
  const values = new Map();
  for (let i = 0; i + 1 < lines.length; i += 2) values.set(lines[i], lines[i + 1]);
  const dir = values.get('dir'), file = values.get('dbfilename');
  if (!dir || !dir.startsWith('/') || /[\r\n\0]/.test(dir) ||
      !file || !/^[A-Za-z0-9._-]{1,128}$/.test(file) || file === '.' || file === '..') throw new Error();
  fs.writeFileSync(process.argv[3], 'REDIS_DIR=' + shell(dir) + '\nREDIS_FILE=' + shell(file) + '\n', {mode: 0o600, flag: 'wx'});
} catch {
  console.error('הגדרות ספריית RDB של Redis אינן בטוחות או אינן נתמכות.');
  process.exit(25);
}
NODE
. "$WORK/target.env"
[ -d "$REDIS_DIR" ] && [ ! -L "$REDIS_DIR" ] || { echo 'ספריית RDB אינה תיקייה רגילה או שהיא קישור סמלי.' >&2; exit 26; }
REDIS_DIR="$(realpath -e -- "$REDIS_DIR")"
RDB_PATH="$REDIS_DIR/$REDIS_FILE"
[ ! -L "$RDB_PATH" ] || { echo 'קובץ RDB הנוכחי הוא קישור סמלי; השחזור בוטל.' >&2; exit 27; }

SERVICE_COUNT=0
for candidate in redis-server.service redis.service; do
  if systemctl is-active --quiet "$candidate"; then SERVICE_UNIT="$candidate"; SERVICE_COUNT=$((SERVICE_COUNT + 1)); fi
done
[ "$SERVICE_COUNT" -eq 1 ] || { echo 'לא זוהה שירות systemd יחיד ופעיל בשם redis-server.service או redis.service.' >&2; exit 28; }
MAIN_PID="$(systemctl show -p MainPID --value "$SERVICE_UNIT")"
case "$MAIN_PID" in ''|*[!0-9]*|0) echo 'לא ניתן לזהות PID ראשי של שירות Redis.' >&2; exit 29;; esac
ss -ltnp "sport = :$REDIS_PORT" 2>/dev/null | grep -F "pid=$MAIN_PID," >/dev/null || {
  echo 'לא ניתן לאמת שיציאת Redis שהוגדרה שייכת לתהליך systemd שזוהה.' >&2; exit 30;
}
SERVICE_IDS="$(ps -o uid=,gid= -p "$MAIN_PID" | tr -s ' ' | sed 's/^ //;s/ $//')"
set -- $SERVICE_IDS
[ "$#" -eq 2 ] || { echo 'לא ניתן לזהות בעלות של תהליך Redis.' >&2; exit 31; }
SERVICE_UID="$1"; SERVICE_GID="$2"

if [ -e "$RDB_PATH" ]; then
  [ -f "$RDB_PATH" ] || { echo 'יעד RDB קיים אך אינו קובץ רגיל.' >&2; exit 32; }
  cp -p -- "$RDB_PATH" "$WORK/original.rdb"
  HAD_RDB=1
fi

# Mark the operation before requesting stop: systemctl may return an error after
# partially stopping the unit. The EXIT trap must then restore a known state.
CHANGES_STARTED=1
systemctl stop "$SERVICE_UNIT" || { echo 'לא ניתן לאמת עצירה תקינה של שירות Redis; מנגנון ההתאוששות ינסה להחזיר את המצב הקודם.' >&2; exit 33; }
for attempt in $(seq 1 15); do
  if ! kill -0 "$MAIN_PID" 2>/dev/null; then break; fi
  sleep 1
done
if kill -0 "$MAIN_PID" 2>/dev/null; then
  echo 'תהליך Redis עדיין פועל לאחר בקשת עצירה; לא הוחלף קובץ RDB.' >&2
  exit 34
fi

STAGE="$REDIS_DIR/.nodebb-restore-$(basename "$WORK").rdb"
[ ! -e "$STAGE" ] || { echo 'קובץ staging קיים; השחזור בוטל.' >&2; exit 35; }
cp -- "$WORK/incoming.rdb" "$STAGE"
chown "$SERVICE_UID:$SERVICE_GID" "$STAGE"
if [ "$HAD_RDB" = "1" ]; then chmod --reference="$WORK/original.rdb" "$STAGE"; else chmod 600 "$STAGE"; fi
mv -f -- "$STAGE" "$RDB_PATH"
systemctl start "$SERVICE_UNIT" || { echo 'שירות Redis לא עלה לאחר התקנת קובץ RDB.' >&2; exit 36; }

READY=0
for attempt in $(seq 1 30); do
  if redis_ping; then READY=1; break; fi
  sleep 1
done
[ "$READY" = "1" ] || { echo 'Redis לא השיב PING לאחר השחזור.' >&2; exit 37; }
CHANGES_STARTED=0
echo 'REDIS_FULL_RESTORE_OK'
""";
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
    }

    private static string SanitizeError(string error)
    {
        var redacted = DiagnosticLogService.Redact(error);
        var safe = string.Join(Environment.NewLine,
            redacted.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(line => !line.Contains("password", StringComparison.OrdinalIgnoreCase)
                    && !line.Contains("credential", StringComparison.OrdinalIgnoreCase)
                    && !line.Contains("redis://", StringComparison.OrdinalIgnoreCase)).Take(8));
        return string.IsNullOrWhiteSpace(safe) ? "שחזור Redis נכשל; פרטי גישה רגישים הוסתרו." : safe;
    }

    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''") + "'";
}
