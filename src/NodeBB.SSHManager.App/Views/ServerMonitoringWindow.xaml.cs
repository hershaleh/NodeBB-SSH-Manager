using System.Windows;
using NodeBB.SSHManager.Services;

namespace NodeBB.SSHManager.Views;

public partial class ServerMonitoringWindow : Window
{
    private readonly RemoteMonitoringService _service;
    private readonly SshServerSession _session;

    public ServerMonitoringWindow(SshServerSession session)
    {
        InitializeComponent();
        _session = session;
        _service = new RemoteMonitoringService(session);
        ServerLabel.Text = $"{session.Profile.Name} · {session.Profile.Host}";
        Loaded += async (_, _) => await RefreshAsync();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async Task RefreshAsync()
    {
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
        finally { RefreshButton.IsEnabled = true; }
    }
}
