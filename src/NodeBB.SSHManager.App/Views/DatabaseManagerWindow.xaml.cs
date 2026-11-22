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
            MessageBox.Show(this, T("סיסמת ההצפנה חייבת להכיל לפחות 12 תווים."), T("סיסמה קצרה"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!string.Equals(password, BackupPasswordConfirmBox.Password, StringComparison.Ordinal))
        {
            MessageBox.Show(this, T("סיסמאות ההצפנה אינן תואמות."), T("אימות סיסמה"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var dialog = new SaveFileDialog
        {
            Title = T("שמירת גיבוי MongoDB"),
            FileName = $"NodeBB-MongoDB-{DateTime.Now:yyyyMMdd-HHmm}.nbbbackup",
            Filter = T("גיבוי MongoDB מוצפן|*.nbbbackup"),
            DefaultExt = ".nbbbackup", AddExtension = true, OverwritePrompt = true
        };
        if (dialog.ShowDialog(this) != true) return;
        DatabaseBackupButton.IsEnabled = false; RefreshButton.IsEnabled = false;
        StatusText.Text = T("יוצר גיבוי MongoDB בשרת...");
        var tempRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NodeBB SSH Manager", "Temp");
        var plainBackup = Path.Combine(tempRoot, $"mongo-{Guid.NewGuid():N}.archive.gz");
        try
        {
            Directory.CreateDirectory(tempRoot);
            await new MongoDatabaseBackupService(_session).DownloadBackupAsync(_installation, plainBackup);
            StatusText.Text = T("הגיבוי התקבל. מצפין אותו במחשב...");
            await BackupEncryptionService.EncryptFileAsync(plainBackup, dialog.FileName, password);
            StatusText.Text = T("גיבוי MongoDB הוצפן ונשמר בהצלחה.");
            MessageBox.Show(this, F($"גיבוי מסד הנתונים נשמר כשהוא מוצפן:\n{dialog.FileName}\n\nשמור את סיסמת ההצפנה במקום בטוח."),
                T("הגיבוי הושלם"), MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Instance.Error("DatabaseUI", "MongoBackupFailed", ex, _session.Profile.Name);
            StatusText.Text = T("גיבוי MongoDB נכשל: ") + ex.Message;
            MessageBox.Show(this, ex.Message, T("שגיאה בגיבוי מסד הנתונים"), MessageBoxButton.OK, MessageBoxImage.Error);
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
            MessageBox.Show(this, T("סיסמת ההצפנה חייבת להכיל לפחות 12 תווים."), T("סיסמה קצרה"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!string.Equals(password, BackupPasswordConfirmBox.Password, StringComparison.Ordinal))
        {
            MessageBox.Show(this, T("סיסמאות ההצפנה אינן תואמות."), T("אימות סיסמה"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var dialog = new SaveFileDialog
        {
            Title = T("שמירת גיבוי PostgreSQL"),
            FileName = $"NodeBB-PostgreSQL-{DateTime.Now:yyyyMMdd-HHmm}.nbbbackup",
            Filter = T("גיבוי PostgreSQL מוצפן|*.nbbbackup"), DefaultExt = ".nbbbackup",
            AddExtension = true, OverwritePrompt = true
        };
        if (dialog.ShowDialog(this) != true) return;
        PostgresBackupButton.IsEnabled = false; RefreshButton.IsEnabled = false;
        var tempRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NodeBB SSH Manager", "Temp");
        var plain = Path.Combine(tempRoot, $"postgres-{Guid.NewGuid():N}.dump");
        try
        {
            Directory.CreateDirectory(tempRoot);
            StatusText.Text = T("יוצר גיבוי PostgreSQL בשרת...");
            await new PostgresDatabaseBackupService(_session).DownloadBackupAsync(_installation, plain);
            StatusText.Text = T("קובץ הגיבוי התקבל. מצפין במחשב...");
            await BackupEncryptionService.EncryptFileAsync(plain, dialog.FileName, password);
            StatusText.Text = T("גיבוי PostgreSQL הוצפן ונשמר בהצלחה.");
            MessageBox.Show(this, F($"גיבוי PostgreSQL נשמר כשהוא מוצפן:\n{dialog.FileName}\n\nשמור את סיסמת ההצפנה במקום בטוח."),
                T("הגיבוי הושלם"), MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Instance.Error("DatabaseUI", "PostgresBackupFailed", ex, _session.Profile.Name);
            StatusText.Text = T("גיבוי PostgreSQL נכשל: ") + ex.Message;
            MessageBox.Show(this, ex.Message, T("שגיאה בגיבוי מסד הנתונים"), MessageBoxButton.OK, MessageBoxImage.Error);
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
            MessageBox.Show(this, T("סיסמת ההצפנה חייבת להכיל לפחות 12 תווים."), T("סיסמה קצרה"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!string.Equals(password, BackupPasswordConfirmBox.Password, StringComparison.Ordinal))
        {
            MessageBox.Show(this, T("סיסמאות ההצפנה אינן תואמות."), T("אימות סיסמה"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var confirm = MessageBox.Show(this,
            T("Redis יוצר קובץ RDB של כל שרת Redis, כולל כל מסדי הנתונים הלוגיים והמידע של שירותים נוספים המשתמשים באותו שרת. אין כאן סינון למסד NodeBB בלבד. האם להמשיך?"),
            T("אישור גיבוי Redis מלא"), MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes) return;
        var dialog = new SaveFileDialog
        {
            Title = T("שמירת גיבוי Redis"),
            FileName = $"NodeBB-Redis-Full-{DateTime.Now:yyyyMMdd-HHmm}.nbbbackup",
            Filter = T("גיבוי Redis מוצפן|*.nbbbackup"), DefaultExt = ".nbbbackup", AddExtension = true, OverwritePrompt = true
        };
        if (dialog.ShowDialog(this) != true) return;
        RedisBackupButton.IsEnabled = false; DatabaseBackupButton.IsEnabled = false; RefreshButton.IsEnabled = false;
        var tempRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NodeBB SSH Manager", "Temp");
        var plain = Path.Combine(tempRoot, $"redis-{Guid.NewGuid():N}.rdb");
        try
        {
            Directory.CreateDirectory(tempRoot);
            StatusText.Text = T("יוצר תמונת RDB של שרת Redis...");
            await new RedisDatabaseBackupService(_session).DownloadBackupAsync(_installation, plain);
            StatusText.Text = T("קובץ RDB התקבל. מצפין במחשב...");
            await BackupEncryptionService.EncryptFileAsync(plain, dialog.FileName, password);
            StatusText.Text = T("גיבוי Redis הוצפן ונשמר.");
            MessageBox.Show(this, F($"גיבוי Redis מלא נשמר כשהוא מוצפן:\n{dialog.FileName}\n\nהגיבוי כולל את כל מסדי Redis בשרת. שמור את הסיסמה במקום בטוח."),
                T("הגיבוי הושלם"), MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Instance.Error("DatabaseUI", "RedisBackupFailed", ex, _session.Profile.Name);
            StatusText.Text = T("גיבוי Redis נכשל: ") + ex.Message;
            MessageBox.Show(this, ex.Message, T("שגיאה בגיבוי Redis"), MessageBoxButton.OK, MessageBoxImage.Error);
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
        var dialog = new OpenFileDialog { Title = T("בחירת גיבוי MongoDB מוצפן"), Filter = T("גיבוי NodeBB מוצפן|*.nbbbackup|כל הקבצים|*.*"), CheckFileExists = true, Multiselect = false };
        if (dialog.ShowDialog(this) != true) return;
        var password = RestorePasswordBox.Password;
        if (password.Length < 12) { MessageBox.Show(this, T("יש להזין סיסמה בת 12 תווים לפחות."), T("סיסמת גיבוי"), MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        var confirmation = MessageBox.Show(this, T("כל תוכן מסד MongoDB של NodeBB יוחלף בתוכן הגיבוי. לפני כן ייווצר גיבוי חירום של כל מסד היעד. אם הייבוא ייכשל, המערכת תנסה לשחזר את מסד הנתונים הקודם. האם להמשיך?"), T("אישור שחזור"), MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (confirmation != MessageBoxResult.Yes) return;
        DatabaseRestoreButton.IsEnabled = false; DatabaseBackupButton.IsEnabled = false; RefreshButton.IsEnabled = false;
        StatusText.Text = T("מפענח ובודק את קובץ הגיבוי...");
        var tempRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NodeBB SSH Manager", "Temp");
        var decrypted = Path.Combine(tempRoot, "restore-" + Guid.NewGuid().ToString("N") + ".archive.gz");
        try
        {
            Directory.CreateDirectory(tempRoot);
            await BackupEncryptionService.DecryptFileAsync(dialog.FileName, decrypted, password);
            await MongoDatabaseRestoreService.ValidateArchiveAsync(decrypted);
            StatusText.Text = T("הקובץ תקין. יוצר גיבוי חירום ומבצע שחזור...");
            await new MongoDatabaseRestoreService(_session).RestoreAsync(_installation, decrypted);
            StatusText.Text = T("השחזור הושלם בהצלחה.");
            MessageBox.Show(this, T("מסד MongoDB שוחזר בהצלחה."), T("השחזור הושלם"), MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Instance.Error("DatabaseUI", "MongoRestoreFailed", ex, _session.Profile.Name);
            StatusText.Text = T("השחזור נכשל: ") + ex.Message;
            MessageBox.Show(this, ex.Message, T("שגיאה בשחזור מסד הנתונים"), MessageBoxButton.OK, MessageBoxImage.Error);
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
            Title = T("בחירת גיבוי PostgreSQL מוצפן"),
            Filter = T("גיבוי NodeBB מוצפן|*.nbbbackup|כל הקבצים|*.*"),
            CheckFileExists = true, Multiselect = false
        };
        if (dialog.ShowDialog(this) != true) return;
        var password = RestorePasswordBox.Password;
        if (password.Length < 12)
        {
            MessageBox.Show(this, T("יש להזין סיסמה בת 12 תווים לפחות."), T("סיסמת גיבוי"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var confirmation = MessageBox.Show(this,
            T("השחזור יחליף את האובייקטים הכלולים בארכיון בתוך מסד PostgreSQL שהוגדר ב-NodeBB. אובייקטים שאינם כלולים בארכיון עשויים להישאר. לפני השינוי ייווצר גיבוי חירום של מסד היעד; אם השחזור ייכשל, המערכת תנסה להחזיר את המצב הקודם. להמשיך?"),
            T("אישור שחזור PostgreSQL"), MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (confirmation != MessageBoxResult.Yes) return;

        PostgresRestoreButton.IsEnabled = false;
        PostgresBackupButton.IsEnabled = false;
        RefreshButton.IsEnabled = false;
        StatusText.Text = T("מפענח ובודק את קובץ הגיבוי...");
        var tempRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NodeBB SSH Manager", "Temp");
        var decrypted = Path.Combine(tempRoot, "restore-" + Guid.NewGuid().ToString("N") + ".dump");
        try
        {
            Directory.CreateDirectory(tempRoot);
            await BackupEncryptionService.DecryptFileAsync(dialog.FileName, decrypted, password);
            await PostgresDatabaseRestoreService.ValidateDumpAsync(decrypted);
            StatusText.Text = T("קובץ הגיבוי תקין. יוצר גיבוי חירום ומבצע שחזור...");
            await new PostgresDatabaseRestoreService(_session).RestoreAsync(_installation, decrypted);
            StatusText.Text = T("שחזור PostgreSQL הושלם בהצלחה.");
            MessageBox.Show(this, T("אובייקטי מסד PostgreSQL שוחזרו בהצלחה."), T("השחזור הושלם"), MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Instance.Error("DatabaseUI", "PostgresRestoreFailed", ex, _session.Profile.Name);
            StatusText.Text = T("שחזור PostgreSQL נכשל: ") + ex.Message;
            MessageBox.Show(this, ex.Message, T("שגיאה בשחזור PostgreSQL"), MessageBoxButton.OK, MessageBoxImage.Error);
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

    private async void RedisRestore_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = T("בחירת גיבוי Redis מלא ומוצפן"),
            Filter = T("גיבוי NodeBB מוצפן|*.nbbbackup|כל הקבצים|*.*"),
            CheckFileExists = true, Multiselect = false
        };
        if (dialog.ShowDialog(this) != true) return;
        var password = RestorePasswordBox.Password;
        if (password.Length < 12)
        {
            MessageBox.Show(this, T("יש להזין סיסמה בת 12 תווים לפחות."), T("סיסמת גיבוי"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var confirmation = MessageBox.Show(this,
            T("אזהרה: קובץ Redis מכיל את כל שרת Redis, ולא רק את נתוני NodeBB. השחזור יחליף את קובץ ה-RDB של השרת המקומי כולו, כולל מסדי נתונים לוגיים ושירותים נוספים. נדרש שירות systemd יחיד ומזוהה. לפני ההחלפה יישמר עותק RDB קודם. להמשיך?"),
            T("אישור שחזור Redis מלא"), MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (confirmation != MessageBoxResult.Yes) return;

        RedisRestoreButton.IsEnabled = false;
        RedisBackupButton.IsEnabled = false;
        RefreshButton.IsEnabled = false;
        StatusText.Text = T("מפענח ובודק את קובץ RDB...");
        var tempRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NodeBB SSH Manager", "Temp");
        var decrypted = Path.Combine(tempRoot, "redis-restore-" + Guid.NewGuid().ToString("N") + ".rdb");
        try
        {
            Directory.CreateDirectory(tempRoot);
            await BackupEncryptionService.DecryptFileAsync(dialog.FileName, decrypted, password);
            await RedisDatabaseBackupService.ValidateRdbAsync(decrypted);
            StatusText.Text = T("קובץ RDB תקין. בודק את שירות Redis ויוצר נקודת חזרה...");
            await new RedisDatabaseRestoreService(_session).RestoreAsync(_installation, decrypted);
            StatusText.Text = T("שחזור Redis הושלם בהצלחה.");
            MessageBox.Show(this, T("שרת Redis שוחזר בהצלחה. השחזור כלל את כל מסדי Redis בשרת."), T("השחזור הושלם"), MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Instance.Error("DatabaseUI", "RedisRestoreFailed", ex, _session.Profile.Name);
            StatusText.Text = T("שחזור Redis נכשל: ") + ex.Message;
            MessageBox.Show(this, ex.Message, T("שגיאה בשחזור Redis"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            try { if (File.Exists(decrypted)) File.Delete(decrypted); } catch { }
            RestorePasswordBox.Clear();
            RedisRestoreButton.IsEnabled = true;
            RedisBackupButton.IsEnabled = true;
            RefreshButton.IsEnabled = true;
        }
    }

    private async Task RefreshAsync()
    {
        RefreshButton.IsEnabled = false;
        StatusText.Text = T("קורא את config.json...");
        try
        {
            var info = await _service.ReadConfigurationAsync(_installation);
            TypeText.Text = info.Type;
            HostText.Text = info.Host;
            PortText.Text = info.Port;
            DatabaseNameText.Text = info.DatabaseName;
            UsernameText.Text = info.UsernameConfigured ? T("כן") : T("לא");
            PasswordText.Text = info.PasswordConfigured ? T("כן") : T("לא");
            UriText.Text = info.UriConfigured ? T("כן (הערך עצמו מוסתר)") : T("לא");
            var isMongo = string.Equals(info.Type, "mongo", StringComparison.OrdinalIgnoreCase);
            var isRedis = string.Equals(info.Type, "redis", StringComparison.OrdinalIgnoreCase);
            var isPostgres = string.Equals(info.Type, "postgres", StringComparison.OrdinalIgnoreCase);
            DatabaseBackupButton.Visibility = isMongo ? Visibility.Visible : Visibility.Collapsed;
            MongoRestorePanel.Visibility = isMongo || isPostgres ? Visibility.Visible : Visibility.Collapsed;
            DatabaseRestoreButton.Visibility = isMongo ? Visibility.Visible : Visibility.Collapsed;
            PostgresRestoreButton.Visibility = isPostgres ? Visibility.Visible : Visibility.Collapsed;
            RedisBackupButton.Visibility = isRedis ? Visibility.Visible : Visibility.Collapsed;
            RedisRestoreButton.Content = T("שחזור Redis מלא (כל השרת)");
            RedisRestoreButton.Visibility = isRedis ? Visibility.Visible : Visibility.Collapsed;
            if (isRedis) MongoRestorePanel.Visibility = Visibility.Visible;
            PostgresBackupButton.Visibility = isPostgres ? Visibility.Visible : Visibility.Collapsed;
            RedisBackupNote.Visibility = isRedis ? Visibility.Visible : Visibility.Collapsed;
            StatusText.Text = T("התצורה נקראה בהצלחה.");
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Instance.Error("DatabaseUI", "ConfigurationDisplayFailed", ex, _session.Profile.Name);
            StatusText.Text = T("קריאת התצורה נכשלה: ") + ex.Message;
        }
        finally { RefreshButton.IsEnabled = true; }
    }
    private static string T(string text) => LocalizationService.Instance.Translate(text);

    private static string F(FormattableString text) => LocalizationService.Instance.TranslateFormat(text);

}
