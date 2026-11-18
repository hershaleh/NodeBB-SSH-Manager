namespace NodeBB.SSHManager.Services;

public enum RemoteBackupKind
{
    FullInstallation,
    ConfigurationAndUploads
}

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

            File.Move(partialDestination, localDestination, true);
            var info = new FileInfo(localDestination);
            DiagnosticLogService.Instance.Info("Backup", "BackupCompleted",
                $"Backup downloaded successfully; {info.Length} bytes.", _session.Profile.Name);
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

    private static string BuildArchiveCommand(string installationPath, string archivePath, RemoteBackupKind kind)
    {
        var path = ShellQuote(installationPath);
        var archive = ShellQuote(archivePath);
        return kind switch
        {
            RemoteBackupKind.FullInstallation => $"umask 077; command -v tar >/dev/null 2>&1 || exit 127; tar -czf {archive} -C {path} . && tar -tzf {archive} >/dev/null",
            RemoteBackupKind.ConfigurationAndUploads => $"""
cd {path} || exit 1
command -v tar >/dev/null 2>&1 || exit 127
umask 077
[ -f config.json ] || { echo 'config.json לא נמצא בהתקנת NodeBB.' >&2; exit 3; }
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
