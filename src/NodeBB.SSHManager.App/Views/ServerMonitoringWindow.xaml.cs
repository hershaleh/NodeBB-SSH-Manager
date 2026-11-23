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
    private readonly ServerMonitoringHistory _history = new(60);

    public ServerMonitoringWindow(SshServerSession session)
    {
        InitializeComponent();
        _session = session;
        _service = new RemoteMonitoringService(session);
        ServerLabel.Text = $"{session.Profile.Name} · {session.Profile.Host}";
        _refreshTimer.Tick += async (_, _) => await RefreshAsync();
        Loaded += async (_, _) => await RefreshAsync();
        Closed += (_, _) => _refreshTimer.Stop();
        TrendCanvas.SizeChanged += (_, _) => RenderTrendChart();
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

            _history.Add(metrics.CollectedAt, metrics.CpuUsagePercent, metrics.MemoryUsagePercent);
            RenderTrendChart();

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
    private void RenderTrendChart()
    {
        if (TrendCanvas is null) return;
        TrendCanvas.Children.Clear();
        var samples = _history.Samples;
        var width = Math.Max(TrendCanvas.ActualWidth, 300);
        const double height = 112;
        if (samples.Count == 0)
        {
            TrendCanvas.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = LocalizationService.Instance["Monitor_TrendEmpty"],
                Foreground = System.Windows.Media.Brushes.Gray,
                Margin = new Thickness(8, 12, 8, 0),
                FlowDirection = LocalizationService.Instance.FlowDirection
            });
            return;
        }

        void AddSeries(Func<ServerMonitoringSample, int?> selector, System.Windows.Media.Brush brush)
        {
            var points = new System.Windows.Media.PointCollection();
            for (var index = 0; index < samples.Count; index++)
            {
                var value = selector(samples[index]);
                if (value is null) continue;
                var x = samples.Count == 1 ? width / 2 : 8 + (width - 16) * index / (samples.Count - 1);
                var y = 6 + (100 - Math.Clamp(value.Value, 0, 100)) * (height - 12) / 100;
                points.Add(new System.Windows.Point(x, y));
            }
            if (points.Count > 0)
                TrendCanvas.Children.Add(new System.Windows.Shapes.Polyline
                {
                    Points = points,
                    Stroke = brush,
                    StrokeThickness = 2.5,
                    StrokeLineJoin = System.Windows.Media.PenLineJoin.Round
                });
        }

        AddSeries(sample => sample.CpuPercent, new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(82, 104, 232)));
        AddSeries(sample => sample.MemoryPercent, new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(23, 134, 93)));
    }

}
