using System.Text;
using System.Text.RegularExpressions;

namespace NodeBB.SSHManager.Services;

/// <summary>
/// Creates a Redis RDB snapshot from the Redis instance configured by NodeBB.
/// Redis RDB snapshots contain the entire Redis server, not a single logical database.
/// </summary>
public sealed class RedisDatabaseBackupService
{
    private readonly SshServerSession _session;
    public RedisDatabaseBackupService(SshServerSession session) => _session = session;

    public async Task DownloadBackupAsync(NodeBbInstallation installation, string localDestination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(installation);
        if (string.IsNullOrWhiteSpace(localDestination)) throw new ArgumentException("יש לבחור נתיב לשמירת הגיבוי.", nameof(localDestination));
        var work = "/tmp/nodebb-redis-backup-" + Guid.NewGuid().ToString("N");
        var remoteArchive = work + "/redis.rdb";
        var partial = localDestination + ".partial-" + Guid.NewGuid().ToString("N");
        DiagnosticLogService.Instance.Info("RedisBackup", "BackupStarted", "Redis RDB snapshot started; credentials are read remotely.", _session.Profile.Name);
        try
        {
            var result = await _session.ExecuteAsync(BuildBackupScript(installation.Path, work), cancellationToken);
            if (result.ExitCode != 0)
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.Error)
                    ? $"יצירת גיבוי Redis נכשלה (קוד {result.ExitCode}). בדוק redis-cli והגדרות Redis בשרת."
                    : SanitizeError(result.Error));
            using var sftp = _session.CreateSftpClient();
            await Task.Run(sftp.Connect, cancellationToken);
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true))
            {
                await Task.Run(() => sftp.DownloadFile(remoteArchive, output), cancellationToken);
                await output.FlushAsync(cancellationToken);
            }
            await ValidateRdbAsync(partial, cancellationToken);
            File.Move(partial, localDestination, true);
            DiagnosticLogService.Instance.Info("RedisBackup", "BackupCompleted", $"Redis RDB snapshot downloaded; {new FileInfo(localDestination).Length} bytes.", _session.Profile.Name);
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Instance.Error("RedisBackup", "BackupFailed", ex, _session.Profile.Name);
            throw;
        }
        finally
        {
            try { if (File.Exists(partial)) File.Delete(partial); } catch { }
            try { await _session.ExecuteAsync($"rm -rf -- {ShellQuote(work)}", CancellationToken.None); }
            catch { DiagnosticLogService.Instance.Warning("RedisBackup", "RemoteCleanupFailed", "Could not remove temporary Redis backup directory.", null, _session.Profile.Name); }
        }
    }

    public static async Task ValidateRdbAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("קובץ RDB חסר.", path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
        if (stream.Length < 18)
            throw new InvalidDataException("קובץ RDB קצר מדי ואינו מכיל כותרת, סמן סיום ו-checksum.");

        var header = new byte[5];
        await stream.ReadExactlyAsync(header, cancellationToken);
        if (!header.AsSpan().SequenceEqual(Encoding.ASCII.GetBytes("REDIS")))
            throw new InvalidDataException("קובץ הגיבוי אינו בפורמט Redis RDB.");
        var version = new byte[4];
        await stream.ReadExactlyAsync(version, cancellationToken);
        if (!version.All(b => b is >= (byte)'0' and <= (byte)'9'))
            throw new InvalidDataException("כותרת גרסת Redis RDB אינה תקינה.");

        // Redis stores an EOF opcode immediately before its 8-byte little-endian CRC64.
        // A zero checksum is valid for Redis configurations that explicitly disable it.
        var checksumOffset = stream.Length - sizeof(ulong);
        stream.Position = checksumOffset - 1;
        var eof = stream.ReadByte();
        if (eof != 0xFF)
            throw new InvalidDataException("קובץ RDB אינו מסתיים בסמן EOF תקין.");

        stream.Position = checksumOffset;
        var checksumBytes = new byte[sizeof(ulong)];
        await stream.ReadExactlyAsync(checksumBytes, cancellationToken);
        var storedChecksum = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(checksumBytes);
        if (storedChecksum == 0) return;

        stream.Position = 0;
        var buffer = new byte[131072];
        long remaining = checksumOffset;
        ulong calculatedChecksum = 0;
        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var requested = (int)Math.Min(buffer.Length, remaining);
            var read = await stream.ReadAsync(buffer.AsMemory(0, requested), cancellationToken);
            if (read == 0) throw new EndOfStreamException("קובץ RDB נקטע במהלך בדיקת checksum.");
            calculatedChecksum = ComputeRedisCrc64(buffer.AsSpan(0, read), calculatedChecksum);
            remaining -= read;
        }

        if (calculatedChecksum != storedChecksum)
            throw new InvalidDataException("בדיקת checksum של קובץ Redis RDB נכשלה; הגיבוי פגום או השתנה.");
    }

    internal static ulong ComputeRedisCrc64(ReadOnlySpan<byte> data, ulong crc = 0)
    {
        const ulong reflectedPolynomial = 0x95AC9329AC4BC9B5UL; // CRC-64/Jones used by Redis
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ reflectedPolynomial : crc >> 1;
        }
        return crc;
    }

    internal static string BuildBackupScript(string installationPath, string workDirectory)
    {
        RemotePathSafety.ValidateNodeBbInstallationPath(installationPath);
        if (!Regex.IsMatch(workDirectory, @"^/tmp/nodebb-redis-backup-[a-f0-9]{32}$"))
            throw new ArgumentException("נתיב העבודה הזמני אינו תקין.", nameof(workDirectory));
        return $$"""
set -eu
INSTALLATION={{ShellQuote(installationPath)}}
WORK={{ShellQuote(workDirectory)}}
umask 077
mkdir -m 700 "$WORK"
command -v node >/dev/null 2>&1 || { echo 'Node.js לא נמצא בשרת.' >&2; exit 127; }
command -v redis-cli >/dev/null 2>&1 || { echo 'redis-cli אינו מותקן בשרת.' >&2; exit 127; }
node - "$INSTALLATION/config.json" "$WORK/redis.env" <<'NODE'
const fs = require('fs');
const shell = value => "'" + String(value).replace(/'/g, "'\\''") + "'";
try {
  const cfg = JSON.parse(fs.readFileSync(process.argv[2], 'utf8'));
  if (cfg.database !== 'redis' || !cfg.redis || typeof cfg.redis !== 'object') {
    console.error('התקנת NodeBB אינה מוגדרת להשתמש ב-Redis.'); process.exit(20);
  }
  const r = cfg.redis;
  let host = typeof r.host === 'string' && r.host ? r.host : '127.0.0.1';
  let port = Number(r.port || 6379);
  let username = r.username || r.user || '';
  let password = r.password || '';
  let tls = !!r.tls;
  if (r.uri) {
    const u = new URL(r.uri);
    if (!['redis:', 'rediss:'].includes(u.protocol)) throw new Error();
    host = u.hostname;
    port = Number(u.port || 6379);
    username = decodeURIComponent(u.username || username);
    password = decodeURIComponent(u.password || password);
    tls = tls || u.protocol === 'rediss:';
  }
  if (!host || /[\r\n\0]/.test(host) || !Number.isInteger(port) || port < 1 || port > 65535) throw new Error();
  const lines = [
    'REDIS_HOST=' + shell(host),
    'REDIS_PORT=' + shell(port),
    'REDIS_USER=' + shell(username),
    'REDISCLI_AUTH=' + shell(password),
    'REDIS_TLS=' + (tls ? '1' : '0')
  ];
  fs.writeFileSync(process.argv[3], lines.join('\n') + '\n', { mode: 0o600, flag: 'wx' });
} catch {
  console.error('לא ניתן לקרוא בבטחה את הגדרות Redis מתוך config.json.'); process.exit(21);
}
NODE
chmod 600 "$WORK/redis.env"
. "$WORK/redis.env"
export REDISCLI_AUTH
ARGS=""
if [ -n "$REDIS_USER" ]; then ARGS="--user $REDIS_USER"; fi
if [ "$REDIS_TLS" = "1" ]; then
  if [ -n "$REDIS_USER" ]; then
    redis-cli --tls -h "$REDIS_HOST" -p "$REDIS_PORT" --user "$REDIS_USER" --rdb "$WORK/redis.rdb"
  else
    redis-cli --tls -h "$REDIS_HOST" -p "$REDIS_PORT" --rdb "$WORK/redis.rdb"
  fi
else
  if [ -n "$REDIS_USER" ]; then
    redis-cli -h "$REDIS_HOST" -p "$REDIS_PORT" --user "$REDIS_USER" --rdb "$WORK/redis.rdb"
  else
    redis-cli -h "$REDIS_HOST" -p "$REDIS_PORT" --rdb "$WORK/redis.rdb"
  fi
fi
[ -s "$WORK/redis.rdb" ] || { echo 'redis-cli לא יצר קובץ RDB.' >&2; exit 22; }
head -c 5 "$WORK/redis.rdb" | grep -q '^REDIS$' || { echo 'קובץ RDB שנוצר אינו תקין.' >&2; exit 23; }
""";
    }

    private static string SanitizeError(string error)
    {
        var safe = string.Join(Environment.NewLine, error.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !line.Contains("redis://", StringComparison.OrdinalIgnoreCase)
                && !line.Contains("password", StringComparison.OrdinalIgnoreCase)
                && !line.Contains("credential", StringComparison.OrdinalIgnoreCase)).Take(8));
        return string.IsNullOrWhiteSpace(safe) ? "גיבוי Redis נכשל; פרטי חיבור רגישים הוסתרו." : safe;
    }

    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''") + "'";
}
