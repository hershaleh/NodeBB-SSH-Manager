using NodeBB.SSHManager.Localization;
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
            MessageBox.Show(this, $"גיבוי מסד הנתונים נשמר כשהוא מוצפן:\n{dialog.FileName}\n\nשמור את סיסמת ההצפנה במקום בטוח.",
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

    private async void PostgresBackup_Click(object sender, RoutedEventArgs e)
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
            Title = "שמירת גיבוי PostgreSQL",
            FileName = $"NodeBB-PostgreSQL-{DateTime.Now:yyyyMMdd-HHmm}.nbbbackup",
            Filter = "גיבוי PostgreSQL מוצפן|*.nbbbackup", DefaultExt = ".nbbbackup",
            AddExtension = true, OverwritePrompt = true
        };
        if (dialog.ShowDialog(this) != true) return;
        PostgresBackupButton.IsEnabled = false; RefreshButton.IsEnabled = false;
        var tempRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NodeBB SSH Manager", "Temp");
        var plain = Path.Combine(tempRoot, $"postgres-{Guid.NewGuid():N}.dump");
        try
        {
            Directory.CreateDirectory(tempRoot);
            StatusText.Text = "יוצר גיבוי PostgreSQL בשרת...";
            await new PostgresDatabaseBackupService(_session).DownloadBackupAsync(_installation, plain);
            StatusText.Text = "קובץ הגיבוי התקבל. מצפין במחשב...";
            await BackupEncryptionService.EncryptFileAsync(plain, dialog.FileName, password);
            StatusText.Text = "גיבוי PostgreSQL הוצפן ונשמר בהצלחה.";
            MessageBox.Show(this, $"גיבוי PostgreSQL נשמר כשהוא מוצפן:\n{dialog.FileName}\n\nשמור את סיסמת ההצפנה במקום בטוח.",
                "הגיבוי הושלם", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Instance.Error("DatabaseUI", "PostgresBackupFailed", ex, _session.Profile.Name);
            StatusText.Text = "גיבוי PostgreSQL נכשל: " + ex.Message;
            MessageBox.Show(this, ex.Message, "שגיאה בגיבוי מסד הנתונים", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            try { if (File.Exists(plain)) File.Delete(plain); } catch { }
            BackupPasswordBox.Clear(); BackupPasswordConfirmBox.Clear();
            PostgresBackupButton.IsEnabled = true; RefreshButton.IsEnabled = true;
        }
    }

    private async void RedisBackup_Click(object sender, RoutedEventArgs e)
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
        var confirm = MessageBox.Show(this,
            "Redis יוצר קובץ RDB של כל שרת Redis, כולל כל מסדי הנתונים הלוגיים והמידע של שירותים נוספים המשתמשים באותו שרת. אין כאן סינון למסד NodeBB בלבד. האם להמשיך?",
            "אישור גיבוי Redis מלא", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes) return;
        var dialog = new SaveFileDialog
        {
            Title = "שמירת גיבוי Redis",
            FileName = $"NodeBB-Redis-Full-{DateTime.Now:yyyyMMdd-HHmm}.nbbbackup",
            Filter = "גיבוי Redis מוצפן|*.nbbbackup", DefaultExt = ".nbbbackup", AddExtension = true, OverwritePrompt = true
        };
        if (dialog.ShowDialog(this) != true) return;
        RedisBackupButton.IsEnabled = false; DatabaseBackupButton.IsEnabled = false; RefreshButton.IsEnabled = false;
        var tempRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NodeBB SSH Manager", "Temp");
        var plain = Path.Combine(tempRoot, $"redis-{Guid.NewGuid():N}.rdb");
        try
        {
            Directory.CreateDirectory(tempRoot);
            StatusText.Text = "יוצר תמונת RDB של שרת Redis...";
            await new RedisDatabaseBackupService(_session).DownloadBackupAsync(_installation, plain);
            StatusText.Text = "קובץ RDB התקבל. מצפין במחשב...";
            await BackupEncryptionService.EncryptFileAsync(plain, dialog.FileName, password);
            StatusText.Text = "גיבוי Redis הוצפן ונשמר.";
            MessageBox.Show(this, $"גיבוי Redis מלא נשמר כשהוא מוצפן:\n{dialog.FileName}\n\nהגיבוי כולל את כל מסדי Redis בשרת. שמור את הסיסמה במקום בטוח.",
                "הגיבוי הושלם", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Instance.Error("DatabaseUI", "RedisBackupFailed", ex, _session.Profile.Name);
            StatusText.Text = "גיבוי Redis נכשל: " + ex.Message;
            MessageBox.Show(this, ex.Message, "שגיאה בגיבוי Redis", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            try { if (File.Exists(plain)) File.Delete(plain); } catch { }
            BackupPasswordBox.Clear(); BackupPasswordConfirmBox.Clear();
            RedisBackupButton.IsEnabled = true; DatabaseBackupButton.IsEnabled = true; RefreshButton.IsEnabled = true;
        }
    }

    private async void DatabaseRestore_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "בחירת גיבוי MongoDB מוצפן", Filter = "גיבוי NodeBB מוצפן|*.nbbbackup|כל הקבצים|*.*", CheckFileExists = true, Multiselect = false };
        if (dialog.ShowDialog(this) != true) return;
        var password = RestorePasswordBox.Password;
        if (password.Length < 12) { MessageBox.Show(this, "יש להזין סיסמה בת 12 תווים לפחות.", "סיסמת גיבוי", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        var confirmation = MessageBox.Show(this, "כל תוכן מסד MongoDB של NodeBB יוחלף בתוכן הגיבוי. לפני כן ייווצר גיבוי חירום של כל מסד היעד. אם הייבוא ייכשל, המערכת תנסה לשחזר את מסד הנתונים הקודם. האם להמשיך?", "אישור שחזור", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (confirmation != MessageBoxResult.Yes) return;
        DatabaseRestoreButton.IsEnabled = false; DatabaseBackupButton.IsEnabled = false; RefreshButton.IsEnabled = false;
        StatusText.Text = "מפענח ובודק את קובץ הגיבוי...";
        var tempRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NodeBB SSH Manager", "Temp");
        var decrypted = Path.Combine(tempRoot, "restore-" + Guid.NewGuid().ToString("N") + ".archive.gz");
        try
        {
            Directory.CreateDirectory(tempRoot);
            await BackupEncryptionService.DecryptFileAsync(dialog.FileName, decrypted, password);
            await MongoDatabaseRestoreService.ValidateArchiveAsync(decrypted);
            StatusText.Text = "הקובץ תקין. יוצר גיבוי חירום ומבצע שחזור...";
            await new MongoDatabaseRestoreService(_session).RestoreAsync(_installation, decrypted);
            StatusText.Text = "השחזור הושלם בהצלחה.";
            MessageBox.Show(this, "מסד MongoDB שוחזר בהצלחה.", "השחזור הושלם", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Instance.Error("DatabaseUI", "MongoRestoreFailed", ex, _session.Profile.Name);
            StatusText.Text = "השחזור נכשל: " + ex.Message;
            MessageBox.Show(this, ex.Message, "שגיאה בשחזור מסד הנתונים", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            try { if (File.Exists(decrypted)) File.Delete(decrypted); } catch { }
            RestorePasswordBox.Clear(); DatabaseRestoreButton.IsEnabled = true; DatabaseBackupButton.IsEnabled = true; RefreshButton.IsEnabled = true;
        }
    }


    private async void PostgresRestore_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "בחירת גיבוי PostgreSQL מוצפן",
            Filter = "גיבוי NodeBB מוצפן|*.nbbbackup|כל הקבצים|*.*",
            CheckFileExists = true, Multiselect = false
        };
        if (dialog.ShowDialog(this) != true) return;
        var password = RestorePasswordBox.Password;
        if (password.Length < 12)
        {
            MessageBox.Show(this, "יש להזין סיסמה בת 12 תווים לפחות.", "סיסמת גיבוי", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var confirmation = MessageBox.Show(this,
            "השחזור יחליף את האובייקטים הכלולים בארכיון בתוך מסד PostgreSQL שהוגדר ב-NodeBB. אובייקטים שאינם כלולים בארכיון עשויים להישאר. לפני השינוי ייווצר גיבוי חירום של מסד היעד; אם השחזור ייכשל, המערכת תנסה להחזיר את המצב הקודם. להמשיך?",
            "אישור שחזור PostgreSQL", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (confirmation != MessageBoxResult.Yes) return;

        PostgresRestoreButton.IsEnabled = false;
        PostgresBackupButton.IsEnabled = false;
        RefreshButton.IsEnabled = false;
        StatusText.Text = "מפענח ובודק את קובץ הגיבוי...";
        var tempRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NodeBB SSH Manager", "Temp");
        var decrypted = Path.Combine(tempRoot, "restore-" + Guid.NewGuid().ToString("N") + ".dump");
        try
        {
            Directory.CreateDirectory(tempRoot);
            await BackupEncryptionService.DecryptFileAsync(dialog.FileName, decrypted, password);
            await PostgresDatabaseRestoreService.ValidateDumpAsync(decrypted);
            StatusText.Text = "קובץ הגיבוי תקין. יוצר גיבוי חירום ומבצע שחזור...";
            await new PostgresDatabaseRestoreService(_session).RestoreAsync(_installation, decrypted);
            StatusText.Text = "שחזור PostgreSQL הושלם בהצלחה.";
            MessageBox.Show(this, "אובייקטי מסד PostgreSQL שוחזרו בהצלחה.", "השחזור הושלם", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Instance.Error("DatabaseUI", "PostgresRestoreFailed", ex, _session.Profile.Name);
            StatusText.Text = "שחזור PostgreSQL נכשל: " + ex.Message;
            MessageBox.Show(this, ex.Message, "שגיאה בשחזור PostgreSQL", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            try { if (File.Exists(decrypted)) File.Delete(decrypted); } catch { }
            RestorePasswordBox.Clear();
            PostgresRestoreButton.IsEnabled = true;
            PostgresBackupButton.IsEnabled = true;
            RefreshButton.IsEnabled = true;
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
            var isMongo = string.Equals(info.Type, "mongo", StringComparison.OrdinalIgnoreCase);
            var isRedis = string.Equals(info.Type, "redis", StringComparison.OrdinalIgnoreCase);
            var isPostgres = string.Equals(info.Type, "postgres", StringComparison.OrdinalIgnoreCase);
            DatabaseBackupButton.Visibility = isMongo ? Visibility.Visible : Visibility.Collapsed;
            MongoRestorePanel.Visibility = isMongo || isPostgres ? Visibility.Visible : Visibility.Collapsed;
            DatabaseRestoreButton.Visibility = isMongo ? Visibility.Visible : Visibility.Collapsed;
            PostgresRestoreButton.Visibility = isPostgres ? Visibility.Visible : Visibility.Collapsed;
            RedisBackupButton.Visibility = isRedis ? Visibility.Visible : Visibility.Collapsed;
            PostgresBackupButton.Visibility = isPostgres ? Visibility.Visible : Visibility.Collapsed;
            RedisBackupNote.Visibility = isRedis ? Visibility.Visible : Visibility.Collapsed;
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
