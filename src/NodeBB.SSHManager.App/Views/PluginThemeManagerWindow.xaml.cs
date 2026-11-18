using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using NodeBB.SSHManager.Services;

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
        StatusText.Text = "טוען חבילות מותקנות...";
        try
        {
            var items = await _service.ListInstalledAsync(_installation);
            _packages.Clear();
            foreach (var item in items) _packages.Add(item);
            StatusText.Text = $"נמצאו {_packages.Count} תוספים וערכות עיצוב.";
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Instance.Error("PackagesUI", "InventoryDisplayFailed", ex, _session.Profile.Name);
            StatusText.Text = "טעינת הרשימה נכשלה: " + ex.Message;
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
            StatusText.Text = "יש להזין שם חבילה תקין: nodebb-plugin-... או nodebb-theme-...";
            return;
        }

        var isTheme = packageName.StartsWith("nodebb-theme-", StringComparison.Ordinal);
        if (isTheme && (action is PackageAction.Activate or PackageAction.Deactivate))
        {
            StatusText.Text = "הפעלה והשבתה של ערכת עיצוב נעשות דרך ממשק הניהול של NodeBB.";
            return;
        }

        var description = action switch
        {
            PackageAction.Install => $"להתקין את {packageName}",
            PackageAction.Update => $"לעדכן את {packageName} לגרסה האחרונה",
            PackageAction.Remove => $"להסיר את {packageName}",
            PackageAction.Activate => $"להפעיל את התוסף {packageName}",
            PackageAction.Deactivate => $"להשבית ולאפס את התוסף {packageName}",
            _ => "לבצע פעולה"
        };
        var warning = action is PackageAction.Remove or PackageAction.Update
            ? "\n\nפעולה זו עשויה להשפיע על תאימות הפורום. ודא שיש גיבוי תקין."
            : "\n\nייתכן שתידרש בנייה מחדש או הפעלה מחדש של NodeBB.";
        if (MessageBox.Show(this, $"האם {description}?{warning}", "אישור שינוי חבילה",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        SetBusy(true, "מבצע פעולה...");
        try
        {
            var result = await _service.ExecuteAsync(_installation, packageName, action);
            var details = string.IsNullOrWhiteSpace(result.Output) ? result.Error : result.Output;
            StatusText.Text = result.ExitCode == 0
                ? "הפעולה הסתיימה בהצלחה."
                : $"הפעולה נכשלה (קוד {result.ExitCode}).";
            MessageBox.Show(this, $"{StatusText.Text}\n\n{details}", "תוצאת פעולה",
                MessageBoxButton.OK, result.ExitCode == 0 ? MessageBoxImage.Information : MessageBoxImage.Error);
            if (result.ExitCode == 0 && action is PackageAction.Install or PackageAction.Update or PackageAction.Remove)
                await RefreshAsync();
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Instance.Error("PackagesUI", "PackageActionFailed", ex, _session.Profile.Name);
            StatusText.Text = "הפעולה נכשלה: " + ex.Message;
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
