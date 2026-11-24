using System.IO;
using System.Windows;
using Microsoft.Win32;
using NodeBB.SSHManager.Localization;
using NodeBB.SSHManager.Models;
using NodeBB.SSHManager.Services;

namespace NodeBB.SSHManager.Views;

public partial class FleetMaintenanceWindow : Window
{
    private readonly ServerProfile[] _profiles;
    private readonly SshServerSession[] _sessions;
    private readonly FleetMaintenanceService _service = new();
    private IReadOnlyList<FleetMaintenanceRow> _rows = [];
    private bool _busy;

    public FleetMaintenanceWindow(IEnumerable<ServerProfile> profiles, IEnumerable<SshServerSession> sessions)
    {
        InitializeComponent();
        _profiles = profiles.ToArray();
        _sessions = sessions.ToArray();
        ApplyLanguage();
        MaintenanceGrid.ItemsSource = _rows;
        Loaded += async (_, _) => await RefreshAsync();
    }

    private bool IsEnglish => LocalizationService.Instance.Language == "en";

    private void ApplyLanguage()
    {
        var en = IsEnglish;
        Title = en ? "NodeBB fleet maintenance" : "מרכז תחזוקת NodeBB";
        TitleText.Text = Title;
        SubtitleText.Text = en
            ? "Discover installations and inspect upgrade prerequisites across authenticated SSH sessions."
            : "איתור התקנות ובדיקת תנאים מקדימים לשדרוג בכל השרתים המחוברים באמצעות SSH מאומת.";
        RefreshButton.Content = en ? "Scan installations" : "סרוק התקנות";
        ExportButton.Content = en ? "Export CSV" : "ייצוא CSV";
        OpenUpgradeButton.Content = en ? "Open upgrade wizard" : "פתח אשף שדרוג";
        TotalLabel.Text = en ? "Installations" : "התקנות";
        ReadyLabel.Text = en ? "Ready for backup + upgrade" : "מוכנות לאחר גיבוי";
        BlockedLabel.Text = en ? "Need maintenance" : "דורשות טיפול";
        OfflineLabel.Text = en ? "Unavailable" : "לא זמינות";
        SafetyText.Text = en
            ? "Inspection is read-only. No server is connected automatically, no SSH key is trusted automatically, and no upgrade is started here."
            : "הבדיקה לקריאה בלבד. אין התחברות אוטומטית, אין אישור אוטומטי למפתחות SSH ולא מתחיל כאן שדרוג.";
        ServerColumn.Header = en ? "Server" : "שרת";
        EndpointColumn.Header = en ? "Address" : "כתובת";
        PathColumn.Header = en ? "Installation path" : "נתיב התקנה";
        VersionColumn.Header = en ? "NodeBB" : "NodeBB";
        BranchColumn.Header = en ? "Branch" : "ענף";
        DatabaseColumn.Header = en ? "Database" : "מסד נתונים";
        NodeColumn.Header = "Node.js";
        NpmColumn.Header = "npm";
        StatusColumn.Header = en ? "Status" : "מצב";
        FindingsColumn.Header = en ? "Findings" : "ממצאים";
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async Task RefreshAsync()
    {
        if (_busy) return;
        _busy = true;
        RefreshButton.IsEnabled = false;
        ExportButton.IsEnabled = false;
        OpenUpgradeButton.IsEnabled = false;
        StatusText.Text = IsEnglish ? "Discovering installations and checking prerequisites…" : "מאתר התקנות ובודק תנאים מקדימים…";
        try
        {
            _rows = await _service.CollectAsync(_profiles, _sessions);
            MaintenanceGrid.ItemsSource = _rows;
            TotalCount.Text = _rows.Count(r => r.InstallationPath != "—").ToString();
            ReadyCount.Text = _rows.Count(r => r.CanUpgrade).ToString();
            BlockedCount.Text = _rows.Count(r => r.IsReachable && !r.CanUpgrade).ToString();
            OfflineCount.Text = _rows.Count(r => !r.IsReachable).ToString();
            StatusText.Text = IsEnglish
                ? $"Inspection completed at {DateTime.Now:yyyy-MM-dd HH:mm:ss}. Review each installation before opening its upgrade wizard."
                : $"הבדיקה הושלמה בשעה {DateTime.Now:yyyy-MM-dd HH:mm:ss}. יש לעיין בכל התקנה לפני פתיחת אשף השדרוג.";
            ExportButton.IsEnabled = _rows.Count > 0;
        }
        catch (Exception ex)
        {
            StatusText.Text = (IsEnglish ? "Inspection failed: " : "הבדיקה נכשלה: ") + ex.Message;
            DiagnosticLogService.Instance.Error("FleetMaintenance", "InspectionFailed", ex);
        }
        finally { _busy = false; RefreshButton.IsEnabled = true; }
    }

    private void MaintenanceGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        OpenUpgradeButton.IsEnabled = MaintenanceGrid.SelectedItem is FleetMaintenanceRow row
            && row.IsReachable && row.InstallationPath != "—";
    }

    private void OpenUpgrade_Click(object sender, RoutedEventArgs e)
    {
        if (MaintenanceGrid.SelectedItem is not FleetMaintenanceRow row ||
            !row.IsReachable || row.InstallationPath == "—") return;
        var profile = _profiles.FirstOrDefault(p => p.Id == row.ProfileId);
        var session = _sessions.FirstOrDefault(s => s.Profile.Id == row.ProfileId && s.IsConnected);
        if (profile is null || session is null)
        {
            MessageBox.Show(this, IsEnglish ? "Reconnect to this server before continuing." : "יש להתחבר מחדש לשרת לפני ההמשך.",
                IsEnglish ? "Connection required" : "נדרש חיבור", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        new NodeBbUpgradeWindow(session, new NodeBbInstallation(row.InstallationPath, row.Version)) { Owner = this }.ShowDialog();
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_rows.Count == 0) return;
        var dialog = new SaveFileDialog
        {
            Filter = IsEnglish ? "CSV file (*.csv)|*.csv" : "קובץ CSV (*.csv)|*.csv",
            FileName = $"nodebb-maintenance-{DateTime.Now:yyyyMMdd-HHmmss}.csv",
            AddExtension = true, DefaultExt = ".csv"
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            File.WriteAllText(dialog.FileName, FleetMaintenanceService.ExportCsv(_rows), new System.Text.UTF8Encoding(true));
            StatusText.Text = IsEnglish ? "CSV export completed." : "ייצוא CSV הושלם.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            StatusText.Text = IsEnglish ? "CSV export failed." : "ייצוא CSV נכשל.";
            DiagnosticLogService.Instance.Error("FleetMaintenance", "CsvExportFailed", ex);
        }
    }
}
