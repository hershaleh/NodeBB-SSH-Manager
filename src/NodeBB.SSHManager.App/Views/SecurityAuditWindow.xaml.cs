using NodeBB.SSHManager.Localization;
using System.Collections.ObjectModel;
using System.Windows;
using NodeBB.SSHManager.Services;

namespace NodeBB.SSHManager.Views;

public partial class SecurityAuditWindow : Window
{
    private readonly RemoteSecurityAuditService _service;
    private readonly NodeBbInstallation _installation;
    private readonly SshServerSession _session;
    private readonly ObservableCollection<SecurityAuditItem> _items = [];

    public SecurityAuditWindow(SshServerSession session, NodeBbInstallation installation)
    {
        InitializeComponent();
        _session = session;
        _installation = installation;
        _service = new RemoteSecurityAuditService(session);
        InstallationLabel.Text = $"{session.Profile.Name} · {_installation.Path}";
        FindingsList.ItemsSource = _items;
        Loaded += async (_, _) => await RefreshAsync();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async Task RefreshAsync()
    {
        RefreshButton.IsEnabled = false;
        var localization = LocalizationService.Instance;
        StatusText.Text = localization["SecurityAudit_Checking"];
        try
        {
            var findings = await _service.RunAsync(_installation);
            _items.Clear();
            foreach (var finding in findings) _items.Add(finding);
            var critical = findings.Count(x => x.Severity == "קריטי");
            var warnings = findings.Count(x => x.Severity == "אזהרה");
            StatusText.Text = localization.TranslateFormat($"הבדיקה הושלמה. {critical} קריטיות, {warnings} אזהרות.");
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Instance.Error("SecurityAuditUI", "RefreshFailed", ex, _session.Profile.Name);
            StatusText.Text = localization.TranslateFormat($"בדיקת האבטחה נכשלה: {ex.Message}");
        }
        finally { RefreshButton.IsEnabled = true; }
    }
}