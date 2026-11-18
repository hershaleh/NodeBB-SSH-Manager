using System.Text;
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

    private async void Upgrade_Click(object sender, RoutedEventArgs e)
    {
        if (_preflight is null || !_preflight.CanUpgrade) return;
        var tag = TargetTagBox.Text.Trim();
        if (!NodeBbUpgradeService.IsValidTargetTag(tag))
        {
            MessageBox.Show(this, "יש להזין תגית גרסה תקינה, לדוגמה v4.19.1.", "תגית לא תקינה", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!DatabaseBackupCheck.IsChecked.GetValueOrDefault() ||
            !FilesBackupCheck.IsChecked.GetValueOrDefault() ||
            !MigrationWarningCheck.IsChecked.GetValueOrDefault())
        {
            MessageBox.Show(this, "יש לאשר את שלוש דרישות הגיבוי וההבנה לפני השדרוג.", "נדרשים אישורים", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (MessageBox.Show(this,
            $"השדרוג יעצור את NodeBB, יעביר את הקוד לתגית {tag} ויריץ ./nodebb upgrade. אם המיגרציה תיכשל, השירות יישאר עצור.\n\nלהמשיך?",
            "אישור שדרוג", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;

        UpgradeButton.IsEnabled = false;
        RefreshButton.IsEnabled = false;
        TargetTagBox.IsEnabled = false;
        StatusText.Text = "עוצר את NodeBB ומבצע שדרוג...";
        try
        {
            var result = await _service.UpgradeAsync(_installation, tag, true, true);
            StatusText.Text = result.Message;
            MessageBox.Show(this, result.Message + (result.Output.Length > 0 ? "\n\n" + result.Output : ""),
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
            TargetTagBox.IsEnabled = true;
            RefreshButton.IsEnabled = true;
            UpgradeButton.IsEnabled = _preflight?.CanUpgrade == true;
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
