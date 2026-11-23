using System.Windows;
using System.Windows.Threading;
using NodeBB.SSHManager.Services;
using NodeBB.SSHManager.Localization;

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
        StatusText.Text = string.Format(LocalizationService.Instance["Monitor_AutoOn"], _refreshTimer.Interval.TotalSeconds.ToString("0", System.Globalization.CultureInfo.CurrentCulture));
    }

    private void AutoRefresh_Unchecked(object sender, RoutedEventArgs e)
    {
        _refreshTimer.Stop();
        StatusText.Text = LocalizationService.Instance["Monitor_AutoOff"];
    }

    private void AutoRefreshInterval_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => UpdateRefreshInterval();

    private void UpdateRefreshInterval()
    {
        if (AutoRefreshIntervalComboBox.SelectedItem is not System.Windows.Controls.ComboBoxItem item ||
            !double.TryParse(item.Tag?.ToString(), out var seconds)) return;
        _refreshTimer.Interval = TimeSpan.FromSeconds(seconds);
        if (AutoRefreshCheckBox?.IsChecked == true && _refreshTimer.IsEnabled)
            StatusText.Text = string.Format(LocalizationService.Instance["Monitor_AutoOn"], seconds.ToString("0", System.Globalization.CultureInfo.CurrentCulture));
    }

    private async Task RefreshAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        RefreshButton.IsEnabled = false;
        StatusText.Text = LocalizationService.Instance["Monitor_Collecting"];
        try
        {
            var metrics = await _service.CollectAsync();
            UptimeText.Text = metrics.Uptime;
            LoadText.Text = string.Format(LocalizationService.Instance["Monitor_Load"], metrics.LoadAverage, metrics.CpuCount);
            CpuText.Text = metrics.CpuUsagePercent is int cpuPercent ? $"{cpuPercent}%" : LocalizationService.Instance["Monitor_Unavailable"];
            NetworkText.Text = metrics.NetworkThroughput;
            MemoryText.Text = metrics.MemoryUsagePercent is int memoryPercent
                ? $"{metrics.Memory} ({memoryPercent}%)"
                : metrics.Memory;
            SwapText.Text = metrics.SwapUsagePercent is int swapPercent
                ? $"{metrics.Swap} ({swapPercent}%)"
                : metrics.Swap;
            DiskText.Text = metrics.Disk;
            DiskInodesText.Text = metrics.DiskInodes;
            ProcessesText.Text = metrics.TopProcesses;
            MemoryProcessesText.Text = metrics.TopMemoryProcesses;

            var alerts = ServerHealthAssessment.Evaluate(metrics.LoadAverage, metrics.Disk, metrics.MemoryUsagePercent,
                metrics.CpuCount, metrics.SwapUsagePercent, metrics.DiskInodes, metrics.CpuUsagePercent);
            HealthAlertsText.Text = alerts.Count == 0
                ? LocalizationService.Instance["Monitor_Healthy"]
                : string.Join(Environment.NewLine, alerts.Select(alert => "• " + LocalizationService.Instance.Translate(alert)));
            HealthPanel.Background = alerts.Count == 0 ? System.Windows.Media.Brushes.Honeydew : System.Windows.Media.Brushes.MistyRose;
            CollectedAtText.Text = string.Format(LocalizationService.Instance["Monitor_CollectedAt"], metrics.CollectedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.CurrentCulture));
            StatusText.Text = LocalizationService.Instance["Monitor_Updated"];
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Instance.Error("MonitoringUI", "RefreshFailed", ex, _session.Profile.Name);
            StatusText.Text = string.Format(LocalizationService.Instance["Monitor_Failed"], ex.Message);
        }
        finally { RefreshButton.IsEnabled = true; _refreshing = false; }
    }
}
