using System.Formats.Tar;
using System.IO.Compression;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Renci.SshNet;

namespace NodeBB.SSHManager.Services;

public sealed record EssentialBackupArchiveInfo(string DatabaseType, bool ContainsUploads);
public sealed record FullInstallationArchiveInfo(int EntryCount, long ExpandedBytes);

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
            if ((entry.Mode & (UnixFileMode.SetUser | UnixFileMode.SetGroup | UnixFileMode.StickyBit)) != UnixFileMode.None)
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
                    var entryBuffer = new byte[131072];
                    long entryBytes = 0;
                    int read;
                    while ((read = await entry.DataStream.ReadAsync(entryBuffer, cancellationToken)) > 0)
                        entryBytes += read;
                    if (entryBytes != entry.Length)
                        throw new InvalidDataException($"קובץ בארכיון נקטע: {path}");
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

    /// <summary>
    /// Validates a full-installation archive before any remote staging or replacement.
    /// Only regular files and directories are accepted; links and special files are rejected.
    /// </summary>
    public static async Task<FullInstallationArchiveInfo> ValidateFullInstallationArchiveAsync(
        string archivePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(archivePath) || !File.Exists(archivePath))
            throw new FileNotFoundException("קובץ גיבוי ההתקנה המלאה לא נמצא.", archivePath);
        if (new FileInfo(archivePath).Length < 20)
            throw new InvalidDataException("ארכיון ההתקנה קצר מדי.");

        await using var file = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
        var magic = new byte[2];
        await file.ReadExactlyAsync(magic, cancellationToken);
        if (magic[0] != 0x1f || magic[1] != 0x8b)
            throw new InvalidDataException("הקובץ אינו ארכיון TAR.GZ.");

        file.Position = 0;
        try
        {
            await using var gzip = new GZipStream(file, CompressionMode.Decompress, true);
            await using var reader = new TarReader(gzip, leaveOpen: true);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var filePaths = new HashSet<string>(StringComparer.Ordinal);
            var directoryPaths = new HashSet<string>(StringComparer.Ordinal);
            var configSeen = false;
            var packageSeen = false;
            var nodeBbCliSeen = false;
            var entryCount = 0;
            long expandedBytes = 0;
            TarEntry? entry;
            while ((entry = await reader.GetNextEntryAsync(false, cancellationToken)) is not null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++entryCount > 2_000_000)
                    throw new InvalidDataException("ארכיון ההתקנה מכיל מספר חריג של פריטים.");

                var path = NormalizeFullArchivePath(entry.Name);
                if (path.Length == 0)
                {
                    if (entry.EntryType != TarEntryType.Directory)
                        throw new InvalidDataException("רשומת השורש בארכיון חייבת להיות תיקייה.");
                    continue;
                }

                if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.Directory))
                    throw new InvalidDataException($"הארכיון מכיל קישור או קובץ מיוחד שאינו נתמך: {path}");
                if ((entry.Mode & (UnixFileMode.SetUser | UnixFileMode.SetGroup | UnixFileMode.StickyBit)) != UnixFileMode.None)
                    throw new InvalidDataException($"הארכיון מכיל הרשאות מסוכנות: {path}");
                if (!seen.Add(path))
                    throw new InvalidDataException($"הארכיון מכיל נתיב כפול: {path}");

                var segments = path.Split('/');
                for (var segmentIndex = 1; segmentIndex < segments.Length; segmentIndex++)
                {
                    var parentPath = string.Join('/', segments.Take(segmentIndex));
                    if (filePaths.Contains(parentPath))
                        throw new InvalidDataException($"הארכיון מכיל קובץ שהוא גם תיקיית אב: {parentPath}");
                    directoryPaths.Add(parentPath);
                }

                if (entry.EntryType == TarEntryType.Directory)
                {
                    if (filePaths.Contains(path))
                        throw new InvalidDataException($"הארכיון מכיל נתיב שהוא גם קובץ וגם תיקייה: {path}");
                    directoryPaths.Add(path);
                }
                else
                {
                    if (directoryPaths.Contains(path))
                        throw new InvalidDataException($"הארכיון מכיל נתיב שהוא גם תיקייה וגם קובץ: {path}");
                    filePaths.Add(path);
                }

                if (entry.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile)
                {
                    if (entry.DataStream is null)
                        throw new InvalidDataException($"קובץ בארכיון אינו מכיל נתונים: {path}");
                    if (entry.Length < 0 || entry.Length > 100L * 1024 * 1024 * 1024 - expandedBytes)
                        throw new InvalidDataException("גודל ההתקנה לאחר חילוץ חורג מהמגבלה של 100GB.");
                    expandedBytes += entry.Length;

                    if (path == "nodebb")
                    {
                        nodeBbCliSeen = true;
                    }

                    if (path is "config.json" or "package.json")
                    {
                        if (entry.Length > 8 * 1024 * 1024)
                            throw new InvalidDataException($"קובץ השורש גדול מדי: {path}");
                        using var content = new MemoryStream((int)entry.Length);
                        await entry.DataStream.CopyToAsync(content, cancellationToken);
                        if (content.Length != entry.Length)
                            throw new InvalidDataException($"קובץ השורש נקטע: {path}");
                        content.Position = 0;
                        using var document = await JsonDocument.ParseAsync(content, cancellationToken: cancellationToken);
                        if (document.RootElement.ValueKind != JsonValueKind.Object)
                            throw new InvalidDataException($"קובץ {path} חייב להכיל אובייקט JSON.");
                        if (path == "config.json")
                        {
                            if (!document.RootElement.TryGetProperty("database", out var database) ||
                                database.ValueKind != JsonValueKind.String ||
                                database.GetString() is not ("mongo" or "postgres" or "redis"))
                                throw new InvalidDataException("config.json בארכיון אינו מכיל סוג מסד נתונים נתמך.");
                            configSeen = true;
                        }
                        else
                        {
                            if (!document.RootElement.TryGetProperty("name", out var packageName) ||
                                packageName.ValueKind != JsonValueKind.String ||
                                !string.Equals(packageName.GetString(), "nodebb", StringComparison.OrdinalIgnoreCase))
                                throw new InvalidDataException("package.json אינו מזוהה כחבילת NodeBB.");
                            packageSeen = true;
                        }
                    }
                    else
                    {
                        await entry.DataStream.CopyToAsync(Stream.Null, cancellationToken);
                    }
                }
            }

            var drain = new byte[131072];
            while (await gzip.ReadAsync(drain, cancellationToken) > 0)
                cancellationToken.ThrowIfCancellationRequested();

            if (!configSeen || !packageSeen || !nodeBbCliSeen)
                throw new InvalidDataException("גיבוי התקנה מלאה חייב לכלול config.json, package.json וקובץ nodebb בתיקיית השורש.");
            return new FullInstallationArchiveInfo(entryCount, expandedBytes);
        }
        catch (EndOfStreamException ex)
        {
            throw new InvalidDataException("ארכיון ההתקנה נקטע או שאינו מכיל מבנה TAR שלם.", ex);
        }
        catch (InvalidDataException) { throw; }
        catch (Exception ex) when (ex is IOException or JsonException or FormatException)
        {
            throw new InvalidDataException("לא ניתן לקרוא או לאמת את ארכיון ההתקנה.", ex);
        }
    }

    internal static string NormalizeFullArchivePath(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.IndexOf((char)0) >= 0 || name.Any(char.IsControl) ||
            name.Contains((char)92) || name.Contains(':') || name.StartsWith("/", StringComparison.Ordinal))
            throw new InvalidDataException("הארכיון מכיל נתיב לא בטוח.");

        var normalized = name;
        while (normalized.StartsWith("./", StringComparison.Ordinal))
            normalized = normalized[2..];
        normalized = normalized.TrimEnd('/');
        if (normalized.Length == 0 || normalized == ".")
            return string.Empty;

        var segments = normalized.Split('/');
        if (segments.Any(segment => segment.Length == 0 || segment is "." or ".."))
            throw new InvalidDataException("הארכיון מכיל נתיב יחסי או מעבר לתיקייה אחרת.");
        return normalized;
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


    /// <summary>
    /// Replaces the NodeBB installation tree from a previously validated archive.
    /// The database is never restored; its endpoint must match the live installation.
    /// The old directory is retained until the restored NodeBB starts successfully.
    /// </summary>
    public async Task RestoreFullInstallationAsync(
        NodeBbInstallation installation, string localArchive, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(installation);
        RemotePathSafety.ValidateNodeBbInstallationPath(installation.Path);
        var archiveInfo = await ValidateFullInstallationArchiveAsync(localArchive, cancellationToken);
        var work = "/tmp/nodebb-full-restore-" + Guid.NewGuid().ToString("N");
        var remoteArchive = work + "/incoming.tar.gz";
        var hash = await ComputeSha256Async(localArchive, cancellationToken);
        var workCreated = false;
        var scriptStarted = false;
        var stopped = false;
        var switched = false;
        var restartAttempted = false;
        var recoveryRequired = false;
        NodeBbRuntimeInfo? runtime = null;
        var runtimeService = new NodeBbRuntimeService(_session);
        var rollbackPath = GetFullRestoreRollbackPath(installation.Path, work);

        try
        {
            var init = await _session.ExecuteAsync($"umask 077 && mkdir -m 700 -- {ShellQuote(work)}", cancellationToken);
            if (init.ExitCode != 0)
                throw new InvalidOperationException("לא ניתן ליצור תיקיית עבודה זמנית ומוגנת בשרת.");
            workCreated = true;

            using (var sftp = _session.CreateSftpClient())
            {
                await Task.Run(sftp.Connect, cancellationToken);
                try
                {
                    await using var input = new FileStream(localArchive, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
                    await Task.Run(() => sftp.UploadFile(input, remoteArchive, false), cancellationToken);
                }
                finally
                {
                    if (sftp.IsConnected) sftp.Disconnect();
                }
            }

            var remoteHash = await _session.ExecuteAsync(
                $"sha256sum -- {ShellQuote(remoteArchive)} | awk '{{print $1}}'", cancellationToken);
            if (remoteHash.ExitCode != 0 ||
                !string.Equals(remoteHash.Output.Trim(), hash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("אימות SHA-256 של הארכיון לאחר העלאה לשרת נכשל; השחזור בוטל.");

            var preflight = await _session.ExecuteAsync(
                BuildFullInstallationPreflightScript(installation.Path, work, hash, archiveInfo.ExpandedBytes),
                cancellationToken);
            if (preflight.ExitCode != 0)
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(preflight.Error)
                    ? $"בדיקות ההכנה לשחזור נכשלו (קוד {preflight.ExitCode}); NodeBB לא נעצר ולא שונו קבצים."
                    : SanitizeError(preflight.Error));

            runtime = await runtimeService.DetectAsync(installation, cancellationToken);
            var stop = await runtimeService.ExecuteLifecycleAsync(installation, runtime, "stop", cancellationToken);
            if (stop.ExitCode != 0)
                throw new InvalidOperationException("לא ניתן לעצור את NodeBB באופן בטוח. לא בוצע שינוי בקבצי ההתקנה.");

            stopped = true;
            DiagnosticLogService.Instance.Warning("FullRestore", "RestoreStarted",
                "Full NodeBB installation restore started. Database contents are not included.", null, _session.Profile.Name);

            scriptStarted = true;
            var restore = await _session.ExecuteAsync(
                BuildFullInstallationRestoreScript(installation.Path, work, hash, archiveInfo.ExpandedBytes),
                cancellationToken);
            if (restore.ExitCode == 42)
            {
                recoveryRequired = true;
                throw new InvalidOperationException(
                    $"החלפת ההתקנה נכשלה והחזרת העותק המקורי נתקלה בתקלה. אין להפעיל שוב שחזור או למחוק קבצים. עותק החירום נמצא בנתיב {rollbackPath}.");
            }
            if (restore.ExitCode != 0)
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(restore.Error)
                    ? $"הכנת ההתקנה והחלפתה נכשלו (קוד {restore.ExitCode}); ההתקנה המקורית נשמרה."
                    : SanitizeError(restore.Error));

            switched = true;
            restartAttempted = true;
            var startNew = await runtimeService.ExecuteLifecycleAsync(installation, runtime, "start", cancellationToken);
            if (startNew.ExitCode != 0)
            {
                var rollback = await _session.ExecuteAsync(
                    BuildFullInstallationRollbackScript(installation.Path, work), CancellationToken.None);
                if (rollback.ExitCode != 0)
                {
                    recoveryRequired = true;
                    throw new InvalidOperationException(
                        $"ההתקנה החדשה לא עלתה, וגם ההחזרה האוטומטית נכשלה. עותק ההתקנה המקורית נמצא בנתיב {rollbackPath}; אין למחוק אותו. נדרשת התערבות מנהל.");
                }

                switched = false;
                var startOld = await runtimeService.ExecuteLifecycleAsync(installation, runtime, "start", CancellationToken.None);
                stopped = startOld.ExitCode != 0;
                if (startOld.ExitCode == 0)
                {
                    var cleanupOld = await _session.ExecuteAsync(
                        BuildFullInstallationFinalizeScript(installation.Path, work, true), CancellationToken.None);
                    if (cleanupOld.ExitCode != 0)
                        DiagnosticLogService.Instance.Warning("FullRestore", "FailedTreeCleanupPending",
                            "The previous installation was restored, but the failed replacement directory remains on the server.", null, _session.Profile.Name);
                }
                throw new InvalidOperationException(startOld.ExitCode == 0
                    ? "ההתקנה המשוחזרת לא הצליחה לעלות. ההתקנה המקורית הוחזרה והופעלה מחדש."
                    : $"ההתקנה החדשה לא עלתה. ההתקנה המקורית הוחזרה, אך גם הפעלתה נכשלה. יש לבדוק את השירות; נתיב ההתקנה המקורית: {installation.Path}");
            }

            stopped = false;
            var finalize = await _session.ExecuteAsync(
                BuildFullInstallationFinalizeScript(installation.Path, work, false), CancellationToken.None);
            if (finalize.ExitCode != 0)
                DiagnosticLogService.Instance.Warning("FullRestore", "RollbackCleanupPending",
                    $"NodeBB was restored and started, but the emergency copy remains at {rollbackPath}.", null, _session.Profile.Name);

            DiagnosticLogService.Instance.Info("FullRestore", "RestoreCompleted",
                $"Full NodeBB installation restored and started; {archiveInfo.EntryCount} archive entries, database contents unchanged.",
                _session.Profile.Name);
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Instance.Error("FullRestore", "RestoreFailed", ex, _session.Profile.Name);
            if (recoveryRequired)
                throw new InvalidOperationException(ex.Message + $" עותק החירום: {rollbackPath}", ex);
            if (stopped && !switched && !restartAttempted && runtime is not null)
            {
                try
                {
                    var restart = await runtimeService.ExecuteLifecycleAsync(installation, runtime, "start", CancellationToken.None);
                    if (restart.ExitCode != 0)
                        DiagnosticLogService.Instance.Error("FullRestore", "OriginalInstallationRestartFailed",
                            $"The original NodeBB installation could not be restarted after the restore was aborted; exit code {restart.ExitCode}.", _session.Profile.Name);
                }
                catch (Exception restartError)
                {
                    DiagnosticLogService.Instance.Error("FullRestore", "OriginalInstallationRestartFailed", restartError, _session.Profile.Name);
                }
            }
            if (switched)
                throw new InvalidOperationException(
                    $"החיבור או הפעלת NodeBB נקטעו לאחר החלפת הקבצים. עותק החירום נשמר בנתיב {rollbackPath}; לא בוצע ניקוי אוטומטי כדי לא לסכן את ההתקנה.", ex);
            throw;
        }
        finally
        {
            // Once the remote restore script starts, it owns cleanup. After an uncertain
            // SSH interruption preserve remote state for manual recovery.
            if (workCreated && !scriptStarted)
            {
                try { await _session.ExecuteAsync($"rm -rf -- {ShellQuote(work)}", CancellationToken.None); }
                catch
                {
                    DiagnosticLogService.Instance.Warning("FullRestore", "RemoteCleanupFailed",
                        "Could not remove the temporary full-restore upload directory.", null, _session.Profile.Name);
                }
            }
        }
    }

    internal static string BuildFullInstallationPreflightScript(
        string installationPath, string workDirectory, string expectedSha256, long expandedBytes)
    {
        RemotePathSafety.ValidateNodeBbInstallationPath(installationPath);
        if (!Regex.IsMatch(workDirectory, @"^/tmp/nodebb-full-restore-[a-f0-9]{32}$"))
            throw new ArgumentException("נתיב העבודה הזמני אינו תקין.", nameof(workDirectory));
        if (!Regex.IsMatch(expectedSha256, @"^[a-f0-9]{64}$"))
            throw new ArgumentException("טביעת SHA-256 אינה תקינה.", nameof(expectedSha256));
        if (expandedBytes is < 1 or > 100L * 1024 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(expandedBytes));

        return """
set -eu
INSTALL=__INSTALL__
WORK=__WORK__
EXPECTED_SHA256=__HASH__
EXPECTED_BYTES=__BYTES__
PARENT="$(dirname "$INSTALL")"
BASE="$(basename "$INSTALL")"
WORK_NAME="$(basename "$WORK")"
STAGE="$PARENT/.nodebb-stage-$WORK_NAME"
ROLLBACK="$PARENT/.nodebb-rollback-$WORK_NAME"
FAILED="$PARENT/.nodebb-failed-$WORK_NAME"
[ -d "$WORK" ] && [ ! -L "$WORK" ] || { echo 'תיקיית העבודה הזמנית חסרה או אינה בטוחה.' >&2; exit 50; }
[ -s "$WORK/incoming.tar.gz" ] && [ ! -L "$WORK/incoming.tar.gz" ] || { echo 'ארכיון ההתקנה שהועלה חסר.' >&2; exit 50; }
command -v sha256sum >/dev/null 2>&1 && command -v tar >/dev/null 2>&1 && command -v node >/dev/null 2>&1 || { echo 'חסרים בשרת כלים נדרשים: sha256sum, tar או node.' >&2; exit 50; }
ACTUAL_SHA256="$(sha256sum -- "$WORK/incoming.tar.gz" | awk '{print $1}')"
[ "$ACTUAL_SHA256" = "$EXPECTED_SHA256" ] || { echo 'אימות SHA-256 של הארכיון נכשל.' >&2; exit 50; }
[ -d "$PARENT" ] && [ -d "$INSTALL" ] && [ ! -L "$INSTALL" ] || { echo 'תיקיית ההתקנה הקיימת אינה תקינה.' >&2; exit 50; }
PARENT_REAL="$(cd "$PARENT" && pwd -P)"
[ "$PARENT_REAL/$BASE" = "$INSTALL" ] || { echo 'נתיב ההתקנה אינו קנוני או עובר דרך קישור סמלי.' >&2; exit 50; }
[ -w "$PARENT" ] || { echo 'אין הרשאת כתיבה לתיקיית ההתקנה.' >&2; exit 50; }
[ -f "$INSTALL/package.json" ] && [ ! -L "$INSTALL/package.json" ] || { echo 'ההתקנה הקיימת אינה מזוהה כחבילת NodeBB.' >&2; exit 50; }
[ ! -e "$STAGE" ] && [ ! -e "$ROLLBACK" ] && [ ! -e "$FAILED" ] || { echo 'נמצאו תיקיות שחזור קודמות; לא תבוצע דריסה.' >&2; exit 50; }
if [ "$(id -u)" != "0" ]; then
  CURRENT_UID="$(id -u)"
  CURRENT_GID="$(id -g)"
  FOREIGN="$(find "$INSTALL" -xdev \( ! -user "$CURRENT_UID" -o ! -group "$CURRENT_GID" \) -print -quit)"
  [ -z "$FOREIGN" ] || { echo 'חלק מקובצי ההתקנה שייכים למשתמש או לקבוצה אחרים; יש להתחבר בהרשאות מתאימות.' >&2; exit 50; }
fi
AVAILABLE_KB="$(df -Pk "$PARENT" | awk 'END {print $4}')"
CURRENT_KB="$(du -skx "$INSTALL" | awk '{print $1}')"
case "$AVAILABLE_KB:$CURRENT_KB" in *[!0-9:]*|:*) echo 'לא ניתן לחשב בבטחה את המקום הפנוי וגודל ההתקנה הקיימת.' >&2; exit 50;; esac
REQUIRED_BYTES="$((EXPECTED_BYTES + CURRENT_KB * 1024 + 67108864))"
[ "$((AVAILABLE_KB * 1024))" -ge "$REQUIRED_BYTES" ] || { echo 'אין די מקום פנוי גם לחילוץ ההתקנה החדשה וגם לשמירת עותק החירום המלא של ההתקנה הקיימת.' >&2; exit 50; }
tar -tzf "$WORK/incoming.tar.gz" >/dev/null || { echo 'בדיקת קריאת ארכיון TAR.GZ בשרת נכשלה.' >&2; exit 50; }
echo 'FULL_RESTORE_PREFLIGHT_OK'
""".Replace("__INSTALL__", ShellQuote(installationPath), StringComparison.Ordinal)
            .Replace("__WORK__", ShellQuote(workDirectory), StringComparison.Ordinal)
            .Replace("__HASH__", ShellQuote(expectedSha256), StringComparison.Ordinal)
            .Replace("__BYTES__", expandedBytes.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    internal static string BuildFullInstallationRestoreScript(
        string installationPath, string workDirectory, string expectedSha256, long expandedBytes)
    {
        RemotePathSafety.ValidateNodeBbInstallationPath(installationPath);
        if (!Regex.IsMatch(workDirectory, @"^/tmp/nodebb-full-restore-[a-f0-9]{32}$"))
            throw new ArgumentException("נתיב העבודה הזמני אינו תקין.", nameof(workDirectory));
        if (!Regex.IsMatch(expectedSha256, @"^[a-f0-9]{64}$"))
            throw new ArgumentException("טביעת SHA-256 אינה תקינה.", nameof(expectedSha256));
        if (expandedBytes is < 1 or > 100L * 1024 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(expandedBytes));

        return """
set -eu
INSTALL=__INSTALL__
WORK=__WORK__
EXPECTED_SHA256=__HASH__
EXPECTED_BYTES=__BYTES__
WORK_NAME="$(basename "$WORK")"
PARENT="$(dirname "$INSTALL")"
BASE="$(basename "$INSTALL")"
STAGE="$PARENT/.nodebb-stage-$WORK_NAME"
ROLLBACK="$PARENT/.nodebb-rollback-$WORK_NAME"
FAILED="$PARENT/.nodebb-failed-$WORK_NAME"
SWAP_STARTED=0
KEEP_ROLLBACK=0

cleanup() {
  result=$?
  trap - EXIT
  if [ "$SWAP_STARTED" = "1" ] && [ "$KEEP_ROLLBACK" = "0" ] && [ -d "$ROLLBACK" ]; then
    set +e
    if [ -e "$INSTALL" ]; then mv "$INSTALL" "$FAILED" || result=42; fi
    if [ ! -e "$INSTALL" ]; then mv "$ROLLBACK" "$INSTALL" || result=42; fi
    if [ "$result" -eq 0 ]; then rm -rf -- "$FAILED"; fi
    set -e
  fi
  if [ "$KEEP_ROLLBACK" = "0" ]; then rm -rf -- "$STAGE"; fi
  rm -rf -- "$WORK"
  exit "$result"
}
trap cleanup EXIT

[ -d "$WORK" ] && [ ! -L "$WORK" ] || { echo 'תיקיית העבודה חסרה או אינה בטוחה.' >&2; exit 40; }
[ -s "$WORK/incoming.tar.gz" ] && [ ! -L "$WORK/incoming.tar.gz" ] || { echo 'ארכיון הגיבוי שהועלה חסר.' >&2; exit 40; }
command -v sha256sum >/dev/null 2>&1 || { echo 'sha256sum אינו זמין בשרת.' >&2; exit 40; }
ACTUAL_SHA256="$(sha256sum -- "$WORK/incoming.tar.gz" | awk '{print $1}')"
[ "$ACTUAL_SHA256" = "$EXPECTED_SHA256" ] || { echo 'אימות SHA-256 של הארכיון נכשל.' >&2; exit 40; }
command -v tar >/dev/null 2>&1 || { echo 'tar אינו מותקן בשרת.' >&2; exit 40; }
command -v node >/dev/null 2>&1 || { echo 'Node.js אינו מותקן בשרת.' >&2; exit 40; }
[ -d "$PARENT" ] && [ -d "$INSTALL" ] && [ ! -L "$INSTALL" ] || { echo 'נתיב ההתקנה הקיים אינו תיקייה רגילה.' >&2; exit 40; }
PARENT_REAL="$(cd "$PARENT" && pwd -P)"
[ "$PARENT_REAL/$BASE" = "$INSTALL" ] || { echo 'נתיב ההתקנה עובר דרך קישור סמלי או אינו קנוני.' >&2; exit 40; }
[ -w "$PARENT" ] || { echo 'אין הרשאת כתיבה לתיקיית ההתקנה.' >&2; exit 40; }
[ ! -e "$STAGE" ] && [ ! -e "$ROLLBACK" ] && [ ! -e "$FAILED" ] || { echo 'נמצאו תיקיות שחזור קודמות; לא בוצעה דריסה.' >&2; exit 40; }
[ -f "$INSTALL/package.json" ] && [ ! -L "$INSTALL/package.json" ] || { echo 'ההתקנה הנוכחית אינה מזוהה כהתקנת NodeBB.' >&2; exit 40; }

if [ "$(id -u)" != "0" ]; then
  CURRENT_UID="$(id -u)"
  CURRENT_GID="$(id -g)"
  FOREIGN="$(find "$INSTALL" -xdev \( ! -user "$CURRENT_UID" -o ! -group "$CURRENT_GID" \) -print -quit)"
  [ -z "$FOREIGN" ] || { echo 'קובצי ההתקנה שייכים למשתמש או לקבוצה אחרים. יש להתחבר כמשתמש NodeBB או כ-root; לא בוצע שינוי.' >&2; exit 40; }
fi

AVAILABLE_KB="$(df -Pk "$PARENT" | awk 'END {print $4}')"
CURRENT_KB="$(du -skx "$INSTALL" | awk '{print $1}')"
case "$AVAILABLE_KB:$CURRENT_KB" in *[!0-9:]*|:*) echo 'לא ניתן לחשב בבטחה את המקום הפנוי וגודל ההתקנה הקיימת.' >&2; exit 40;; esac
REQUIRED_BYTES="$((EXPECTED_BYTES + CURRENT_KB * 1024 + 67108864))"
[ "$((AVAILABLE_KB * 1024))" -ge "$REQUIRED_BYTES" ] || { echo 'אין די מקום פנוי גם לחילוץ ההתקנה החדשה וגם לשמירת עותק החירום המלא של ההתקנה הקיימת; לא בוצע שינוי.' >&2; exit 40; }

mkdir -m 700 "$STAGE"
tar -tzf "$WORK/incoming.tar.gz" >/dev/null || { echo 'ארכיון TAR.GZ פגום.' >&2; exit 41; }
if [ "$(id -u)" = "0" ]; then
  tar --same-owner --same-permissions -xzf "$WORK/incoming.tar.gz" -C "$STAGE" || { echo 'חילוץ הארכיון נכשל.' >&2; exit 41; }
else
  tar --no-same-owner --same-permissions -xzf "$WORK/incoming.tar.gz" -C "$STAGE" || { echo 'חילוץ הארכיון נכשל.' >&2; exit 41; }
fi
[ -f "$STAGE/config.json" ] && [ ! -L "$STAGE/config.json" ] || { echo 'config.json חסר או אינו קובץ רגיל.' >&2; exit 41; }
[ -f "$STAGE/package.json" ] && [ ! -L "$STAGE/package.json" ] || { echo 'package.json חסר או אינו קובץ רגיל.' >&2; exit 41; }
[ -f "$STAGE/nodebb" ] && [ -x "$STAGE/nodebb" ] && [ ! -L "$STAGE/nodebb" ] || { echo 'קובץ ההפעלה nodebb חסר, אינו executable או אינו קובץ רגיל.' >&2; exit 41; }
SPECIAL="$(find "$STAGE" -mindepth 1 ! -type f ! -type d -print -quit)"
[ -z "$SPECIAL" ] || { echo 'הארכיון חולץ עם קישור או קובץ מיוחד; השחזור בוטל.' >&2; exit 41; }

node - "$INSTALL/package.json" "$INSTALL/config.json" "$STAGE/package.json" "$STAGE/config.json" <<'NODE'
const fs = require('fs');
function readJson(path) { return JSON.parse(fs.readFileSync(path, 'utf8')); }
function identity(config) {
  const type = config.database;
  if (!['mongo', 'postgres', 'redis'].includes(type)) throw new Error('unsupported database');
  const section = config[type];
  if (!section || typeof section !== 'object') throw new Error('missing database section');
  const defaults = { mongo: 27017, postgres: 5432, redis: 6379 };
  const normalizeHost = value => String(value).replace(/^\[|\]$/g, '').toLowerCase();
  let host = typeof section.host === 'string' && section.host ? normalizeHost(section.host) : '127.0.0.1';
  let port = Number(section.port || defaults[type]);
  let database = section.database || section.db || (type === 'redis' ? 0 : '');
  if (section.uri) {
    const uri = new URL(section.uri);
    if (type === 'mongo' && !['mongodb:', 'mongodb+srv:'].includes(uri.protocol)) throw new Error('invalid mongo uri');
    if (type === 'postgres' && !['postgres:', 'postgresql:'].includes(uri.protocol)) throw new Error('invalid postgres uri');
    if (type === 'redis' && !['redis:', 'rediss:'].includes(uri.protocol)) throw new Error('invalid redis uri');
    host = normalizeHost(uri.hostname);
    port = Number(uri.port || defaults[type]);
    const pathDatabase = decodeURIComponent(uri.pathname.replace(/^\/+/, ''));
    if (pathDatabase) database = pathDatabase;
  }
  if (!host || !Number.isInteger(port) || port < 1 || port > 65535) throw new Error('invalid endpoint');
  return JSON.stringify([type, host, port, String(database)]);
}
try {
  const currentPackage = readJson(process.argv[2]);
  const currentConfig = readJson(process.argv[3]);
  const incomingPackage = readJson(process.argv[4]);
  const incomingConfig = readJson(process.argv[5]);
  if (String(currentPackage.name).toLowerCase() !== 'nodebb' ||
      String(incomingPackage.name).toLowerCase() !== 'nodebb') throw new Error('not NodeBB');
  if (identity(currentConfig) !== identity(incomingConfig)) {
    console.error('הגדרות מסד הנתונים בארכיון שונות מההתקנה הנוכחית; השחזור בוטל כדי למנוע חיבור למסד אחר.');
    process.exit(30);
  }
} catch (error) {
  if (error && error.message && error.message.includes('הגדרות מסד')) process.exit(30);
  console.error('לא ניתן לאמת את חבילות NodeBB ואת זהות מסד הנתונים.');
  process.exit(31);
}
NODE

SWAP_STARTED=1
mv -- "$INSTALL" "$ROLLBACK" || { SWAP_STARTED=0; echo 'לא ניתן להעביר את ההתקנה המקורית לתיקיית החירום.' >&2; exit 43; }
if ! mv -- "$STAGE" "$INSTALL"; then
  echo 'לא ניתן להציב את ההתקנה החדשה; מתבצעת החזרה אוטומטית.' >&2
  exit 43
fi
KEEP_ROLLBACK=1
echo 'FULL_RESTORE_SWITCHED'
""".Replace("__INSTALL__", ShellQuote(installationPath), StringComparison.Ordinal)
            .Replace("__WORK__", ShellQuote(workDirectory), StringComparison.Ordinal)
            .Replace("__HASH__", ShellQuote(expectedSha256), StringComparison.Ordinal)
            .Replace("__BYTES__", expandedBytes.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);

    }

    internal static string BuildFullInstallationRollbackScript(string installationPath, string workDirectory)
    {
        RemotePathSafety.ValidateNodeBbInstallationPath(installationPath);
        if (!Regex.IsMatch(workDirectory, @"^/tmp/nodebb-full-restore-[a-f0-9]{32}$"))
            throw new ArgumentException("נתיב העבודה הזמני אינו תקין.", nameof(workDirectory));
        return """
set -eu
INSTALL=__INSTALL__
WORK=__WORK__
WORK_NAME="$(basename "$WORK")"
PARENT="$(dirname "$INSTALL")"
ROLLBACK="$PARENT/.nodebb-rollback-$WORK_NAME"
FAILED="$PARENT/.nodebb-failed-$WORK_NAME"
[ -d "$ROLLBACK" ] && [ ! -L "$ROLLBACK" ] || { echo 'עותק החירום של ההתקנה המקורית לא נמצא.' >&2; exit 44; }
[ -d "$INSTALL" ] && [ ! -L "$INSTALL" ] || { echo 'תיקיית ההתקנה החדשה אינה תקינה.' >&2; exit 44; }
[ ! -e "$FAILED" ] || { echo 'תיקיית כשל קודמת כבר קיימת.' >&2; exit 44; }
mv -- "$INSTALL" "$FAILED" || exit 44
if ! mv -- "$ROLLBACK" "$INSTALL"; then
  mv -- "$FAILED" "$INSTALL" || { echo 'CRITICAL: לא ניתן להחזיר את שתי תיקיות ההתקנה למקומן.' >&2; exit 45; }
  exit 44
fi
echo 'FULL_RESTORE_ROLLED_BACK'
""".Replace("__INSTALL__", ShellQuote(installationPath), StringComparison.Ordinal)
            .Replace("__WORK__", ShellQuote(workDirectory), StringComparison.Ordinal);

    }

    internal static string BuildFullInstallationFinalizeScript(
        string installationPath, string workDirectory, bool removeFailedReplacement)
    {
        RemotePathSafety.ValidateNodeBbInstallationPath(installationPath);
        if (!Regex.IsMatch(workDirectory, @"^/tmp/nodebb-full-restore-[a-f0-9]{32}$"))
            throw new ArgumentException("נתיב העבודה הזמני אינו תקין.", nameof(workDirectory));
        return """
set -eu
INSTALL=__INSTALL__
WORK=__WORK__
WORK_NAME="$(basename "$WORK")"
PARENT="$(dirname "$INSTALL")"
ROLLBACK="$PARENT/.nodebb-rollback-$WORK_NAME"
FAILED="$PARENT/.nodebb-failed-$WORK_NAME"
[ -d "$INSTALL" ] && [ ! -L "$INSTALL" ] || { echo 'ההתקנה הפעילה אינה תיקייה תקינה.' >&2; exit 46; }
[ -f "$INSTALL/package.json" ] && [ ! -L "$INSTALL/package.json" ] || { echo 'חבילת NodeBB אינה נמצאת בהתקנה הפעילה.' >&2; exit 46; }
if [ "__REMOVE_FAILED__" = "1" ]; then
  [ -d "$FAILED" ] && [ ! -L "$FAILED" ] || { echo 'תיקיית ההתקנה שנכשלה לא נמצאה.' >&2; exit 46; }
  rm -rf -- "$FAILED"
else
  [ -d "$ROLLBACK" ] && [ ! -L "$ROLLBACK" ] || { echo 'עותק החירום לא נמצא; לא ניתן לאשר ניקוי.' >&2; exit 46; }
  rm -rf -- "$ROLLBACK"
fi
rm -rf -- "$WORK"
echo 'FULL_RESTORE_FINALIZED'
""".Replace("__INSTALL__", ShellQuote(installationPath), StringComparison.Ordinal)
            .Replace("__WORK__", ShellQuote(workDirectory), StringComparison.Ordinal)
            .Replace("__REMOVE_FAILED__", removeFailedReplacement ? "1" : "0", StringComparison.Ordinal);

    }

    private static string GetFullRestoreRollbackPath(string installationPath, string workDirectory)
    {
        var workName = workDirectory[(workDirectory.LastIndexOf('/') + 1)..];
        var parent = installationPath[..installationPath.TrimEnd('/').LastIndexOf('/')];
        return parent + "/.nodebb-rollback-" + workName;
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    internal static string BuildRestoreScript(string installationPath, string workDirectory, bool restoreUploads)
    {
        RemotePathSafety.ValidateNodeBbInstallationPath(installationPath);
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
        var redactedError = DiagnosticLogService.Redact(error);
        var safe = string.Join(Environment.NewLine,
            redactedError.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
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
