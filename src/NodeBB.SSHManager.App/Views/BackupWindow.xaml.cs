using System.Windows;
using Microsoft.Win32;
using NodeBB.SSHManager.Services;

namespace NodeBB.SSHManager.Views;

public partial class BackupWindow : Window
{
    private readonly RemoteBackupService _service;
    private readonly NodeBbInstallation _installation;
    private readonly SshServerSession _session;

    public BackupWindow(SshServerSession session, NodeBbInstallation installation)
    {
        InitializeComponent();
        _session = session;
        _installation = installation;
        _service = new RemoteBackupService(session);
        InstallationLabel.Text = $"{session.Profile.Name} · {_installation.Path}";
    }

    private async void FullBackup_Click(object sender, RoutedEventArgs e) =>
        await CreateBackupAsync(RemoteBackupKind.FullInstallation);

    private async void EssentialBackup_Click(object sender, RoutedEventArgs e) =>
        await CreateBackupAsync(RemoteBackupKind.ConfigurationAndUploads);

    private async Task CreateBackupAsync(RemoteBackupKind kind)
    {
        var suffix = kind == RemoteBackupKind.FullInstallation ? "full" : "config-uploads";
        var dialog = new SaveFileDialog
        {
            Title = "שמירת גיבוי NodeBB",
            FileName = $"NodeBB-{suffix}-{DateTime.Now:yyyyMMdd-HHmm}.tar.gz",
            Filter = "ארכיון GZip TAR|*.tar.gz",
            DefaultExt = ".tar.gz",
            AddExtension = true,
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(this) != true) return;

        IsEnabled = false;
        StatusText.Text = "יוצר גיבוי בשרת ומוריד אותו למחשב...";
        try
        {
            await _service.DownloadBackupAsync(_installation, kind, dialog.FileName);
            StatusText.Text = "הגיבוי נשמר בהצלחה.";
            MessageBox.Show(this, $"הגיבוי נשמר במחשב:\n{dialog.FileName}", "הגיבוי הושלם",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Instance.Error("BackupUI", "BackupDownloadFailed", ex, _session.Profile.Name);
            StatusText.Text = "יצירת הגיבוי נכשלה: " + ex.Message;
            MessageBox.Show(this, ex.Message, "שגיאה ביצירת גיבוי", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { IsEnabled = true; }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
