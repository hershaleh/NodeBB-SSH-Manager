using System.Text;
using Microsoft.Win32;
using System.Windows;
using NodeBB.SSHManager.Services;

namespace NodeBB.SSHManager.Views;

public partial class NodeBbUpgradeWindow : Window
{
    private readonly SshServerSession _session;
    private readonly NodeBbInstallation _installation;
    private readonly NodeBbUpgradeService _service;
    private NodeBbUpgradePreflight? _preflight;

    public NodeBbUpgradeWindow(SshServerSession session, NodeBbInstallation installation)
    {
        InitializeComponent();
        _session = session;
        _installation = installation;
        _service = new NodeBbUpgradeService(session);
        InstallationLabel.Text = $"{session.Profile.Name} · {installation.Path}";
        Loaded += async (_, _) => await RefreshAsync();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async Task RefreshAsync()
    {
        RefreshButton.IsEnabled = false;
        UpgradeButton.IsEnabled = false;
        StatusText.Text = "בודק את השרת...";
        try
        {
            _preflight = await _service.InspectAsync(_installation);
            var b = new StringBuilder();
            Add("מאגר Git", _preflight.IsGitRepository);
            Add("עץ עבודה נקי (ללא שינויים וקבצים לא מעוקבים)", _preflight.IsClean);
            Add("remote בשם origin", _preflight.HasOrigin);
            Add("Node.js", _preflight.HasNode, _preflight.NodeVersion);
            Add("npm", _preflight.HasNpm, _preflight.NpmVersion);
            b.AppendLine($"גרסה נוכחית: {_preflight.CurrentVersion}");
            b.AppendLine($"ענף נוכחי: {(_preflight.CurrentBranch.Length == 0 ? "(detached)" : _preflight.CurrentBranch)}");
            b.AppendLine($"מסד נתונים: {_preflight.DatabaseType}");
            b.AppendLine($"Commit: {_preflight.CurrentCommit}");
            PreflightText.Text = b.ToString();
            StatusText.Text = _preflight.CanUpgrade ? "השרת עבר את בדיקות המוכנות." : "השרת אינו מוכן לשדרוג. יש לטפל בבדיקות שנכשלו.";
            UpgradeButton.IsEnabled = _preflight.CanUpgrade;
            RedisScopeWarningCheck.Visibility = string.Equals(_preflight.DatabaseType, "redis", StringComparison.OrdinalIgnoreCase) ? Visibility.Visible : Visibility.Collapsed;
            void Add(string label, bool ok, string extra = "") =>
                b.AppendLine($"{(ok ? "✓" : "✗")} {label}{(extra.Length > 0 ? ": " + extra : "")}");
        }
        catch (Exception ex)
        {
            _preflight = null;
            PreflightText.Text = ex.Message;
            StatusText.Text = "בדיקת המוכנות נכשלה.";
            DiagnosticLogService.Instance.Error("NodeBBUpgradeUI", "PreflightFailed", ex, _session.Profile.Name);
        }
        finally { RefreshButton.IsEnabled = true; }
    }

    private void BrowseBackupFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "בחירת תיקייה לשמירת גיבויי טרום-השדרוג",
            Multiselect = false,
            InitialDirectory = Directory.Exists(BackupFolderBox.Text)
                ? BackupFolderBox.Text
                : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };
        if (dialog.ShowDialog(this) == true)
            BackupFolderBox.Text = dialog.FolderName;
    }

    private async void Upgrade_Click(object sender, RoutedEventArgs e)
    {
        if (_preflight is null || !_preflight.CanUpgrade) return;
        var tag = TargetTagBox.Text.Trim();
        if (!NodeBbUpgradeService.IsValidTargetTag(tag))
        {
            MessageBox.Show(this, "יש להזין תגית גרסה תקינה, לדוגמה v4.19.1.", "תגית לא תקינה", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (string.IsNullOrWhiteSpace(BackupFolderBox.Text))
        {
            MessageBox.Show(this, "יש לבחור תיקייה לשמירת הגיבויים.", "נדרשת תיקיית גיבוי", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var password = BackupPasswordBox.Password;
        if (password.Length < 12 || !string.Equals(password, BackupPasswordConfirmBox.Password, StringComparison.Ordinal))
        {
            MessageBox.Show(this, password.Length < 12 ? "סיסמת הגיבוי חייבת להכיל לפחות 12 תווים." : "סיסמאות ההצפנה אינן תואמות.",
                "בדיקת סיסמה", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (string.Equals(_preflight.DatabaseType, "redis", StringComparison.OrdinalIgnoreCase) &&
            !RedisScopeWarningCheck.IsChecked.GetValueOrDefault())
        {
            MessageBox.Show(this, "יש לאשר במפורש שגיבוי Redis כולל את כל שרת Redis ולא רק את מסד NodeBB.", "נדרש אישור היקף גיבוי", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!MigrationWarningCheck.IsChecked.GetValueOrDefault())
        {
            MessageBox.Show(this, "יש לאשר שהבנת את הסיכון של מיגרציות מסד הנתונים.", "נדרש אישור", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (MessageBox.Show(this,
            $"התוכנה תיצור ותאמת גיבוי מוצפן של מסד הנתונים ושל config.json/קובצי התצורה והעלאות, ורק לאחר הצלחתם תעצור את NodeBB ותשדרג לתגית {tag}. אם מיגרציה תיכשל, השירות יישאר עצור.\n\nלהמשיך?",
            "אישור גיבוי ושדרוג", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;

        UpgradeButton.IsEnabled = false;
        RefreshButton.IsEnabled = false;
        TargetTagBox.IsEnabled = false;
        StatusText.Text = "בודק שתגית הגרסה קיימת לפני יצירת הגיבויים...";
        try
        {
            await _service.VerifyTargetTagAsync(_installation, tag);
            var progress = new Progress<string>(message => StatusText.Text = message);
            var backupSet = await new NodeBbUpgradeBackupService(_session).CreateAsync(
                _installation, _preflight.DatabaseType, BackupFolderBox.Text, password, progress);
            BackupPasswordBox.Clear();
            BackupPasswordConfirmBox.Clear();

            StatusText.Text = "הגיבויים אומתו. מתחיל כעת את השדרוג...";
            var result = await _service.UpgradeAsync(_installation, tag, true, true);
            var artifactSummary = string.Join("\n", backupSet.Artifacts.Select(a =>
                $"{a.Description}: {a.FilePath} ({a.SizeBytes:N0} bytes)"));
            var message = result.Message + "\n\nתיקיית הגיבויים: " + backupSet.DirectoryPath + "\n" + artifactSummary;
            StatusText.Text = result.Message;
            MessageBox.Show(this, message + (result.Output.Length > 0 ? "\n\n" + result.Output : ""),
                result.Success && result.Restarted ? "השדרוג הושלם" : "תוצאת השדרוג",
                MessageBoxButton.OK, result.Success && result.Restarted ? MessageBoxImage.Information : MessageBoxImage.Warning);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            StatusText.Text = "השדרוג בוטל או נכשל: " + ex.Message;
            DiagnosticLogService.Instance.Error("NodeBBUpgradeUI", "UpgradeFailed", ex, _session.Profile.Name);
            MessageBox.Show(this, ex.Message, "שדרוג לא הושלם", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            BackupPasswordBox.Clear();
            BackupPasswordConfirmBox.Clear();
            TargetTagBox.IsEnabled = true;
            RefreshButton.IsEnabled = true;
            UpgradeButton.IsEnabled = _preflight?.CanUpgrade == true;
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
