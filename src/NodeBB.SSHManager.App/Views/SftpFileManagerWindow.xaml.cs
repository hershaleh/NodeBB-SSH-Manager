using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using Renci.SshNet.Sftp;
using NodeBB.SSHManager.Services;

namespace NodeBB.SSHManager.Views;

public sealed class RemoteFileItem
{
    public string Name { get; init; } = "";
    public string FullPath { get; init; } = "";
    public bool IsDirectory { get; init; }
    public string Kind => IsDirectory ? "תיקייה" : "קובץ";
    public string SizeDisplay { get; init; } = "—";
    public string ModifiedDisplay { get; init; } = "—";
}

public partial class SftpFileManagerWindow : Window
{
    private readonly SshServerSession _session;
    private readonly Renci.SshNet.SftpClient _client;
    private readonly ObservableCollection<RemoteFileItem> _items = [];
    private readonly SemaphoreSlim _sftpLock = new(1, 1);
    private volatile bool _isClosing;
    private string _currentPath = "/";

    public SftpFileManagerWindow(SshServerSession session)
    {
        InitializeComponent();
        _session = session;
        _client = session.CreateSftpClient();
        FilesList.ItemsSource = _items;
        ServerLabel.Text = $"{session.Profile.Name} · {session.Profile.Host}";
        Loaded += async (_, _) => await ConnectAndLoadAsync();
        Closed += async (_, _) =>
        {
            _isClosing = true;
            await _sftpLock.WaitAsync();
            try { _client.Dispose(); }
            finally { _sftpLock.Release(); }
        };
    }

    private async Task ConnectAndLoadAsync()
    {
        try
        {
            StatusText.Text = "מתחבר...";
            await WithSftpLockAsync(() => { _client.Connect(); return true; });
            await LoadDirectoryAsync(_currentPath);
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Instance.Error("SFTP", "ConnectOrListFailed", ex, _session.Profile.Name);
            StatusText.Text = "שגיאה: " + ex.Message;
        }
    }

    private async Task LoadDirectoryAsync(string path)
    {
        try
        {
            var list = await WithSftpLockAsync(() => _client.ListDirectory(path)
                .Where(x => x.Name is not ("." or ".."))
                .OrderByDescending(x => x.IsDirectory).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .Select(x => new RemoteFileItem
                {
                    Name = SftpPathSafety.ValidateName(x.Name),
                    FullPath = SftpPathSafety.NormalizeAbsolutePath(x.FullName),
                    IsDirectory = x.IsDirectory,
                    SizeDisplay = x.IsDirectory ? "—" : FormatSize(x.Length),
                    ModifiedDisplay = x.LastWriteTime.ToString("yyyy-MM-dd HH:mm")
                }).ToArray());
            _currentPath = path;
            PathBox.Text = path;
            _items.Clear();
            foreach (var item in list) _items.Add(item);
            StatusText.Text = $"{_items.Count} פריטים";
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Instance.Error("SFTP", "DirectoryListingFailed", ex, _session.Profile.Name);
            StatusText.Text = "שגיאה בטעינת התיקייה: " + ex.Message;
        }
    }

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = bytes; var unit = 0;
        while (size >= 1024 && unit < units.Length - 1) { size /= 1024; unit++; }
        return $"{size:0.##} {units[unit]}";
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadDirectoryAsync(_currentPath);
    private async void Up_Click(object sender, RoutedEventArgs e)
    {
        var parent = _currentPath.TrimEnd('/');
        var index = parent.LastIndexOf('/');
        await LoadDirectoryAsync(index <= 0 ? "/" : parent[..index]);
    }

    private async void FilesList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FilesList.SelectedItem is RemoteFileItem { IsDirectory: true } item)
            await LoadDirectoryAsync(item.FullPath);
    }

    private async void Upload_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "בחירת קובץ להעלאה", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var fileName = Path.GetFileName(dialog.FileName);
            var remotePath = SftpPathSafety.Combine(_currentPath, fileName);
            var replaceApproved = await WithSftpLockAsync(() => _client.Exists(remotePath));
            if (replaceApproved &&
                MessageBox.Show(this, $"הקובץ '{fileName}' כבר קיים בשרת. להחליף אותו?",
                    "אישור החלפה", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            StatusText.Text = "מעלה קובץ...";
            await WithSftpLockAsync(() =>
            {
                var existsNow = _client.Exists(remotePath);
                if (existsNow && !replaceApproved)
                    throw new IOException("קובץ באותו שם נוצר בשרת לאחר בדיקת ההחלפה. לא בוצעה דריסה.");

                var token = Guid.NewGuid().ToString("N");
                var temporaryPath = Combine(_currentPath, $".{fileName}.upload-{token}");
                var backupPath = Combine(_currentPath, $".{fileName}.previous-{token}");
                var movedOriginal = false;
                try
                {
                    using (var input = File.OpenRead(dialog.FileName))
                        _client.UploadFile(input, temporaryPath, false);

                    if (existsNow)
                    {
                        _client.RenameFile(remotePath, backupPath);
                        movedOriginal = true;
                    }

                    _client.RenameFile(temporaryPath, remotePath);
                    if (movedOriginal && _client.Exists(backupPath))
                    {
                        try { _client.DeleteFile(backupPath); }
                        catch (Exception cleanupError)
                        {
                            DiagnosticLogService.Instance.Warning("SFTP", "UploadBackupCleanupFailed",
                                $"The new file is in place, but the previous-file backup remains at {backupPath}.", null, _session.Profile.Name);
                            DiagnosticLogService.Instance.Error("SFTP", "UploadBackupCleanupError", cleanupError, _session.Profile.Name);
                        }
                    }
                }
                catch
                {
                    try
                    {
                        if (_client.Exists(temporaryPath)) _client.DeleteFile(temporaryPath);
                        if (movedOriginal && !_client.Exists(remotePath) && _client.Exists(backupPath))
                            _client.RenameFile(backupPath, remotePath);
                    }
                    catch (Exception recoveryError)
                    {
                        DiagnosticLogService.Instance.Error("SFTP", "UploadRecoveryFailed", recoveryError, _session.Profile.Name);
                    }
                    throw;
                }
                return true;
            });
            await LoadDirectoryAsync(_currentPath);
            DiagnosticLogService.Instance.Info("SFTP", "UploadCompleted", $"Uploaded a file to {_currentPath}.", _session.Profile.Name);
            StatusText.Text = "ההעלאה הושלמה";
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Instance.Error("SFTP", "UploadFailed", ex, _session.Profile.Name);
            StatusText.Text = "ההעלאה נכשלה: " + ex.Message;
        }
    }

    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        if (FilesList.SelectedItem is not RemoteFileItem { IsDirectory: false } item) { StatusText.Text = "בחר קובץ להורדה."; return; }
        var dialog = new SaveFileDialog { FileName = item.Name, Title = "שמירת קובץ" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            StatusText.Text = "מוריד קובץ...";
            var destination = Path.GetFullPath(dialog.FileName);
            var remotePath = SftpPathSafety.NormalizeAbsolutePath(item.FullPath);
            var temporaryPath = destination + ".partial-" + Guid.NewGuid().ToString("N");
            try
            {
                await WithSftpLockAsync(() =>
                {
                    using var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    _client.DownloadFile(remotePath, output);
                    output.Flush(true);
                    return true;
                });
                File.Move(temporaryPath, destination, true);
            }
            catch
            {
                try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch { }
                throw;
            }
            DiagnosticLogService.Instance.Info("SFTP", "DownloadCompleted", $"Downloaded remote file {item.FullPath}.", _session.Profile.Name);
            StatusText.Text = "ההורדה הושלמה";
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Instance.Error("SFTP", "DownloadFailed", ex, _session.Profile.Name);
            StatusText.Text = "ההורדה נכשלה: " + ex.Message;
        }
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (FilesList.SelectedItem is not RemoteFileItem item) { StatusText.Text = "בחר פריט למחיקה."; return; }
        if (MessageBox.Show(this, $"למחוק את '{item.Name}' מהשרת? פעולה זו אינה הפיכה.", "אישור מחיקה",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            await WithSftpLockAsync(() =>
            {
                var remotePath = SftpPathSafety.NormalizeAbsolutePath(item.FullPath);
                if (item.IsDirectory) _client.DeleteDirectory(remotePath);
                else _client.DeleteFile(remotePath);
                return true;
            });
            DiagnosticLogService.Instance.Info("SFTP", "RemoteDeleteCompleted", $"Deleted remote item {item.FullPath}.", _session.Profile.Name);
            await LoadDirectoryAsync(_currentPath);
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Instance.Error("SFTP", "RemoteDeleteFailed", ex, _session.Profile.Name);
            StatusText.Text = "המחיקה נכשלה: " + ex.Message;
        }
    }

    private async Task<T> WithSftpLockAsync<T>(Func<T> operation)
    {
        if (_isClosing) throw new ObjectDisposedException(nameof(SftpFileManagerWindow));
        await _sftpLock.WaitAsync();
        try
        {
            if (_isClosing) throw new ObjectDisposedException(nameof(SftpFileManagerWindow));
            return await Task.Run(operation);
        }
        finally
        {
            _sftpLock.Release();
        }
    }

}
