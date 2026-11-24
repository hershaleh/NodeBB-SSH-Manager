using System.Windows;
using Microsoft.Win32;
using NodeBB.SSHManager.Localization;
using NodeBB.SSHManager.Models;
using NodeBB.SSHManager.Services;

namespace NodeBB.SSHManager.Views;

public partial class FleetReadinessWindow : Window
{
    private readonly ServerProfile[] _profiles;
    private readonly SshServerSession[] _sessions;
    private readonly FleetReadinessService _service = new();
    private IReadOnlyList<FleetReadinessRow> _rows = [];
    private bool _refreshing;

    public FleetReadinessWindow(IEnumerable<ServerProfile> profiles, IEnumerable<SshServerSession> sessions)
    {
        InitializeComponent();
        _profiles = profiles.ToArray();
        _sessions = sessions.ToArray();
        ApplyLanguage();
        InventoryGrid.ItemsSource = _rows;
        Loaded += async (_, _) => await RefreshAsync();
    }

    private void ApplyLanguage()
    {
        var en = LocalizationService.Instance.Language == "en";
        Title = en ? "Server inventory and readiness" : "מלאי שרתים ובדיקת מוכנות";
        TitleText.Text = Title;
        SubtitleText.Text = en ? "Environment and maintenance-tool inventory — authenticated existing SSH sessions only"
            : "זיהוי סביבת השרת והכלים הנדרשים לתחזוקה — רק בחיבורי SSH שכבר אומתו";
        ExportButton.Content = en ? "Export CSV" : "ייצוא CSV";
        RefreshButton.Content = en ? "Scan connected servers" : "סרוק שרתים מחוברים";
        TotalLabel.Text = en ? "Profiles" : "שרתים במאגר";
        ConnectedLabel.Text = en ? "Scanned successfully" : "נסרקו בהצלחה";
        ReviewLabel.Text = en ? "Need review" : "דורשים בדיקה";
        OfflineLabel.Text = en ? "Disconnected / failed" : "לא מחוברים / נכשלו";
        StatusText.Text = en ? "No automatic connection is made. Inventory collection does not modify the server."
            : "לא מתבצעת התחברות אוטומטית. הסריקה אינה משנה דבר בשרת.";
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async Task RefreshAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        RefreshButton.IsEnabled = false;
        ExportButton.IsEnabled = false;
        var en = LocalizationService.Instance.Language == "en";
        StatusText.Text = en ? "Collecting inventory from authenticated sessions (up to three at a time)…"
            : "אוסף מידע מחיבורי SSH מאומתים (עד שלושה במקביל)…";
        try
        {
            _rows = await _service.CollectAsync(_profiles, _sessions);
            InventoryGrid.ItemsSource = _rows;
            TotalCount.Text = _rows.Count.ToString();
            ConnectedCount.Text = _rows.Count(r => r.IsReachable).ToString();
            ReviewCount.Text = _rows.Count(r => r.IsReachable && r.NeedsReview).ToString();
            OfflineCount.Text = _rows.Count(r => !r.IsReachable).ToString();
            StatusText.Text = en
                ? $"Updated {DateTime.Now:yyyy-MM-dd HH:mm:ss}. Inventory is informational; verify NodeBB/database compatibility before maintenance."
                : $"עודכן {DateTime.Now:yyyy-MM-dd HH:mm:ss}. המידע הוא לצורכי בדיקה; יש לאמת תאימות NodeBB ומסד נתונים לפני תחזוקה.";
            ExportButton.IsEnabled = _rows.Count > 0;
        }
        catch (Exception ex)
        {
            StatusText.Text = (en ? "Inventory scan failed: " : "סריקת המלאי נכשלה: ") + ex.Message;
            DiagnosticLogService.Instance.Error("FleetReadiness", "ScanFailed", ex);
        }
        finally { RefreshButton.IsEnabled = true; _refreshing = false; }
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_rows.Count == 0) return;
        var en = LocalizationService.Instance.Language == "en";
        var dialog = new SaveFileDialog
        {
            Filter = en ? "CSV file (*.csv)|*.csv" : "קובץ CSV (*.csv)|*.csv",
            FileName = $"nodebb-fleet-inventory-{DateTime.Now:yyyyMMdd-HHmmss}.csv",
            AddExtension = true, DefaultExt = ".csv"
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            File.WriteAllText(dialog.FileName, FleetReadinessService.ExportCsv(_rows), new System.Text.UTF8Encoding(true));
            StatusText.Text = en ? "CSV export completed." : "ייצוא CSV הושלם.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            StatusText.Text = en ? "CSV export failed." : "ייצוא CSV נכשל.";
            DiagnosticLogService.Instance.Error("FleetReadiness", "CsvExportFailed", ex);
        }
    }
}