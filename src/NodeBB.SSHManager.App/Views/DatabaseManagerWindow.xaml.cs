using System.Windows;
using NodeBB.SSHManager.Services;

namespace NodeBB.SSHManager.Views;

public partial class DatabaseManagerWindow : Window
{
    private readonly NodeBbDatabaseService _service;
    private readonly NodeBbInstallation _installation;
    private readonly SshServerSession _session;

    public DatabaseManagerWindow(SshServerSession session, NodeBbInstallation installation)
    {
        InitializeComponent();
        _session = session;
        _installation = installation;
        _service = new NodeBbDatabaseService(session);
        InstallationLabel.Text = $"{session.Profile.Name} · {_installation.Path}";
        Loaded += async (_, _) => await RefreshAsync();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async Task RefreshAsync()
    {
        RefreshButton.IsEnabled = false;
        StatusText.Text = "קורא את config.json...";
        try
        {
            var info = await _service.ReadConfigurationAsync(_installation);
            TypeText.Text = info.Type;
            HostText.Text = info.Host;
            PortText.Text = info.Port;
            DatabaseNameText.Text = info.DatabaseName;
            UsernameText.Text = info.UsernameConfigured ? "כן" : "לא";
            PasswordText.Text = info.PasswordConfigured ? "כן" : "לא";
            UriText.Text = info.UriConfigured ? "כן (הערך עצמו מוסתר)" : "לא";
            StatusText.Text = "התצורה נקראה בהצלחה.";
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Instance.Error("DatabaseUI", "ConfigurationDisplayFailed", ex, _session.Profile.Name);
            StatusText.Text = "קריאת התצורה נכשלה: " + ex.Message;
        }
        finally { RefreshButton.IsEnabled = true; }
    }
}
