using NodeBB.SSHManager.Localization;
using System.Collections.ObjectModel;
using System.Windows;
using NodeBB.SSHManager.Services;

namespace NodeBB.SSHManager.Views;

public sealed record CapabilityDisplayItem(string Name, string Value);

public partial class ServerDiagnosticsWindow : Window
{
    private readonly RemoteCapabilityService _service;
    private readonly SshServerSession _session;
    private readonly ObservableCollection<CapabilityDisplayItem> _items = [];

    public ServerDiagnosticsWindow(SshServerSession session)
    {
        InitializeComponent();
        _session = session;
        _service = new RemoteCapabilityService(session);
        ServerLabel.Text = $"{session.Profile.Name} · {session.Profile.Host}";
        CapabilitiesList.ItemsSource = _items;
        Loaded += async (_, _) => await DetectAsync();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await DetectAsync();

    private async Task DetectAsync()
    {
        RefreshButton.IsEnabled = false;
        StatusText.Text = T("סורק את השרת...");
        try
        {
            var data = await _service.DetectAsync();
            _items.Clear();
            _items.Add(new(T("שם שרת"), data.HostName));
            _items.Add(new(T("מערכת הפעלה"), data.OperatingSystem));
            _items.Add(new("Kernel", data.Kernel));
            _items.Add(new(T("ארכיטקטורה"), data.Architecture));
            _items.Add(new(T("מנהל שירותים"), data.InitSystem));
            _items.Add(new(T("מנהל חבילות"), data.PackageManager));
            _items.Add(new(T("משתמש SSH"), data.RemoteUser));
            _items.Add(new(T("הרשאות root"), T(data.IsRoot ? "כן" : "לא")));
            _items.Add(new("Node.js", data.NodeVersion));
            _items.Add(new("npm", data.NpmVersion));
            _items.Add(new("Git", data.GitVersion));
            _items.Add(new("MongoDB", data.MongoVersion));
            _items.Add(new("Redis", data.RedisVersion));
            _items.Add(new("Docker", data.DockerVersion));
            _items.Add(new("Podman", data.PodmanVersion));
            _items.Add(new("PM2", data.Pm2Version));
            _items.Add(new(T("פקודת NodeBB גלובלית"), T(data.NodeBbCliAvailable ? "זמינה" : "לא נמצאה")));
            DetectedAtText.Text = string.Format(T("עודכן: {0}"), data.DetectedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
            StatusText.Text = T("הסריקה הושלמה.");
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Instance.Error("InventoryUI", "InventoryDisplayFailed", ex, _session.Profile.Name);
            StatusText.Text = string.Format(T("הסריקה נכשלה: {0}"), ex.Message);
        }
        finally { RefreshButton.IsEnabled = true; }
    }
    private static string T(string text) => LocalizationService.Instance.Translate(text);
}
