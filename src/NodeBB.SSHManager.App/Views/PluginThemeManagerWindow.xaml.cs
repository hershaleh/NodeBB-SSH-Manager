using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using NodeBB.SSHManager.Services;
using NodeBB.SSHManager.Localization;

namespace NodeBB.SSHManager.Views;

public partial class PluginThemeManagerWindow : Window
{
    private readonly PluginThemeRemoteService _service;
    private readonly SshServerSession _session;
    private readonly NodeBbInstallation _installation;
    private readonly ObservableCollection<NodeBbPackage> _packages = [];

    public PluginThemeManagerWindow(SshServerSession session, NodeBbInstallation installation)
    {
        InitializeComponent();
        _session = session;
        _installation = installation;
        _service = new PluginThemeRemoteService(session);
        InstallationLabel.Text = $"{session.Profile.Name} · {_installation.Path}";
        PackagesList.ItemsSource = _packages;
        Loaded += async (_, _) => await RefreshAsync();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private void PackagesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PackagesList.SelectedItem is NodeBbPackage package)
            PackageNameBox.Text = package.Name;
    }

    private async Task RefreshAsync()
    {
        RefreshButton.IsEnabled = false;
        StatusText.Text = LocalizationService.Instance["Audit_Plugin_Loading"];
        try
        {
            var items = await _service.ListInstalledAsync(_installation);
            _packages.Clear();
            foreach (var item in items) _packages.Add(item);
            StatusText.Text = string.Format(LocalizationService.Instance["Plugin_Found"], _packages.Count);
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Instance.Error("PackagesUI", "InventoryDisplayFailed", ex, _session.Profile.Name);
            StatusText.Text = string.Format(LocalizationService.Instance["Plugin_LoadFailed"], ex.Message);
        }
        finally { RefreshButton.IsEnabled = true; }
    }

    private async void Install_Click(object sender, RoutedEventArgs e) => await RunActionAsync(PackageAction.Install);
    private async void Update_Click(object sender, RoutedEventArgs e) => await RunActionAsync(PackageAction.Update);
    private async void Activate_Click(object sender, RoutedEventArgs e) => await RunActionAsync(PackageAction.Activate);
    private async void Deactivate_Click(object sender, RoutedEventArgs e) => await RunActionAsync(PackageAction.Deactivate);
    private async void Remove_Click(object sender, RoutedEventArgs e) => await RunActionAsync(PackageAction.Remove);

    private async Task RunActionAsync(PackageAction action)
    {
        var packageName = PackageNameBox.Text.Trim();
        if (!PluginThemeRemoteService.IsValidPackageName(packageName))
        {
            StatusText.Text = LocalizationService.Instance["Audit_Plugin_InvalidName"];
            return;
        }

        var isTheme = packageName.StartsWith("nodebb-theme-", StringComparison.Ordinal);
        if (isTheme && (action is PackageAction.Activate or PackageAction.Deactivate))
        {
            StatusText.Text = LocalizationService.Instance["Audit_Plugin_ThemeActivation"];
            return;
        }

        var description = action switch
        {
            PackageAction.Install => string.Format(LocalizationService.Instance["Plugin_Install"], packageName),
            PackageAction.Update => string.Format(LocalizationService.Instance["Plugin_Update"], packageName),
            PackageAction.Remove => string.Format(LocalizationService.Instance["Plugin_Remove"], packageName),
            PackageAction.Activate => string.Format(LocalizationService.Instance["Plugin_Activate"], packageName),
            PackageAction.Deactivate => string.Format(LocalizationService.Instance["Plugin_Deactivate"], packageName),
            _ => LocalizationService.Instance["Plugin_Action"]
        };
        var warning = action switch
        {
            PackageAction.Remove or PackageAction.Update => LocalizationService.Instance["Plugin_WarnRisk"],
            PackageAction.Deactivate => LocalizationService.Instance["Plugin_WarnDeactivate"],
            _ => LocalizationService.Instance["Plugin_WarnBuild"]
        };
        if (MessageBox.Show(this, string.Format(LocalizationService.Instance["Plugin_Confirm"], description, warning), LocalizationService.Instance["Audit_Service_ConfirmTitle"],
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        SetBusy(true, LocalizationService.Instance["Audit_Plugin_ActionRunning"]);
        try
        {
            var result = await _service.ExecuteAsync(_installation, packageName, action);
            var details = string.Join(Environment.NewLine + Environment.NewLine,
                new[] { result.Output.Trim(), result.Error.Trim() }.Where(value => !string.IsNullOrWhiteSpace(value)));
            StatusText.Text = result.ExitCode == 0
                ? LocalizationService.Instance["Audit_Plugin_ActionSuccess"]
                : string.Format(LocalizationService.Instance["Plugin_Failed"], result.ExitCode);
            MessageBox.Show(this, $"{StatusText.Text}\n\n{details}", LocalizationService.Instance["Plugin_Result"],
                MessageBoxButton.OK, result.ExitCode == 0 ? MessageBoxImage.Information : MessageBoxImage.Error);
            if (result.ExitCode == 0)
                await RefreshAsync();
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Instance.Error("PackagesUI", "PackageActionFailed", ex, _session.Profile.Name);
            StatusText.Text = string.Format(LocalizationService.Instance["Plugin_ActionFailed"], ex.Message);
        }
        finally { SetBusy(false, StatusText.Text); }
    }

    private void SetBusy(bool busy, string message)
    {
        IsEnabled = !busy;
        RefreshButton.IsEnabled = !busy;
        StatusText.Text = message;
    }
}
