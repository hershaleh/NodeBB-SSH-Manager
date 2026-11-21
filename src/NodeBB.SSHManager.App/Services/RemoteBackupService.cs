using System.Formats.Tar;
using System.IO.Compression;

namespace NodeBB.SSHManager.Services;

public enum RemoteBackupKind
{
    FullInstallation,
    ConfigurationAndUploads
}

public sealed record BackupArchiveValidationResult(int EntryCount, long ExpandedBytes);

public sealed class RemoteBackupService
{
    private readonly SshServerSession _session;

    public RemoteBackupService(SshServerSession session) => _session = session;

    public async Task DownloadBackupAsync(
        NodeBbInstallation installation,
        RemoteBackupKind kind,
        string localDestination,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(localDestination))
            throw new ArgumentException("יש לבחור נתיב לשמירת הגיבוי.", nameof(localDestination));

        var archiveName = $"nodebb-ssh-manager-{Guid.NewGuid():N}.tar.gz";
        var remoteArchive = "/tmp/" + archiveName;
        var partialDestination = localDestination + ".partial-" + Guid.NewGuid().ToString("N");
        var command = BuildArchiveCommand(installation.Path, remoteArchive, kind);
        DiagnosticLogService.Instance.Info("Backup", "BackupStarted",
            $"Started {kind} backup for {installation.Path}.", _session.Profile.Name);

        try
        {
            var result = await _session.ExecuteAsync(command, cancellationToken);
            if (result.ExitCode != 0)
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.Error)
                    ? $"יצירת הגיבוי נכשלה (קוד {result.ExitCode})."
                    : result.Error);

            using var sftp = _session.CreateSftpClient();
            await Task.Run(sftp.Connect, cancellationToken);
            await using (var output = new FileStream(partialDestination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await Task.Run(() => sftp.DownloadFile(remoteArchive, output), cancellationToken);
                await output.FlushAsync(cancellationToken);
            }

            var validation = await ValidateArchiveAsync(partialDestination, cancellationToken);
            if (validation.EntryCount == 0)
                throw new InvalidDataException("ארכיון הגיבוי ריק ואינו מכיל קבצים.");

            File.Move(partialDestination, localDestination, true);
            var info = new FileInfo(localDestination);
            DiagnosticLogService.Instance.Info("Backup", "BackupCompleted",
                $"Backup downloaded and verified successfully; {info.Length} bytes, {validation.EntryCount} entries.", _session.Profile.Name);
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Instance.Error("Backup", "BackupFailed", ex, _session.Profile.Name);
            throw;
        }
        finally
        {
            try { if (File.Exists(partialDestination)) File.Delete(partialDestination); } catch { }
            try
            {
                await _session.ExecuteAsync($"rm -f {ShellQuote(remoteArchive)}", CancellationToken.None);
            }
            catch (Exception ex)
            {
                DiagnosticLogService.Instance.Warning("Backup", "RemoteTemporaryFileCleanupFailed",
                    "Could not remove the temporary backup archive from the remote server.", ex, _session.Profile.Name);
            }
        }
    }


    /// <summary>
    /// Fully reads the downloaded TAR.GZ so gzip checksums, TAR headers and every entry
    /// are verified locally before the archive is promoted to its final destination.
    /// This validates integrity only; it does not make a full-installation archive restorable.
    /// </summary>
    public static async Task<BackupArchiveValidationResult> ValidateArchiveAsync(
        string archivePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(archivePath) || !File.Exists(archivePath))
            throw new FileNotFoundException("קובץ הגיבוי הזמני לא נמצא.", archivePath);
        if (new FileInfo(archivePath).Length < 20)
            throw new InvalidDataException("ארכיון הגיבוי קצר מדי או פגום.");

        await using var file = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
        var magic = new byte[2];
        await file.ReadExactlyAsync(magic, cancellationToken);
        if (magic[0] != 0x1f || magic[1] != 0x8b)
            throw new InvalidDataException("הקובץ שהורד אינו ארכיון GZip תקין.");

        try
        {
            file.Position = 0;
            await using var gzip = new GZipStream(file, CompressionMode.Decompress, true);
        await using var reader = new TarReader(gzip, leaveOpen: true);
        var entryCount = 0;
        long expandedBytes = 0;
        TarEntry? entry;
        while ((entry = await reader.GetNextEntryAsync(false, cancellationToken)) is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++entryCount > 2_000_000)
                throw new InvalidDataException("ארכיון הגיבוי מכיל מספר חריג של פריטים.");
            if (entry.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile)
            {
                if (entry.Length < 0 || entry.Length > long.MaxValue - expandedBytes)
                    throw new InvalidDataException("גודל תוכן הארכיון אינו תקין.");
                expandedBytes += entry.Length;
                if (entry.DataStream is null)
                    throw new InvalidDataException("הארכיון מכיל קובץ ללא זרם נתונים.");
                long entryBytes = 0;
                var entryBuffer = new byte[131072];
                int entryRead;
                while ((entryRead = await entry.DataStream.ReadAsync(entryBuffer, cancellationToken)) > 0)
                    entryBytes += entryRead;
                if (entryBytes != entry.Length)
                    throw new InvalidDataException("קובץ בתוך ארכיון הגיבוי נקטע או פגום.");
            }
            else if (entry.DataStream is not null)
            {
                await entry.DataStream.CopyToAsync(Stream.Null, cancellationToken);
            }
        }

        if (entryCount == 0)
            throw new InvalidDataException("ארכיון הגיבוי ריק ואינו מכיל פריטים.");

        var buffer = new byte[131072];
        while (await gzip.ReadAsync(buffer, cancellationToken) > 0)
            cancellationToken.ThrowIfCancellationRequested();

            return new BackupArchiveValidationResult(entryCount, expandedBytes);
        }
        catch (EndOfStreamException ex)
        {
            throw new InvalidDataException("ארכיון הגיבוי נקטע לפני סופו או שאינו מכיל מבנה TAR שלם.", ex);
        }
    }

    private static string BuildArchiveCommand(string installationPath, string archivePath, RemoteBackupKind kind)
    {
        RemotePathSafety.ValidateNodeBbInstallationPath(installationPath);
        var path = ShellQuote(installationPath);
        var archive = ShellQuote(archivePath);
        return kind switch
        {
            RemoteBackupKind.FullInstallation => $"umask 077; command -v tar >/dev/null 2>&1 || exit 127; tar -czf {archive} -C {path} . && tar -tzf {archive} >/dev/null",
            RemoteBackupKind.ConfigurationAndUploads => $"""
cd {path} || exit 1
command -v tar >/dev/null 2>&1 || exit 127
umask 077
[ -f config.json ] || (echo 'config.json לא נמצא בהתקנת NodeBB.' >&2; exit 3)
set -- config.json
for item in package.json package-lock.json npm-shrinkwrap.json yarn.lock pnpm-lock.yaml public/uploads; do
  [ -e "$item" ] && set -- "$@" "$item"
done
tar -czf {archive} "$@" && tar -tzf {archive} >/dev/null
""",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
    }

    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''") + "'";
}
