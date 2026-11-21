using System.Windows;
using Microsoft.Win32;
using NodeBB.SSHManager.Models;
using NodeBB.SSHManager.Services;

namespace NodeBB.SSHManager.Views;

public partial class BackupWindow : Window
{
    private readonly RemoteBackupService _service;
    private readonly NodeBbInstallation _installation;
    private readonly SshServerSession _session;
    private readonly CredentialVaultService _credentialVault = new();
    private readonly ScheduledBackupService _scheduledBackupService = new();

    public BackupWindow(SshServerSession session, NodeBbInstallation installation)
    {
        InitializeComponent();
        _session = session;
        _installation = installation;
        _service = new RemoteBackupService(session);
        InstallationLabel.Text = $"{session.Profile.Name} · {_installation.Path}";
        EncryptionFields.Visibility = EncryptBackupCheckBox.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        ScheduleDirectoryBox.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "NodeBB Backups");
        RefreshScheduledBackups();
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

        var filePrefix = LocalBackupRetentionService.BuildFilePrefix(_session.Profile.Name, _session.Profile.Id, kind);
        var extension = encrypted ? ".nbbbackup" : ".tar.gz";
        var dialog = new SaveFileDialog
        {
            Title = "שמירת גיבוי NodeBB",
            FileName = $"{filePrefix}-{DateTime.Now:yyyyMMdd-HHmm}{extension}",
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

            var keepText = (RetentionComboBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag?.ToString();
            var keepCount = int.TryParse(keepText, out var parsedKeep) ? parsedKeep : 0;
            var removed = keepCount > 0
                ? LocalBackupRetentionService.ApplyRetention(Path.GetDirectoryName(dialog.FileName)!, filePrefix, keepCount)
                : 0;
            StatusText.Text = encrypted ? "הגיבוי הוצפן ונשמר בהצלחה." : "הגיבוי נשמר בהצלחה.";
            if (removed > 0) StatusText.Text += $" הוסרו {removed} גיבויים ישנים לפי מדיניות השמירה.";
            MessageBox.Show(this, $"הגיבוי נשמר במחשב:\n{dialog.FileName}\n\nסוג: {(encrypted ? "מוצפן" : "ללא הצפנה")}\nגיבויים ישנים שהוסרו: {removed}",
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


    private void RefreshScheduledBackups()
    {
        try
        {
            ScheduledBackupsList.ItemsSource = _scheduledBackupService.GetSchedules(_session.Profile.Id, _installation.Path);
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Instance.Error("ScheduledBackup", "ScheduleListLoadFailed", ex, _session.Profile.Name);
            StatusText.Text = "לא ניתן לטעון את רשימת הגיבויים המתוזמנים: " + ex.Message;
        }
    }

    private void BrowseScheduleFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "בחירת תיקיית גיבויים מתוזמנים", Multiselect = false };
        if (Directory.Exists(ScheduleDirectoryBox.Text)) dialog.FolderName = ScheduleDirectoryBox.Text;
        if (dialog.ShowDialog(this) == true) ScheduleDirectoryBox.Text = dialog.FolderName;
    }

    private void CreateScheduledBackup_Click(object sender, RoutedEventArgs e)
    {
        var password = ScheduleEncryptionPasswordBox.Password;
        if (password.Length < 12)
        {
            MessageBox.Show(this, "סיסמת ההצפנה חייבת להכיל לפחות 12 תווים.", "סיסמה קצרה",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!string.Equals(password, ScheduleEncryptionConfirmBox.Password, StringComparison.Ordinal))
        {
            MessageBox.Show(this, "סיסמאות ההצפנה אינן תואמות.", "אימות סיסמה",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (_credentialVault.Get(_session.Profile.Id) is null)
        {
            MessageBox.Show(this,
                "כדי שגיבוי יפעל ללא חלון התחברות, יש להתחבר מחדש לשרת ולבחור באפשרות שמירת פרטי ההתחברות בכספת Windows. גם מפתח SSH ללא סיסמה ניתן לשמור כפרט התחברות ריק.",
                "נדרשים פרטי התחברות שמורים", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            var frequencyTag = (ScheduleFrequencyComboBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag?.ToString();
            var kindTag = (ScheduleKindComboBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag?.ToString();
            var dayTag = (ScheduleDayComboBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag?.ToString();
            if (!Enum.TryParse<ScheduledBackupFrequency>(frequencyTag, out var frequency) ||
                !Enum.TryParse<RemoteBackupKind>(kindTag, out var kind) ||
                !Enum.TryParse<DayOfWeek>(dayTag, out var day))
                throw new InvalidDataException("יש לבחור תדירות, יום וסוג גיבוי תקינים.");

            var keepText = (RetentionComboBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag?.ToString();
            var keepCount = int.TryParse(keepText, out var parsedKeep) ? parsedKeep : 0;
            var schedule = new ScheduledBackupDefinition
            {
                ProfileId = _session.Profile.Id,
                ProfileName = _session.Profile.Name,
                InstallationPath = _installation.Path,
                DestinationDirectory = ScheduleDirectoryBox.Text.Trim(),
                BackupKind = kind,
                RetentionCount = keepCount,
                IncludeDatabase = ScheduleIncludeDatabaseCheckBox.IsChecked == true,
                Frequency = frequency,
                DayOfWeek = day,
                LocalTime = ScheduleTimeBox.Text.Trim()
            };
            var executable = Environment.ProcessPath
                ?? throw new InvalidOperationException("לא ניתן לזהות את קובץ ההפעלה הנוכחי.");
            _scheduledBackupService.Create(schedule, password, executable);
            ScheduleEncryptionPasswordBox.Clear();
            ScheduleEncryptionConfirmBox.Clear();
            RefreshScheduledBackups();
            StatusText.Text = "תזמון הגיבוי נוצר בהצלחה במתזמן המשימות של Windows.";
            MessageBox.Show(this,
                "התזמון נוצר. Windows יפעיל גיבוי מוצפן בשעה שנבחרה, גם כשהתוכנה סגורה, כל עוד המשתמש מחובר למחשב. פרטי SSH וסיסמת ההצפנה אינם נכתבים בשורת הפקודה או בקובץ התזמון.",
                "התזמון נשמר", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Instance.Error("ScheduledBackup", "ScheduleCreateFailed", ex, _session.Profile.Name);
            MessageBox.Show(this, ex.Message, "יצירת תזמון נכשלה", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            ScheduleEncryptionPasswordBox.Clear();
            ScheduleEncryptionConfirmBox.Clear();
        }
    }

    private async void RunScheduledBackupNow_Click(object sender, RoutedEventArgs e)
    {
        if (ScheduledBackupsList.SelectedItem is not ScheduledBackupDefinition schedule)
        {
            MessageBox.Show(this, "בחר תזמון להפעלה.", "לא נבחר תזמון", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (sender is System.Windows.Controls.Button button) button.IsEnabled = false;
        StatusText.Text = "מפעיל גיבוי מתוזמן כעת...";
        try
        {
            await ScheduledBackupRunner.RunAsync(schedule.Id);
            RefreshScheduledBackups();
            StatusText.Text = "הגיבוי המתוזמן הושלם בהצלחה.";
            MessageBox.Show(this, "הגיבוי הושלם. קובצי הגיבוי נשמרו כשהם מוצפנים בתיקייה שהוגדרה בתזמון.",
                "הגיבוי הושלם", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Instance.Error("ScheduledBackup", "ManualScheduledRunFailed", ex, _session.Profile.Name);
            RefreshScheduledBackups();
            StatusText.Text = "הפעלת הגיבוי המתוזמן נכשלה: " + ex.Message;
            MessageBox.Show(this, ex.Message, "הפעלת גיבוי נכשלה", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            if (sender is System.Windows.Controls.Button actionButton) actionButton.IsEnabled = true;
        }
    }

    private void DeleteScheduledBackup_Click(object sender, RoutedEventArgs e)
    {
        if (ScheduledBackupsList.SelectedItem is not ScheduledBackupDefinition schedule)
        {
            MessageBox.Show(this, "בחר תזמון להסרה.", "לא נבחר תזמון", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (MessageBox.Show(this, "להסיר את התזמון ואת סיסמת ההצפנה השמורה שלו? קובצי הגיבוי שכבר נוצרו לא יימחקו.",
            "אישור הסרת תזמון", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        try
        {
            _scheduledBackupService.Remove(schedule.Id);
            RefreshScheduledBackups();
            StatusText.Text = "התזמון הוסר. גיבויים קיימים נשמרו.";
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Instance.Error("ScheduledBackup", "ScheduleRemoveFailed", ex, _session.Profile.Name);
            MessageBox.Show(this, ex.Message, "הסרת תזמון נכשלה", MessageBoxButton.OK, MessageBoxImage.Error);
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


    private async void RestoreFull_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "בחירת גיבוי התקנה מלאה של NodeBB",
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
            "שחזור מלא יחליף את כל תיקיית התקנת NodeBB, כולל קוד, תוספים, ערכות נושא, קבצים והגדרות. NodeBB ייעצר וינסה לעלות מחדש לאחר ההחלפה. מסד הנתונים עצמו אינו כלול ואינו משתנה; התוכנה תוודא שסוג המסד וכתובת/שם מסד היעד תואמים להתקנה הנוכחית. עותק מלא של ההתקנה הקיימת יישמר עד שהגרסה המשוחזרת תעלה בהצלחה. אם לא ניתן יהיה לשחזר בעלות והרשאות בבטחה, הפעולה תיעצר ללא שינוי. להמשיך?",
            "אישור שחזור התקנה מלאה", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        IsEnabled = false;
        StatusText.Text = "מכין, מאמת ומעלה את ארכיון ההתקנה...";
        var tempRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NodeBB SSH Manager", "Temp");
        var decrypted = Path.Combine(tempRoot, "full-restore-" + Guid.NewGuid().ToString("N") + ".tar.gz");
        var archivePath = dialog.FileName;
        try
        {
            if (encrypted)
            {
                Directory.CreateDirectory(tempRoot);
                await BackupEncryptionService.DecryptFileAsync(dialog.FileName, decrypted, password);
                archivePath = decrypted;
            }

            StatusText.Text = "מאמת את כל קובצי ההתקנה, עוצר את NodeBB ומכין החלפה בטוחה...";
            await new RemoteBackupRestoreService(_session).RestoreFullInstallationAsync(_installation, archivePath);
            StatusText.Text = "התקנת NodeBB המלאה שוחזרה והופעלה בהצלחה.";
            MessageBox.Show(this,
                "כל קובצי התקנת NodeBB שוחזרו והשרת הופעל מחדש. מסד הנתונים לא שוחזר ולא שונה.",
                "שחזור התקנה מלאה הושלם", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Instance.Error("BackupUI", "FullRestoreFailed", ex, _session.Profile.Name);
            StatusText.Text = "שחזור ההתקנה המלאה נכשל: " + ex.Message;
            MessageBox.Show(this, ex.Message, "שגיאה בשחזור התקנה מלאה", MessageBoxButton.OK, MessageBoxImage.Error);
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
