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
    private string _currentPath = "/";

    public SftpFileManagerWindow(SshServerSession session)
    {
        InitializeComponent();
        _session = session;
        _client = session.CreateSftpClient();
        FilesList.ItemsSource = _items;
        ServerLabel.Text = $"{session.Profile.Name} · {session.Profile.Host}";
        Loaded += async (_, _) => await ConnectAndLoadAsync();
        Closed += (_, _) => _client.Dispose();
    }

    private async Task ConnectAndLoadAsync()
    {
        try
        {
            StatusText.Text = "מתחבר...";
            await Task.Run(_client.Connect);
            await LoadDirectoryAsync(_currentPath);
        }
        catch (Exception ex) { StatusText.Text = "שגיאה: " + ex.Message; }
    }

    private async Task LoadDirectoryAsync(string path)
    {
        try
        {
            var list = await Task.Run(() => _client.ListDirectory(path)
                .Where(x => x.Name is not ("." or ".."))
                .OrderByDescending(x => x.IsDirectory).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .Select(x => new RemoteFileItem
                {
                    Name = x.Name,
                    FullPath = x.FullName,
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
        catch (Exception ex) { StatusText.Text = "שגיאה בטעינת התיקייה: " + ex.Message; }
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
            StatusText.Text = "מעלה קובץ...";
            await using var input = File.OpenRead(dialog.FileName);
            await Task.Run(() => _client.UploadFile(input, Combine(_currentPath, Path.GetFileName(dialog.FileName)), true));
            await LoadDirectoryAsync(_currentPath);
            StatusText.Text = "ההעלאה הושלמה";
        }
        catch (Exception ex) { StatusText.Text = "ההעלאה נכשלה: " + ex.Message; }
    }

    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        if (FilesList.SelectedItem is not RemoteFileItem { IsDirectory: false } item) { StatusText.Text = "בחר קובץ להורדה."; return; }
        var dialog = new SaveFileDialog { FileName = item.Name, Title = "שמירת קובץ" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            StatusText.Text = "מוריד קובץ...";
            await using var output = File.Create(dialog.FileName);
            await Task.Run(() => _client.DownloadFile(item.FullPath, output));
            StatusText.Text = "ההורדה הושלמה";
        }
        catch (Exception ex) { StatusText.Text = "ההורדה נכשלה: " + ex.Message; }
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (FilesList.SelectedItem is not RemoteFileItem item) { StatusText.Text = "בחר פריט למחיקה."; return; }
        if (MessageBox.Show(this, $"למחוק את '{item.Name}' מהשרת? פעולה זו אינה הפיכה.", "אישור מחיקה",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            await Task.Run(() => { if (item.IsDirectory) _client.DeleteDirectory(item.FullPath); else _client.DeleteFile(item.FullPath); });
            await LoadDirectoryAsync(_currentPath);
        }
        catch (Exception ex) { StatusText.Text = "המחיקה נכשלה: " + ex.Message; }
    }

    private static string Combine(string directory, string name) =>
        (directory.TrimEnd('/') + "/" + name).Replace("//", "/");
}
