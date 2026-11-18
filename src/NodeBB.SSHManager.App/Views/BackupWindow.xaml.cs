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
        EncryptionFields.Visibility = EncryptBackupCheckBox.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void FullBackup_Click(object sender, RoutedEventArgs e) =>
        await CreateBackupAsync(RemoteBackupKind.FullInstallation);

    private async void EssentialBackup_Click(object sender, RoutedEventArgs e) =>
        await CreateBackupAsync(RemoteBackupKind.ConfigurationAndUploads);

    private void EncryptionOption_Changed(object sender, RoutedEventArgs e)
    {
        EncryptionFields.Visibility = EncryptBackupCheckBox.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task CreateBackupAsync(RemoteBackupKind kind)
    {
        var encrypted = EncryptBackupCheckBox.IsChecked == true;
        var password = EncryptionPasswordBox.Password;
        if (encrypted)
        {
            if (password.Length < 12)
            {
                MessageBox.Show(this, "סיסמת ההצפנה חייבת להכיל לפחות 12 תווים.", "סיסמה קצרה",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!string.Equals(password, EncryptionConfirmBox.Password, StringComparison.Ordinal))
            {
                MessageBox.Show(this, "סיסמאות ההצפנה אינן תואמות.", "אימות סיסמה",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }

        var suffix = kind == RemoteBackupKind.FullInstallation ? "full" : "config-uploads";
        var extension = encrypted ? ".nbbbackup" : ".tar.gz";
        var dialog = new SaveFileDialog
        {
            Title = "שמירת גיבוי NodeBB",
            FileName = $"NodeBB-{suffix}-{DateTime.Now:yyyyMMdd-HHmm}{extension}",
            Filter = encrypted ? "גיבוי NodeBB מוצפן|*.nbbbackup" : "ארכיון GZip TAR|*.tar.gz",
            DefaultExt = extension,
            AddExtension = true,
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(this) != true) return;

        IsEnabled = false;
        StatusText.Text = "יוצר גיבוי בשרת ומוריד אותו למחשב...";
        try
        {
            if (encrypted)
            {
                var temporaryPlain = Path.Combine(Path.GetTempPath(), $"nodebb-backup-{Guid.NewGuid():N}.tar.gz");
                try
                {
                    await _service.DownloadBackupAsync(_installation, kind, temporaryPlain);
                    StatusText.Text = "מצפין את הגיבוי...";
                    await BackupEncryptionService.EncryptFileAsync(temporaryPlain, dialog.FileName, password);
                }
                finally { try { if (File.Exists(temporaryPlain)) File.Delete(temporaryPlain); } catch { } }
            }
            else
            {
                await _service.DownloadBackupAsync(_installation, kind, dialog.FileName);
            }

            StatusText.Text = encrypted ? "הגיבוי הוצפן ונשמר בהצלחה." : "הגיבוי נשמר בהצלחה.";
            MessageBox.Show(this, $"הגיבוי נשמר במחשב:\n{dialog.FileName}\n\nסוג: {(encrypted ? "מוצפן" : "ללא הצפנה")}",
                "הגיבוי הושלם", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Instance.Error("BackupUI", "BackupDownloadFailed", ex, _session.Profile.Name);
            StatusText.Text = "יצירת הגיבוי נכשלה: " + ex.Message;
            MessageBox.Show(this, ex.Message, "שגיאה ביצירת גיבוי", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            EncryptionPasswordBox.Clear();
            EncryptionConfirmBox.Clear();
            IsEnabled = true;
        }
    }


    private async void RestoreEssential_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "בחירת גיבוי הגדרות והעלאות",
            Filter = "גיבוי NodeBB מוצפן או TAR.GZ|*.nbbbackup;*.tar.gz|כל הקבצים|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true) return;

        var encrypted = dialog.FileName.EndsWith(".nbbbackup", StringComparison.OrdinalIgnoreCase);
        var password = RestorePasswordBox.Password;
        if (encrypted && password.Length < 12)
        {
            MessageBox.Show(this, "יש להזין את סיסמת ההצפנה (לפחות 12 תווים).", "סיסמת גיבוי",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (MessageBox.Show(this,
            "השחזור יחליף את config.json ואת תיקיית public/uploads אם היא כלולה בגיבוי. מסד הנתונים עצמו לא ישוחזר, וקובצי package/lock לא ישתנו. הגדרות חיבור מסד הנתונים יוחלפו רק אם סוג המסד והשרת/מסד היעד תואמים להתקנה הנוכחית. לפני השינוי תישמר עותק חירום של הקבצים הקיימים, והתוכנה תנסה להחזירם במקרה כשל. להמשיך?",
            "אישור שחזור קבצים", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        IsEnabled = false;
        StatusText.Text = "מכין ובודק את ארכיון הגיבוי...";
        var tempRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NodeBB SSH Manager", "Temp");
        var decrypted = Path.Combine(tempRoot, "file-restore-" + Guid.NewGuid().ToString("N") + ".tar.gz");
        var archivePath = dialog.FileName;
        try
        {
            if (encrypted)
            {
                Directory.CreateDirectory(tempRoot);
                await BackupEncryptionService.DecryptFileAsync(dialog.FileName, decrypted, password);
                archivePath = decrypted;
            }

            StatusText.Text = "בודק את הארכיון ומבצע שחזור בטוח...";
            await new RemoteBackupRestoreService(_session).RestoreAsync(_installation, archivePath);
            StatusText.Text = "שחזור ההגדרות וההעלאות הושלם בהצלחה.";
            MessageBox.Show(this,
                "config.json ותיקיית ההעלאות (אם נכללה בגיבוי) שוחזרו בהצלחה. מסד הנתונים וקובצי package/lock לא שונו.",
                "השחזור הושלם", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Instance.Error("BackupUI", "EssentialRestoreFailed", ex, _session.Profile.Name);
            StatusText.Text = "שחזור הקבצים נכשל: " + ex.Message;
            MessageBox.Show(this, ex.Message, "שגיאה בשחזור קבצים", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            try { if (File.Exists(decrypted)) File.Delete(decrypted); } catch { }
            RestorePasswordBox.Clear();
            EncryptionPasswordBox.Clear();
            EncryptionConfirmBox.Clear();
            IsEnabled = true;
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
