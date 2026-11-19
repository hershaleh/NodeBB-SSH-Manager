using System.Windows;
using System.Windows.Threading;
using NodeBB.SSHManager.Services;

namespace NodeBB.SSHManager.Views;

public partial class ServerMonitoringWindow : Window
{
    private readonly RemoteMonitoringService _service;
    private readonly SshServerSession _session;
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromMinutes(1) };
    private bool _refreshing;

    public ServerMonitoringWindow(SshServerSession session)
    {
        InitializeComponent();
        _session = session;
        _service = new RemoteMonitoringService(session);
        ServerLabel.Text = $"{session.Profile.Name} · {session.Profile.Host}";
        _refreshTimer.Tick += async (_, _) => await RefreshAsync();
        Loaded += async (_, _) => await RefreshAsync();
        Closed += (_, _) => _refreshTimer.Stop();
        AutoRefreshIntervalComboBox.SelectedIndex = 1;
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private void AutoRefresh_Checked(object sender, RoutedEventArgs e)
    {
        UpdateRefreshInterval();
        _refreshTimer.Start();
        StatusText.Text = $"רענון אוטומטי פעיל ({_refreshTimer.Interval.TotalSeconds:0} שניות).";
    }

    private void AutoRefresh_Unchecked(object sender, RoutedEventArgs e)
    {
        _refreshTimer.Stop();
        StatusText.Text = "רענון אוטומטי כבוי.";
    }

    private void AutoRefreshInterval_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        UpdateRefreshInterval();
    }

    private void UpdateRefreshInterval()
    {
        if (AutoRefreshIntervalComboBox.SelectedItem is not System.Windows.Controls.ComboBoxItem item ||
            !double.TryParse(item.Tag?.ToString(), out var seconds)) return;
        _refreshTimer.Interval = TimeSpan.FromSeconds(seconds);
        if (AutoRefreshCheckBox?.IsChecked == true && _refreshTimer.IsEnabled)
            StatusText.Text = $"רענון אוטומטי פעיל ({seconds:0} שניות).";
    }

    private async Task RefreshAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        RefreshButton.IsEnabled = false;
        StatusText.Text = "אוסף נתונים...";
        try
        {
            var metrics = await _service.CollectAsync();
            UptimeText.Text = metrics.Uptime;
            LoadText.Text = metrics.LoadAverage;
            MemoryText.Text = metrics.Memory;
            DiskText.Text = metrics.Disk;
            ProcessesText.Text = metrics.TopProcesses;
            CollectedAtText.Text = $"נאסף: {metrics.CollectedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
            StatusText.Text = "הנתונים עודכנו.";
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Instance.Error("MonitoringUI", "RefreshFailed", ex, _session.Profile.Name);
            StatusText.Text = "איסוף הנתונים נכשל: " + ex.Message;
        }
        finally { RefreshButton.IsEnabled = true; _refreshing = false; }
    }
}
