using System.Collections.ObjectModel;
using System.Windows;
using NodeBB.SSHManager.Services;
using NodeBB.SSHManager.Localization;

namespace NodeBB.SSHManager.Views;

public partial class SystemServicesWindow : Window
{
    private readonly RemoteServiceManagerService _service;
    private readonly SshServerSession _session;
    private readonly ObservableCollection<RemoteServiceEntry> _items = [];
    private string _manager = "UNSUPPORTED";

    public SystemServicesWindow(SshServerSession session)
    {
        InitializeComponent();
        _session = session;
        _service = new RemoteServiceManagerService(session);
        ServerLabel.Text = $"{session.Profile.Name} · {session.Profile.Host}";
        ServicesList.ItemsSource = _items;
        Loaded += async (_, _) => await RefreshAsync();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async Task RefreshAsync()
    {
        IsEnabled = false;
        StatusText.Text = LocalizationService.Instance["Audit_Service_Scanning"];
        try
        {
            var result = await _service.ListAsync();
            _manager = result.Manager;
            ManagerText.Text = string.Format(LocalizationService.Instance["Service_Manager"], _manager);
            _items.Clear();
            foreach (var item in result.Services) _items.Add(item);
            StatusText.Text = string.Format(LocalizationService.Instance["Service_Found"], _items.Count);
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Instance.Error("SystemServicesUI", "InventoryFailed", ex, _session.Profile.Name);
            StatusText.Text = string.Format(LocalizationService.Instance["Service_ScanFailed"], ex.Message);
        }
        finally { IsEnabled = true; }
    }

    private async void Action_Click(object sender, RoutedEventArgs e)
    {
        if (ServicesList.SelectedItem is not RemoteServiceEntry selected) { StatusText.Text = LocalizationService.Instance["Audit_Service_Select"]; return; }
        var action = (sender as FrameworkElement)?.Tag?.ToString() ?? "";
        var verb = action switch { "start" => LocalizationService.Instance["Service_Start"], "stop" => LocalizationService.Instance["Service_Stop"], "restart" => LocalizationService.Instance["Service_Restart"], _ => LocalizationService.Instance["Service_Act"] };
        if (MessageBox.Show(this, string.Format(LocalizationService.Instance["Service_Confirm"], verb, selected.Name, _session.Profile.Name),
                LocalizationService.Instance["Audit_Service_ConfirmTitle"], MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        IsEnabled = false;
        try
        {
            var result = await _service.ExecuteAsync(_manager, selected.Name, action);
            await RefreshAsync();
            // RefreshAsync updates the status line with the inventory result. Restore the
            // action outcome afterwards so a successful/failed command is not hidden.
            StatusText.Text = result.ExitCode == 0
                ? string.Format(LocalizationService.Instance["Service_Success"], selected.Name)
                : string.Format(LocalizationService.Instance["Service_Failed"], result.ExitCode,
                    string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error);
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Instance.Error("SystemServicesUI", "ActionFailed", ex, _session.Profile.Name);
            StatusText.Text = string.Format(LocalizationService.Instance["Service_ActionFailed"], ex.Message);
        }
        finally { IsEnabled = true; }
    }
}