using System.Formats.Tar;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using Renci.SshNet;

namespace NodeBB.SSHManager.Services;

public sealed record EssentialBackupArchiveInfo(string DatabaseType, bool ContainsUploads);

/// <summary>
/// Restores only the safe configuration/uploads backup format. Full-installation archives are
/// deliberately rejected. The database engine and endpoint must match the live installation.
/// </summary>
public sealed class RemoteBackupRestoreService
{
    private static readonly HashSet<string> OptionalRootFiles = new(StringComparer.Ordinal)
    {
        "package.json", "package-lock.json", "npm-shrinkwrap.json", "yarn.lock", "pnpm-lock.yaml"
    };

    private readonly SshServerSession _session;
    public RemoteBackupRestoreService(SshServerSession session) => _session = session;

    public static async Task<EssentialBackupArchiveInfo> ValidateEssentialArchiveAsync(
        string archivePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(archivePath) || !File.Exists(archivePath))
            throw new FileNotFoundException("קובץ גיבוי הקבצים לא נמצא.", archivePath);
        if (new FileInfo(archivePath).Length < 20)
            throw new InvalidDataException("ארכיון הגיבוי קצר מדי.");

        await using var file = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
        var magic = new byte[2];
        await file.ReadExactlyAsync(magic, cancellationToken);
        if (magic[0] != 0x1f || magic[1] != 0x8b)
            throw new InvalidDataException("הקובץ אינו ארכיון TAR.GZ.");

        file.Position = 0;
        await using var gzip = new GZipStream(file, CompressionMode.Decompress, true);
        await using var reader = new TarReader(gzip, leaveOpen: true);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var configSeen = false;
        var containsUploads = false;
        var databaseType = "";
        long totalExpanded = 0;
        var entryCount = 0;

        TarEntry? entry;
        while ((entry = await reader.GetNextEntryAsync(false, cancellationToken)) is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++entryCount > 250_000)
                throw new InvalidDataException("הארכיון מכיל יותר מדי פריטים.");
            var path = NormalizeArchivePath(entry.Name);
            if ((entry.Mode & 0xE00) != 0)
                throw new InvalidDataException("הארכיון מכיל הרשאות setuid/setgid/sticky שאינן מותרות.");
            if (!IsAllowedEntry(path, entry.EntryType))
                throw new InvalidDataException($"הארכיון מכיל פריט שאינו נתמך: {path}");
            if (!seen.Add(path))
                throw new InvalidDataException("הארכיון מכיל שמות קבצים כפולים.");
            if (path == "public/uploads" || path.StartsWith("public/uploads/", StringComparison.Ordinal))
                containsUploads = true;

            if (entry.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile)
            {
                if (entry.DataStream is null)
                    throw new InvalidDataException("הארכיון מכיל קובץ ללא תוכן קריא.");
                const long maxExpanded = 100L * 1024 * 1024 * 1024;
                if (entry.Length < 0 || entry.Length > maxExpanded - totalExpanded)
                    throw new InvalidDataException("הארכיון גדול מדי לבדיקה (מעל 100GB לאחר פתיחה).");
                totalExpanded += entry.Length;

                if (path == "config.json")
                {
                    if (entry.Length > 8 * 1024 * 1024)
                        throw new InvalidDataException("config.json גדול מדי.");
                    using var configBuffer = new MemoryStream((int)entry.Length);
                    await entry.DataStream.CopyToAsync(configBuffer, cancellationToken);
                    if (configBuffer.Length != entry.Length)
                        throw new InvalidDataException("config.json נקטע בתוך הארכיון.");
                    configBuffer.Position = 0;
                    using var document = JsonDocument.Parse(configBuffer);
                    if (document.RootElement.ValueKind != JsonValueKind.Object ||
                        !document.RootElement.TryGetProperty("database", out var databaseElement) ||
                        databaseElement.ValueKind != JsonValueKind.String)
                        throw new InvalidDataException("config.json בארכיון אינו מכיל סוג מסד נתונים תקין.");
                    databaseType = databaseElement.GetString() ?? "";
                    if (databaseType is not ("mongo" or "postgres" or "redis"))
                        throw new InvalidDataException("הגיבוי מכיל סוג מסד נתונים שאינו נתמך.");
                    configSeen = true;
                }
                else
                {
                    await entry.DataStream.CopyToAsync(Stream.Null, cancellationToken);
                }
            }
            else if (entry.DataStream is not null)
            {
                await entry.DataStream.CopyToAsync(Stream.Null, cancellationToken);
            }
        }

        if (!configSeen)
            throw new InvalidDataException("הארכיון אינו מכיל config.json תקין.");
        var drainBuffer = new byte[131072];
        while (await gzip.ReadAsync(drainBuffer, cancellationToken) > 0) { }
        return new EssentialBackupArchiveInfo(databaseType, containsUploads);
    }

    internal static string NormalizeArchivePath(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.IndexOf('\0') >= 0 || name.Any(char.IsControl) ||
            name.Contains('\\') || name.Contains(':') || name.StartsWith("/", StringComparison.Ordinal))
            throw new InvalidDataException("הארכיון מכיל נתיב לא בטוח.");
        var normalized = name.TrimEnd('/');
        if (normalized.Length == 0)
            throw new InvalidDataException("הארכיון מכיל נתיב ריק.");
        var segments = normalized.Split('/');
        if (segments.Any(segment => segment.Length == 0 || segment is "." or ".."))
            throw new InvalidDataException("הארכיון מכיל נתיב יחסי או מעבר לתיקייה אחרת.");
        return normalized;
    }

    internal static bool IsAllowedEntry(string path, TarEntryType type)
    {
        var regular = type is TarEntryType.RegularFile or TarEntryType.V7RegularFile;
        var directory = type == TarEntryType.Directory;
        if (path == "config.json" || OptionalRootFiles.Contains(path)) return regular;
        if (path == "public" || path == "public/uploads") return directory;
        if (path.StartsWith("public/uploads/", StringComparison.Ordinal)) return regular || directory;
        return false;
    }

    public async Task RestoreAsync(NodeBbInstallation installation, string localArchive,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(installation);
        var archiveInfo = await ValidateEssentialArchiveAsync(localArchive, cancellationToken);
        var work = "/tmp/nodebb-file-restore-" + Guid.NewGuid().ToString("N");
        var remoteArchive = work + "/incoming.tar.gz";
        var scriptStarted = false;

        try
        {
            var init = await _session.ExecuteAsync($"umask 077 && mkdir -m 700 -- {ShellQuote(work)}", cancellationToken);
            if (init.ExitCode != 0)
                throw new InvalidOperationException("לא ניתן ליצור תיקיית עבודה זמנית ומוגנת בשרת.");
            using (var sftp = _session.CreateSftpClient())
            {
                await Task.Run(sftp.Connect, cancellationToken);
                await using var input = new FileStream(localArchive, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
                await Task.Run(() => sftp.UploadFile(input, remoteArchive, false), cancellationToken);
            }

            DiagnosticLogService.Instance.Warning("FileRestore", "RestoreStarted",
                "Configuration/uploads restore started with remote rollback protection.", null, _session.Profile.Name);
            scriptStarted = true;
            var result = await _session.ExecuteAsync(
                BuildRestoreScript(installation.Path, work, archiveInfo.ContainsUploads), cancellationToken);
            if (result.ExitCode == 42)
            {
                var workName = work[(work.LastIndexOf('/') + 1)..];
                var rollbackPath = installation.Path.TrimEnd('/') + "/.nodebb-file-restore-" + workName;
                throw new InvalidOperationException(
                    $"שחזור הקבצים נכשל וגם ההחזרה נכשלה. גיבוי החירום נשמר בשרת בנתיב {rollbackPath}; אין למחוק אותו. נדרשת התערבות מנהל.");
            }
            if (result.ExitCode == 41)
                throw new InvalidOperationException("שחזור הקבצים נכשל. התוכנה החזירה את config.json ואת תיקיית ההעלאות למצבם הקודם.");
            if (result.ExitCode != 0)
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.Error)
                    ? $"שחזור הקבצים נכשל (קוד {result.ExitCode})."
                    : SanitizeError(result.Error));

            DiagnosticLogService.Instance.Info("FileRestore", "RestoreCompleted",
                "NodeBB configuration/uploads restore completed.", _session.Profile.Name);
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Instance.Error("FileRestore", "RestoreFailed", ex, _session.Profile.Name);
            throw;
        }
        finally
        {
            // The remote EXIT trap owns cleanup once the script starts. Do not remove a rollback
            // directory after a timeout or SSH disconnect.
            if (!scriptStarted)
            {
                try { await _session.ExecuteAsync($"rm -rf -- {ShellQuote(work)}", CancellationToken.None); }
                catch
                {
                    DiagnosticLogService.Instance.Warning("FileRestore", "RemoteCleanupFailed",
                        "Could not remove temporary file-restore upload directory.", null, _session.Profile.Name);
                }
            }
        }
    }

    internal static string BuildRestoreScript(string installationPath, string workDirectory, bool restoreUploads)
    {
        if (string.IsNullOrWhiteSpace(installationPath) || installationPath.IndexOf('\0') >= 0)
            throw new ArgumentException("נתיב התקנת NodeBB אינו תקין.", nameof(installationPath));
        if (!Regex.IsMatch(workDirectory, @"^/tmp/nodebb-file-restore-[a-f0-9]{32}$"))
            throw new ArgumentException("נתיב העבודה הזמני אינו תקין.", nameof(workDirectory));

        return $$"""
set -eu
INSTALL={{ShellQuote(installationPath)}}
WORK={{ShellQuote(workDirectory)}}
WORK_NAME="$(basename "$WORK")"
ROLLBACK="$INSTALL/.nodebb-file-restore-$WORK_NAME"
CONFIG_STAGE="$INSTALL/.config-restore-$WORK_NAME"
UPLOADS_STAGE="$INSTALL/public/.uploads-restore-$WORK_NAME"
RESTORE_UPLOADS={{(restoreUploads ? "1" : "0")}}
HAD_UPLOADS=0
CHANGES_STARTED=0
KEEP_WORK=0
ROLLBACK_CREATED=0

rollback_changes() {
  set +e
  ROLLBACK_FAILED=0
  if [ -f "$ROLLBACK/config.json" ]; then
    cp -p "$ROLLBACK/config.json" "$CONFIG_STAGE.rollback" && mv -f "$CONFIG_STAGE.rollback" "$INSTALL/config.json" || ROLLBACK_FAILED=1
  else
    ROLLBACK_FAILED=1
  fi
  if [ "$RESTORE_UPLOADS" = "1" ]; then
    if [ "$HAD_UPLOADS" = "1" ]; then
      if [ -d "$ROLLBACK/uploads" ]; then
        rm -rf -- "$INSTALL/public/uploads" || ROLLBACK_FAILED=1
        mkdir -p "$INSTALL/public" || ROLLBACK_FAILED=1
        mv "$ROLLBACK/uploads" "$INSTALL/public/uploads" || ROLLBACK_FAILED=1
      else
        [ -d "$INSTALL/public/uploads" ] || ROLLBACK_FAILED=1
      fi
    else
      rm -rf -- "$INSTALL/public/uploads" || ROLLBACK_FAILED=1
    fi
    rm -rf -- "$UPLOADS_STAGE"
  fi
  rm -f -- "$CONFIG_STAGE" "$CONFIG_STAGE.rollback"
  if [ "$ROLLBACK_FAILED" = "0" ]; then
    rm -rf -- "$ROLLBACK"
    ROLLBACK_CREATED=0
    CHANGES_STARTED=0
    return 0
  fi
  return 1
}

cleanup() {
  result=$?
  trap - EXIT
  if [ "$CHANGES_STARTED" = "1" ]; then
    if rollback_changes; then
      echo 'Restore failed; original files were restored.' >&2
      result=41
    else
      echo 'CRITICAL: rollback failed. Preserve this rollback directory:' "$ROLLBACK" >&2
      KEEP_WORK=1
      result=42
    fi
  fi
  rm -rf -- "$CONFIG_STAGE" "$CONFIG_STAGE.rollback" "$UPLOADS_STAGE"
  if [ "$CHANGES_STARTED" = "0" ] && [ "$ROLLBACK_CREATED" = "1" ]; then rm -rf -- "$ROLLBACK"; fi
  if [ "$KEEP_WORK" = "0" ]; then rm -rf -- "$WORK"; fi
  exit "$result"
}
trap cleanup EXIT

umask 022
command -v node >/dev/null 2>&1 || { echo 'Node.js לא נמצא בשרת.' >&2; exit 127; }
command -v tar >/dev/null 2>&1 || { echo 'tar אינו מותקן בשרת.' >&2; exit 127; }
[ -s "$WORK/incoming.tar.gz" ] || { echo 'ארכיון הגיבוי שהועלה ריק.' >&2; exit 20; }
mkdir -m 700 "$WORK/extract"
tar -tzf "$WORK/incoming.tar.gz" > "$WORK/archive.list" || { echo 'ארכיון TAR.GZ פגום.' >&2; exit 21; }
tar --no-same-owner --no-same-permissions -xzf "$WORK/incoming.tar.gz" -C "$WORK/extract" || { echo 'לא ניתן לחלץ את ארכיון הגיבוי.' >&2; exit 22; }
[ -f "$WORK/extract/config.json" ] && [ ! -L "$WORK/extract/config.json" ] || { echo 'config.json חסר או אינו קובץ רגיל.' >&2; exit 23; }
if [ "$RESTORE_UPLOADS" = "1" ]; then
  [ -d "$WORK/extract/public/uploads" ] && [ ! -L "$WORK/extract/public" ] && [ ! -L "$WORK/extract/public/uploads" ] || { echo 'תיקיית public/uploads חסרה או אינה בטוחה.' >&2; exit 24; }
fi
if find "$WORK/extract" -type l -print -quit | grep -q .; then
  echo 'הארכיון מכיל קישורים סמליים; השחזור בוטל.' >&2
  exit 25
fi
[ -f "$INSTALL/config.json" ] && [ ! -L "$INSTALL/config.json" ] || { echo 'config.json הקיים חסר או אינו קובץ רגיל.' >&2; exit 26; }
[ ! -L "$INSTALL/public" ] && [ ! -L "$INSTALL/public/uploads" ] || { echo 'תיקיית public או uploads היא קישור סמלי; השחזור בוטל.' >&2; exit 27; }
node - "$INSTALL/config.json" "$WORK/extract/config.json" <<'NODE'
const fs = require('fs');
function identity(config) {
  const type = config.database;
  if (!['mongo', 'postgres', 'redis'].includes(type)) throw new Error();
  const section = config[type];
  if (!section || typeof section !== 'object') throw new Error();
  const defaults = { mongo: 27017, postgres: 5432, redis: 6379 };
  const normalizeHost = value => String(value).replace(/^\[|\]$/g, '').toLowerCase();
  let host = typeof section.host === 'string' && section.host ? normalizeHost(section.host) : '127.0.0.1';
  let port = Number(section.port || defaults[type]);
  let database = section.database || section.db || (type === 'redis' ? 0 : '');
  if (section.uri) {
    const uri = new URL(section.uri);
    if (type === 'mongo' && !['mongodb:', 'mongodb+srv:'].includes(uri.protocol)) throw new Error();
    if (type === 'postgres' && !['postgres:', 'postgresql:'].includes(uri.protocol)) throw new Error();
    if (type === 'redis' && !['redis:', 'rediss:'].includes(uri.protocol)) throw new Error();
    host = normalizeHost(uri.hostname);
    port = Number(uri.port || defaults[type]);
    const pathDatabase = decodeURIComponent(uri.pathname.replace(/^\/+/, ''));
    if (pathDatabase) database = pathDatabase;
  }
  if (!host || !Number.isInteger(port) || port < 1 || port > 65535) throw new Error();
  return JSON.stringify([type, host, port, String(database)]);
}
try {
  const current = JSON.parse(fs.readFileSync(process.argv[2], 'utf8'));
  const incoming = JSON.parse(fs.readFileSync(process.argv[3], 'utf8'));
  if (identity(current) !== identity(incoming)) {
    console.error('הגדרות מסד הנתונים בארכיון שונות מהשרת הנוכחי; השחזור בוטל כדי למנוע חיבור למסד אחר.');
    process.exit(30);
  }
} catch (error) {
  if (error && error.message && error.message.includes('הגדרות מסד')) process.exit(30);
  console.error('לא ניתן לאמת את config.json מול הגדרות השרת הנוכחיות.'); process.exit(31);
}
NODE
[ ! -e "$ROLLBACK" ] || { echo 'תיקיית גיבוי חירום קיימת; הפעולה בוטלה.' >&2; exit 32; }
mkdir -m 700 "$ROLLBACK"
ROLLBACK_CREATED=1
cp -p "$INSTALL/config.json" "$ROLLBACK/config.json"
if [ "$RESTORE_UPLOADS" = "1" ]; then
  mkdir -p "$INSTALL/public"
  cp -a "$WORK/extract/public/uploads" "$UPLOADS_STAGE"
fi
cp -p "$WORK/extract/config.json" "$CONFIG_STAGE"
CHANGES_STARTED=1
if [ "$RESTORE_UPLOADS" = "1" ] && [ -d "$INSTALL/public/uploads" ]; then
  HAD_UPLOADS=1
  mv "$INSTALL/public/uploads" "$ROLLBACK/uploads"
fi
mv -f "$CONFIG_STAGE" "$INSTALL/config.json"
if [ "$RESTORE_UPLOADS" = "1" ]; then
  mv "$UPLOADS_STAGE" "$INSTALL/public/uploads"
fi
CHANGES_STARTED=0
rm -rf -- "$ROLLBACK"
echo 'NodeBB configuration/uploads restore completed.'
""";
    }

    private static string SanitizeError(string error)
    {
        var safe = string.Join(Environment.NewLine,
            error.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(line => !line.Contains("password", StringComparison.OrdinalIgnoreCase)
                    && !line.Contains("credential", StringComparison.OrdinalIgnoreCase)
                    && !line.Contains("mongodb://", StringComparison.OrdinalIgnoreCase)
                    && !line.Contains("postgres://", StringComparison.OrdinalIgnoreCase)
                    && !line.Contains("postgresql://", StringComparison.OrdinalIgnoreCase)
                    && !line.Contains("redis://", StringComparison.OrdinalIgnoreCase)).Take(10));
        return string.IsNullOrWhiteSpace(safe) ? "שחזור קובצי NodeBB נכשל; פרטי גישה רגישים הוסתרו." : safe;
    }

    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''") + "'";
}
