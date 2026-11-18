using System.Windows;
using Microsoft.Win32;
using NodeBB.SSHManager.Services;

namespace NodeBB.SSHManager.Views;

public partial class DatabaseManagerWindow : Window
{
    private readonly NodeBbDatabaseService _service;
    private readonly NodeBbInstallation _installation;
    private readonly SshServerSession _session;

    public DatabaseManagerWindow(SshServerSession session, NodeBbInstallation installation)
    {
        InitializeComponent();
        _session = session;
        _installation = installation;
        _service = new NodeBbDatabaseService(session);
        InstallationLabel.Text = $"{session.Profile.Name} · {_installation.Path}";
        Loaded += async (_, _) => await RefreshAsync();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async void DatabaseBackup_Click(object sender, RoutedEventArgs e)
    {
        var password = BackupPasswordBox.Password;
        if (password.Length < 12)
        {
            MessageBox.Show(this, "סיסמת ההצפנה חייבת להכיל לפחות 12 תווים.", "סיסמה קצרה", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!string.Equals(password, BackupPasswordConfirmBox.Password, StringComparison.Ordinal))
        {
            MessageBox.Show(this, "סיסמאות ההצפנה אינן תואמות.", "אימות סיסמה", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var dialog = new SaveFileDialog
        {
            Title = "שמירת גיבוי MongoDB",
            FileName = $"NodeBB-MongoDB-{DateTime.Now:yyyyMMdd-HHmm}.nbbbackup",
            Filter = "גיבוי MongoDB מוצפן|*.nbbbackup",
            DefaultExt = ".nbbbackup", AddExtension = true, OverwritePrompt = true
        };
        if (dialog.ShowDialog(this) != true) return;
        DatabaseBackupButton.IsEnabled = false; RefreshButton.IsEnabled = false;
        StatusText.Text = "יוצר גיבוי MongoDB בשרת...";
        var tempRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NodeBB SSH Manager", "Temp");
        var plainBackup = Path.Combine(tempRoot, $"mongo-{Guid.NewGuid():N}.archive.gz");
        try
        {
            Directory.CreateDirectory(tempRoot);
            await new MongoDatabaseBackupService(_session).DownloadBackupAsync(_installation, plainBackup);
            StatusText.Text = "הגיבוי התקבל. מצפין אותו במחשב...";
            await BackupEncryptionService.EncryptFileAsync(plainBackup, dialog.FileName, password);
            StatusText.Text = "גיבוי MongoDB הוצפן ונשמר בהצלחה.";
            MessageBox.Show(this, $"גיבוי מסד הנתונים נשמר כשהוא מוצפן:
{dialog.FileName}

שמור את סיסמת ההצפנה במקום בטוח.",
                "הגיבוי הושלם", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Instance.Error("DatabaseUI", "MongoBackupFailed", ex, _session.Profile.Name);
            StatusText.Text = "גיבוי MongoDB נכשל: " + ex.Message;
            MessageBox.Show(this, ex.Message, "שגיאה בגיבוי מסד הנתונים", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            try { if (File.Exists(plainBackup)) File.Delete(plainBackup); } catch { }
            BackupPasswordBox.Clear(); BackupPasswordConfirmBox.Clear();
            DatabaseBackupButton.IsEnabled = true; RefreshButton.IsEnabled = true;
        }
    }

    private async Task RefreshAsync()
    {
        RefreshButton.IsEnabled = false;
        StatusText.Text = "קורא את config.json...";
        try
        {
            var info = await _service.ReadConfigurationAsync(_installation);
            TypeText.Text = info.Type;
            HostText.Text = info.Host;
            PortText.Text = info.Port;
            DatabaseNameText.Text = info.DatabaseName;
            UsernameText.Text = info.UsernameConfigured ? "כן" : "לא";
            PasswordText.Text = info.PasswordConfigured ? "כן" : "לא";
            UriText.Text = info.UriConfigured ? "כן (הערך עצמו מוסתר)" : "לא";
            StatusText.Text = "התצורה נקראה בהצלחה.";
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Instance.Error("DatabaseUI", "ConfigurationDisplayFailed", ex, _session.Profile.Name);
            StatusText.Text = "קריאת התצורה נכשלה: " + ex.Message;
        }
        finally { RefreshButton.IsEnabled = true; }
    }
}
