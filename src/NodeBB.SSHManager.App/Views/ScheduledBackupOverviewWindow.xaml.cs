using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using NodeBB.SSHManager.Localization;
using NodeBB.SSHManager.Models;
using NodeBB.SSHManager.Services;

namespace NodeBB.SSHManager.Views;

public partial class ScheduledBackupOverviewWindow : Window
{
    private readonly ServerProfile[] _profiles;
    private readonly ScheduledBackupOverviewService _overview = new();
    private readonly ScheduledBackupService _schedules = new();
    private IReadOnlyList<ScheduledBackupOverviewRow> _rows = [];

    public ScheduledBackupOverviewWindow(IEnumerable<ServerProfile> profiles)
    {
        InitializeComponent();
        _profiles = profiles.ToArray();
        ApplyLanguage();
        RefreshRows();
    }

    private void ApplyLanguage()
    {
        var en = LocalizationService.Instance.Language == "en";
        Title = en ? "Scheduled backup management" : "ניהול גיבויים מתוזמנים";
        TitleText.Text = Title;
        SubtitleText.Text = en ? "All local schedules across NodeBB servers, including run status and destination"
            : "כל התזמונים המקומיים של כל שרתי NodeBB, כולל מצב הריצה והיעד";
        ExportButton.Content = en ? "Export CSV" : "ייצוא CSV";
        RemoveButton.Content = en ? "Remove selected schedule" : "הסרת תזמון נבחר";
        RefreshButton.Content = en ? "Refresh" : "רענון";
        TotalLabel.Text = en ? "Schedules" : "תזמונים";
        HealthyLabel.Text = en ? "No alerts" : "ללא התראות";
        AttentionLabel.Text = en ? "Need attention" : "דורשים בדיקה";
        DatabaseLabel.Text = en ? "Include database" : "כוללים מסד נתונים";
        ScopeText.Text = en ? "Tasks run as the current Windows user and only while that user is logged on."
            : "התזמונים פועלים תחת משתמש Windows הנוכחי ורק כשהוא מחובר.";
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshRows();

    private void RefreshRows()
    {
        try
        {
            _rows = _overview.Collect(_profiles, DateTime.Now);
            SchedulesGrid.ItemsSource = _rows;
            TotalCount.Text = _rows.Count.ToString();
            HealthyCount.Text = _rows.Count(r => !r.IsAttentionRequired).ToString();
            AttentionCount.Text = _rows.Count(r => r.IsAttentionRequired).ToString();
            DatabaseCount.Text = _rows.Count(r => r.IncludesDatabase).ToString();
            StatusText.Text = LocalizationService.Instance.Language == "en"
                ? $"Loaded {_rows.Count} scheduled backups."
                : $"נטענו {_rows.Count} תזמוני גיבוי.";
            ExportButton.IsEnabled = _rows.Count > 0;
        }
        catch (Exception ex)
        {
            StatusText.Text = (LocalizationService.Instance.Language == "en" ? "Could not load schedules: " : "לא ניתן לטעון תזמונים: ") + ex.Message;
            DiagnosticLogService.Instance.Error("ScheduledBackupOverview", "LoadFailed", ex);
        }
        UpdateSelection();
    }

    private void SchedulesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSelection();

    private void UpdateSelection()
    {
        RemoveButton.IsEnabled = SchedulesGrid.SelectedItem is ScheduledBackupOverviewRow;
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (SchedulesGrid.SelectedItem is not ScheduledBackupOverviewRow selected) return;
        var en = LocalizationService.Instance.Language == "en";
        var message = en
            ? $"Remove the scheduled task for {selected.ServerName}? Existing backup files will not be deleted."
            : $"להסיר את תזמון הגיבוי עבור {selected.ServerName}? קובצי גיבוי שכבר נוצרו לא יימחקו.";
        if (MessageBox.Show(this, message, en ? "Remove scheduled backup" : "הסרת תזמון גיבוי",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            _schedules.Remove(selected.ScheduleId);
            StatusText.Text = en ? "Scheduled task and protected schedule settings removed. Backup files were kept."
                : "משימת התזמון וההגדרות המוגנות הוסרו. קובצי הגיבוי נשמרו.";
            RefreshRows();
        }
        catch (Exception ex)
        {
            StatusText.Text = (en ? "Removal failed: " : "הסרת התזמון נכשלה: ") + ex.Message;
            DiagnosticLogService.Instance.Error("ScheduledBackupOverview", "RemoveFailed", ex, selected.ServerName);
        }
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_rows.Count == 0) return;
        var en = LocalizationService.Instance.Language == "en";
        var dialog = new SaveFileDialog
        {
            Filter = en ? "CSV file (*.csv)|*.csv" : "קובץ CSV (*.csv)|*.csv",
            FileName = $"nodebb-scheduled-backups-{DateTime.Now:yyyyMMdd-HHmmss}.csv",
            AddExtension = true, DefaultExt = ".csv"
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            File.WriteAllText(dialog.FileName, ScheduledBackupOverviewService.ExportCsv(_rows), new System.Text.UTF8Encoding(true));
            StatusText.Text = en ? "CSV export completed." : "ייצוא CSV הושלם.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            StatusText.Text = en ? "CSV export failed." : "ייצוא CSV נכשל.";
            DiagnosticLogService.Instance.Error("ScheduledBackupOverview", "CsvExportFailed", ex);
        }
    }
}