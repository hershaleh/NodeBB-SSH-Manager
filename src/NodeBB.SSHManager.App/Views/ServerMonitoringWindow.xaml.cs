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
    private readonly ServerMonitoringHistory _history;
    private readonly ServerMonitoringHistoryStore _historyStore = new();
    private readonly MonitoringSettingsStore _settingsStore = new();
    private readonly MonitoringAlertHistoryStore _alertStore = new();
    private MonitoringAlertThresholds _thresholds = new();
    private MonitoringAlertHistory _alertHistory = new();

    public ServerMonitoringWindow(SshServerSession session)
    {
        InitializeComponent();
        _session = session;
        _service = new RemoteMonitoringService(session);
        _history = new ServerMonitoringHistory(ServerMonitoringHistoryStore.MaximumSamples, _historyStore.Load(session.Profile.Id));
        _thresholds = _settingsStore.Load(session.Profile.Id);
        _alertHistory = new MonitoringAlertHistory(_alertStore.Load(session.Profile.Id));
        LoadThresholdFields();
        RenderAlertHistory();
        ServerLabel.Text = $"{session.Profile.Name} · {session.Profile.Host}";
        _refreshTimer.Tick += async (_, _) => await RefreshAsync();
        Loaded += async (_, _) => await RefreshAsync();
        Closed += (_, _) => _refreshTimer.Stop();
        TrendCanvas.SizeChanged += (_, _) => RenderTrendChart();
        AutoRefreshIntervalComboBox.SelectedIndex = 1;
        TrendRangeComboBox.SelectedIndex = 2;
        RenderTrendChart();
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

            var loadValue = double.TryParse(metrics.LoadAverage.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(),
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsedLoad) &&
                double.IsFinite(parsedLoad) && parsedLoad >= 0 ? parsedLoad / Math.Max(1, metrics.CpuCount) : (double?)null;
            _history.Add(metrics.CollectedAt, metrics.CpuUsagePercent, metrics.MemoryUsagePercent, loadValue);
            try { _historyStore.Save(_session.Profile.Id, _history.Samples); }
            catch (Exception historyException) when (historyException is IOException or UnauthorizedAccessException)
            {
                DiagnosticLogService.Instance.Error("MonitoringUI", "HistorySaveFailed", historyException, _session.Profile.Name);
            }
            RenderTrendChart();

            var alerts = ServerHealthAssessment.Evaluate(metrics.LoadAverage, metrics.Disk, metrics.MemoryUsagePercent,
                metrics.CpuCount, metrics.SwapUsagePercent, metrics.DiskInodes, metrics.CpuUsagePercent, _thresholds);
            var alertChanges = _alertHistory.Update(metrics.CollectedAt, alerts);
            if (alertChanges.Count > 0)
                _alertStore.Save(_session.Profile.Id, _alertHistory.Events);
            RenderAlertHistory();
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

    private void LoadThresholdFields()
    {
        CpuThresholdTextBox.Text = _thresholds.CpuPercent.ToString(System.Globalization.CultureInfo.InvariantCulture);
        MemoryThresholdTextBox.Text = _thresholds.MemoryPercent.ToString(System.Globalization.CultureInfo.InvariantCulture);
        SwapThresholdTextBox.Text = _thresholds.SwapPercent.ToString(System.Globalization.CultureInfo.InvariantCulture);
        DiskThresholdTextBox.Text = _thresholds.DiskPercent.ToString(System.Globalization.CultureInfo.InvariantCulture);
        InodeThresholdTextBox.Text = _thresholds.InodePercent.ToString(System.Globalization.CultureInfo.InvariantCulture);
        LoadThresholdTextBox.Text = _thresholds.LoadPerCpu.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
    }

    private void SaveThresholds_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!int.TryParse(CpuThresholdTextBox.Text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var cpu) ||
                !int.TryParse(MemoryThresholdTextBox.Text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var memory) ||
                !int.TryParse(SwapThresholdTextBox.Text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var swap) ||
                !int.TryParse(DiskThresholdTextBox.Text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var disk) ||
                !int.TryParse(InodeThresholdTextBox.Text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var inode) ||
                !double.TryParse(LoadThresholdTextBox.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var load))
                throw new ArgumentException(LocalizationService.Instance["Monitor_ThresholdInvalid"]);
            var updated = new MonitoringAlertThresholds
            {
                CpuPercent = cpu, MemoryPercent = memory, SwapPercent = swap,
                DiskPercent = disk, InodePercent = inode, LoadPerCpu = load
            };
            updated.Validate();
            _settingsStore.Save(_session.Profile.Id, updated);
            _thresholds = updated;
            ThresholdStatusText.Text = LocalizationService.Instance["Monitor_ThresholdSaved"];
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            ThresholdStatusText.Text = ex is ArgumentException
                ? LocalizationService.Instance["Monitor_ThresholdInvalid"]
                : LocalizationService.Instance["Monitor_ThresholdSaveFailed"];
        }
    }

    private void ResetThresholds_Click(object sender, RoutedEventArgs e)
    {
        var defaults = new MonitoringAlertThresholds();
        try
        {
            _settingsStore.Save(_session.Profile.Id, defaults);
            _thresholds = defaults;
            LoadThresholdFields();
            ThresholdStatusText.Text = LocalizationService.Instance["Monitor_ThresholdReset"];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ThresholdStatusText.Text = LocalizationService.Instance["Monitor_ThresholdSaveFailed"];
        }
    }

    private void ExportAlerts_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Filter = LocalizationService.Instance["Monitor_AlertCsvFilter"],
                FileName = $"nodebb-monitor-alerts-{DateTime.Now:yyyyMMdd-HHmmss}.csv",
                AddExtension = true,
                DefaultExt = ".csv"
            };
            if (dialog.ShowDialog(this) != true) return;
            var lines = new List<string> { "Timestamp,State,Message" };
            lines.AddRange(_alertHistory.Events.Select(item =>
                Csv(item.OccurredAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)) + "," +
                Csv(item.IsActive ? "Active" : "Resolved") + "," +
                Csv(LocalizationService.Instance.Translate(item.Message))));
            File.WriteAllText(dialog.FileName, string.Join(Environment.NewLine, lines), new System.Text.UTF8Encoding(true));
            ThresholdStatusText.Text = LocalizationService.Instance["Monitor_AlertExported"];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            ThresholdStatusText.Text = LocalizationService.Instance["Monitor_AlertExportFailed"];
        }
    }

    private static string Csv(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";

    private void RenderAlertHistory()
    {
        AlertHistoryList.ItemsSource = _alertHistory.Events.Reverse()
            .Select(item => $"{item.OccurredAt.ToLocalTime():yyyy-MM-dd HH:mm:ss} · {(item.IsActive ? LocalizationService.Instance["Monitor_AlertRaised"] : LocalizationService.Instance["Monitor_AlertResolved"])} · {LocalizationService.Instance.Translate(item.Message)}")
            .ToArray();
        AlertHistoryCountText.Text = string.Format(LocalizationService.Instance["Monitor_AlertHistoryCount"], _alertHistory.Events.Count);
    }

    private void TrendRange_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => RenderTrendChart();

    private IReadOnlyList<ServerMonitoringSample> GetVisibleSamples()
    {
        var hours = 24;
        if (TrendRangeComboBox?.SelectedItem is System.Windows.Controls.ComboBoxItem item &&
            int.TryParse(item.Tag?.ToString(), out var selectedHours) && selectedHours is 1 or 6 or 24)
            hours = selectedHours;
        return _history.GetSince(DateTimeOffset.UtcNow.AddHours(-hours));
    }

    private void ExportMetrics_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var samples = GetVisibleSamples();
            if (samples.Count == 0)
            {
                StatusText.Text = LocalizationService.Instance["Monitor_MetricsEmpty"];
                return;
            }
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Filter = LocalizationService.Instance["Monitor_AlertCsvFilter"],
                FileName = $"nodebb-performance-{DateTime.Now:yyyyMMdd-HHmmss}.csv",
                AddExtension = true,
                DefaultExt = ".csv"
            };
            if (dialog.ShowDialog(this) != true) return;
            var lines = new List<string> { "Timestamp,CpuPercent,MemoryPercent,LoadPerCpu" };
            lines.AddRange(samples.Select(sample =>
                Csv(sample.CollectedAt.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture)) + "," +
                (sample.CpuPercent?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "") + "," +
                (sample.MemoryPercent?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "") + "," +
                (sample.LoadPerCpu?.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) ?? "")));
            File.WriteAllText(dialog.FileName, string.Join(Environment.NewLine, lines), new System.Text.UTF8Encoding(true));
            StatusText.Text = string.Format(LocalizationService.Instance["Monitor_MetricsExported"], samples.Count);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            StatusText.Text = LocalizationService.Instance["Monitor_MetricsExportFailed"];
        }
    }

    private void RenderTrendChart()
    {
        if (TrendCanvas is null) return;
        TrendCanvas.Children.Clear();
        var samples = GetVisibleSamples();
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
        var cpuValues = samples.Where(sample => sample.CpuPercent.HasValue).Select(sample => sample.CpuPercent!.Value).ToArray();
        var memoryValues = samples.Where(sample => sample.MemoryPercent.HasValue).Select(sample => sample.MemoryPercent!.Value).ToArray();
        var summary = new List<string>();
        if (cpuValues.Length > 0) summary.Add(string.Format(LocalizationService.Instance["Monitor_TrendCpuRange"], cpuValues.Min(), cpuValues.Max(), cpuValues.Average().ToString("0", System.Globalization.CultureInfo.InvariantCulture)));
        if (memoryValues.Length > 0) summary.Add(string.Format(LocalizationService.Instance["Monitor_TrendMemoryRange"], memoryValues.Min(), memoryValues.Max(), memoryValues.Average().ToString("0", System.Globalization.CultureInfo.InvariantCulture)));
        TrendSummaryText.Text = string.Join("   ·   ", summary);
    }

}
