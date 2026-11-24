using System.Windows;
using Microsoft.Win32;
using NodeBB.SSHManager.Localization;
using NodeBB.SSHManager.Models;
using NodeBB.SSHManager.Services;

namespace NodeBB.SSHManager.Views;

public partial class MultiServerHealthOverviewWindow : Window
{
    private readonly ServerProfile[] _profiles;
    private readonly SshServerSession[] _sessions;
    private readonly MultiServerHealthOverviewService _service = new();
    private IReadOnlyList<ServerHealthOverviewRow> _rows = [];
    private bool _refreshing;

    public MultiServerHealthOverviewWindow(IEnumerable<ServerProfile> profiles, IEnumerable<SshServerSession> sessions)
    {
        InitializeComponent();
        _profiles = profiles.ToArray();
        _sessions = sessions.ToArray();
        HealthGrid.ItemsSource = _rows;
        Loaded += async (_, _) => await RefreshAsync();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async Task RefreshAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        RefreshButton.IsEnabled = false;
        ExportButton.IsEnabled = false;
        StatusText.Text = LocalizationService.Instance.Language == "en"
            ? "Collecting metrics from currently connected servers (up to three at a time)…"
            : "אוסף נתונים מהשרתים המחוברים (עד שלושה במקביל)…";
        try
        {
            _rows = await _service.CollectAsync(_profiles, _sessions);
            HealthGrid.ItemsSource = _rows;
            TotalCount.Text = _rows.Count.ToString();
            ReachableCount.Text = _rows.Count(row => row.IsReachable).ToString();
            UnavailableCount.Text = _rows.Count(row => !row.IsReachable).ToString();
            AlertCount.Text = _rows.Count(row => row.IsReachable && row.Alerts != "תקין").ToString();
            StatusText.Text = string.Format(
                LocalizationService.Instance.Language == "en"
                    ? "Updated {0:yyyy-MM-dd HH:mm:ss}. Only existing authenticated SSH sessions were queried."
                    : "עודכן {0:yyyy-MM-dd HH:mm:ss}. נבדקו רק חיבורי SSH קיימים שעברו אימות.",
                DateTime.Now);
            ExportButton.IsEnabled = _rows.Count > 0;
        }
        catch (Exception ex)
        {
            StatusText.Text = LocalizationService.Instance.Language == "en"
                ? "Could not refresh the overview: " + ex.Message
                : "רענון תמונת המצב נכשל: " + ex.Message;
            DiagnosticLogService.Instance.Error("MultiServerMonitoring", "OverviewRefreshFailed", ex);
        }
        finally
        {
            RefreshButton.IsEnabled = true;
            _refreshing = false;
        }
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_rows.Count == 0) return;
        var dialog = new SaveFileDialog
        {
            Filter = LocalizationService.Instance.Language == "en" ? "CSV file (*.csv)|*.csv" : "קובץ CSV (*.csv)|*.csv",
            FileName = $"nodebb-server-health-{DateTime.Now:yyyyMMdd-HHmmss}.csv",
            AddExtension = true,
            DefaultExt = ".csv"
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            File.WriteAllText(dialog.FileName, MultiServerHealthOverviewService.ExportCsv(_rows),
                new System.Text.UTF8Encoding(true));
            StatusText.Text = LocalizationService.Instance.Language == "en" ? "CSV export completed." : "ייצוא CSV הושלם.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            StatusText.Text = LocalizationService.Instance.Language == "en" ? "CSV export failed." : "ייצוא CSV נכשל.";
            DiagnosticLogService.Instance.Error("MultiServerMonitoring", "CsvExportFailed", ex);
        }
    }
}
