using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using Renci.SshNet.Sftp;
using NodeBB.SSHManager.Services;
using NodeBB.SSHManager.Localization;

namespace NodeBB.SSHManager.Views;

public sealed class RemoteFileItem
{
    public string Name { get; init; } = "";
    public string FullPath { get; init; } = "";
    public bool IsDirectory { get; init; }
    public string Kind => IsDirectory ? LocalizationService.Instance["Sftp_Directory"] : LocalizationService.Instance["Sftp_File"];
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
            StatusText.Text = LocalizationService.Instance["Sftp_Connecting"];
            await WithSftpLockAsync(() => { _client.Connect(); return true; });
            await LoadDirectoryAsync(_currentPath);
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Instance.Error("SFTP", "ConnectOrListFailed", ex, _session.Profile.Name);
            StatusText.Text = string.Format(LocalizationService.Instance["Sftp_Error"], ex.Message);
        }
    }

    private async Task LoadDirectoryAsync(string path)
    {
        try
        {
            var safePath = SftpPathSafety.NormalizeAbsolutePath(path);
            var list = await WithSftpLockAsync(() => _client.ListDirectory(safePath)
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
            _currentPath = safePath;
            PathBox.Text = safePath;
            _items.Clear();
            foreach (var item in list) _items.Add(item);
            StatusText.Text = string.Format(LocalizationService.Instance["Sftp_Items"], _items.Count);
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Instance.Error("SFTP", "DirectoryListingFailed", ex, _session.Profile.Name);
            StatusText.Text = string.Format(LocalizationService.Instance["Sftp_ListError"], ex.Message);
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

    private async void Navigate_Click(object sender, RoutedEventArgs e)
    {
        var requested = PathBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(requested)) { StatusText.Text = LocalizationService.Instance["Sftp_AbsolutePath"]; return; }
        await LoadDirectoryAsync(requested);
    }

    private async void CreateDirectory_Click(object sender, RoutedEventArgs e)
    {
        var name = PromptForName(LocalizationService.Instance["Sftp_CreateDirectory"], LocalizationService.Instance["Sftp_NewDirectory"]);
        if (name is null) return;
        try
        {
            var remotePath = SftpPathSafety.Combine(_currentPath, name);
            await WithSftpLockAsync(() =>
            {
                if (_client.Exists(remotePath)) throw new IOException(LocalizationService.Instance["Sftp_AlreadyExists"]);
                _client.CreateDirectory(remotePath);
                return true;
            });
            DiagnosticLogService.Instance.Info("SFTP", "DirectoryCreated", $"Created remote directory {remotePath}.", _session.Profile.Name);
            await LoadDirectoryAsync(_currentPath);
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Instance.Error("SFTP", "DirectoryCreateFailed", ex, _session.Profile.Name);
            StatusText.Text = string.Format(LocalizationService.Instance["Sftp_CreateFailed"], ex.Message);
        }
    }

    private async void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (FilesList.SelectedItem is not RemoteFileItem item) { StatusText.Text = LocalizationService.Instance["Sftp_SelectRename"]; return; }
        var newName = PromptForName(LocalizationService.Instance["Sftp_Rename"], LocalizationService.Instance["Sftp_NewName"], item.Name);
        if (newName is null || string.Equals(newName, item.Name, StringComparison.Ordinal)) return;
        try
        {
            var destination = SftpPathSafety.Combine(_currentPath, newName);
            await WithSftpLockAsync(() =>
            {
                var source = SftpPathSafety.NormalizeAbsolutePath(item.FullPath);
                if (_client.Exists(destination)) throw new IOException(LocalizationService.Instance["Sftp_AlreadyExistsNew"]);
                if (item.IsDirectory) _client.RenameFile(source, destination);
                else _client.RenameFile(source, destination);
                return true;
            });
            DiagnosticLogService.Instance.Info("SFTP", "RemoteRenameCompleted", $"Renamed {item.FullPath} to {destination}.", _session.Profile.Name);
            await LoadDirectoryAsync(_currentPath);
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Instance.Error("SFTP", "RemoteRenameFailed", ex, _session.Profile.Name);
            StatusText.Text = string.Format(LocalizationService.Instance["Sftp_RenameFailed"], ex.Message);
        }
    }

    private string? PromptForName(string title, string label, string initialValue = "")
    {
        var dialog = new Window
        {
            Title = title, Owner = this, Width = 430, Height = 175,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize, FlowDirection = FlowDirection.RightToLeft
        };
        var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new System.Windows.Controls.TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 8) });
        var input = new System.Windows.Controls.TextBox { Text = initialValue, Height = 32, VerticalContentAlignment = VerticalAlignment.Center };
        panel.Children.Add(input);
        var buttons = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 14, 0, 0) };
        var ok = new System.Windows.Controls.Button { Content = LocalizationService.Instance["Sftp_Ok"], Width = 90, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new System.Windows.Controls.Button { Content = LocalizationService.Instance["Sftp_Cancel"], Width = 90, IsCancel = true };
        ok.Click += (_, _) => { try { SftpPathSafety.ValidateName(input.Text.Trim()); dialog.DialogResult = true; dialog.Close(); } catch (Exception ex) { MessageBox.Show(dialog, ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Warning); } };
        buttons.Children.Add(ok); buttons.Children.Add(cancel); panel.Children.Add(buttons);
        dialog.Content = panel;
        dialog.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
        return dialog.ShowDialog() == true ? input.Text.Trim() : null;
    }
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
        var dialog = new OpenFileDialog { Title = LocalizationService.Instance["Sftp_SelectUpload"], CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var fileName = Path.GetFileName(dialog.FileName);
            var remotePath = SftpPathSafety.Combine(_currentPath, fileName);
            var replaceApproved = await WithSftpLockAsync(() => _client.Exists(remotePath));
            if (replaceApproved &&
                MessageBox.Show(this, string.Format(LocalizationService.Instance["Sftp_ReplacePrompt"], fileName),
                    LocalizationService.Instance["Sftp_ConfirmReplace"], MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            StatusText.Text = LocalizationService.Instance["Sftp_Uploading"];
            await WithSftpLockAsync(() =>
            {
                var existsNow = _client.Exists(remotePath);
                if (existsNow && !replaceApproved)
                    throw new IOException(LocalizationService.Instance["Sftp_Race"]);

                var token = Guid.NewGuid().ToString("N");
                var temporaryPath = SftpPathSafety.Combine(_currentPath, $".{fileName}.upload-{token}");
                var backupPath = SftpPathSafety.Combine(_currentPath, $".{fileName}.previous-{token}");
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
            StatusText.Text = LocalizationService.Instance["Sftp_UploadDone"];
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Instance.Error("SFTP", "UploadFailed", ex, _session.Profile.Name);
            StatusText.Text = string.Format(LocalizationService.Instance["Sftp_UploadFailed"], ex.Message);
        }
    }

    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        if (FilesList.SelectedItem is not RemoteFileItem { IsDirectory: false } item) { StatusText.Text = LocalizationService.Instance["Sftp_SelectDownload"]; return; }
        var dialog = new SaveFileDialog { FileName = item.Name, Title = LocalizationService.Instance["Sftp_SaveFile"] };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            StatusText.Text = LocalizationService.Instance["Sftp_Downloading"];
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
            StatusText.Text = LocalizationService.Instance["Sftp_DownloadDone"];
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Instance.Error("SFTP", "DownloadFailed", ex, _session.Profile.Name);
            StatusText.Text = string.Format(LocalizationService.Instance["Sftp_DownloadFailed"], ex.Message);
        }
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (FilesList.SelectedItem is not RemoteFileItem item) { StatusText.Text = LocalizationService.Instance["Sftp_SelectDelete"]; return; }
        if (MessageBox.Show(this, string.Format(LocalizationService.Instance["Sftp_DeletePrompt"], item.Name), LocalizationService.Instance["Sftp_ConfirmDelete"],
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
            StatusText.Text = string.Format(LocalizationService.Instance["Sftp_DeleteFailed"], ex.Message);
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
