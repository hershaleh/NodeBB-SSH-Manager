using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using NodeBB.SSHManager.Services;

namespace NodeBB.SSHManager.Views;

public partial class ServerActionHistoryOverviewWindow : Window
{
    private readonly IReadOnlyList<ServerActionHistoryEntry> _entries =
        new ServerActionHistoryService().GetRecent(2000);

    public ServerActionHistoryOverviewWindow()
    {
        InitializeComponent();
        ApplyFilter();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();
    private void FilterBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (HistoryList is not null) ApplyFilter();
    }

    private ServerActionHistoryEntry[] GetFilteredEntries()
    {
        var query = SearchBox.Text.Trim();
        var filter = FilterBox.SelectedIndex;
        return _entries.Where(entry =>
            (filter == 0 || (filter == 1 && entry.Succeeded) || (filter == 2 && !entry.Succeeded))
            && (string.IsNullOrWhiteSpace(query)
                || entry.ServerName.Contains(query, StringComparison.OrdinalIgnoreCase)
                || entry.ServerHost.Contains(query, StringComparison.OrdinalIgnoreCase)
                || entry.Action.Contains(query, StringComparison.OrdinalIgnoreCase)
                || entry.Target.Contains(query, StringComparison.OrdinalIgnoreCase)
                || entry.Summary.Contains(query, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(entry => entry.Timestamp).ToArray();
    }

    private void ApplyFilter()
    {
        var filtered = GetFilteredEntries();
        HistoryList.ItemsSource = filtered.Select(entry => new HistoryOverviewItem(
            entry.Timestamp.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss"),
            $"{entry.ServerName} · {entry.ServerHost}:{entry.ServerPort}",
            string.IsNullOrWhiteSpace(entry.Target) ? entry.Action : $"{entry.Action} · {entry.Target}",
            entry.Succeeded ? "הצליח" : "נכשל",
            entry.Succeeded ? "#18794E" : "#B42318",
            entry.Summary)).ToArray();
        CountText.Text = $"מוצגות {filtered.Length} מתוך {_entries.Count} פעולות אחרונות · עד 2,000 רשומות";
        EmptyState.Visibility = filtered.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = _entries.Count == 0 ? "לא נשמרו עדיין פעולות בשרתים." : "לא נמצאו פעולות התואמות לחיפוש ולמסנן.";
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var filtered = GetFilteredEntries();
        if (filtered.Length == 0)
        {
            MessageBox.Show(this, "אין תוצאות לייצוא.", "ייצוא היסטוריה", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dialog = new SaveFileDialog
        {
            Title = "ייצוא היסטוריית כל השרתים",
            Filter = "קובץ CSV (*.csv)|*.csv",
            FileName = "nodebb-all-servers-history.csv",
            AddExtension = true,
            DefaultExt = ".csv"
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            File.WriteAllText(dialog.FileName, ServerActionHistoryService.ExportCsv(filtered), new System.Text.UTF8Encoding(true));
            MessageBox.Show(this, $"יוצאו {filtered.Length} פעולות.", "הייצוא הושלם", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Instance.Warning("ActionHistory", "GlobalExportFailed",
                "Could not export global action history.", ex);
            MessageBox.Show(this, "ייצוא ההיסטוריה נכשל. בדוק הרשאות כתיבה ליעד.", "שגיאה", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}

public sealed record HistoryOverviewItem(string Timestamp, string Server, string ActionLine, string Result, string ResultColor, string Summary);
