using System;
using NodeBB.SSHManager.Localization;
using System.Collections.ObjectModel;
using System.Windows;
using NodeBB.SSHManager.Services;

namespace NodeBB.SSHManager.Views;

public partial class NodeBbManagerWindow : Window
{
    private readonly NodeBbRemoteService _service;
    private readonly NodeBbInstallationStore _installationStore;
    private readonly SshServerSession _session;
    private readonly ObservableCollection<NodeBbInstallation> _installations = [];
    private bool _isBusy;

    public NodeBbManagerWindow(SshServerSession session)
    {
        InitializeComponent();
        _session = session;
        _service = new NodeBbRemoteService(session);
        _installationStore = new NodeBbInstallationStore(session.Profile.Id);
        ServerLabel.Text = $"{session.Profile.Name} · {session.Profile.Host}";
        InstallationsList.ItemsSource = _installations;
        InstallationPicker.ItemsSource = _installations;
        UpdateActionAvailability();
        Loaded += async (_, _) => await DiscoverInstallationsAsync();
    }

    private void InstallationsList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    { if (InstallationsList.SelectedItem is NodeBbInstallation item) { if (!Equals(InstallationPicker.SelectedItem, item)) InstallationPicker.SelectedItem = item; } UpdateActionAvailability(); }

    private void InstallationPicker_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    { if (InstallationPicker.SelectedItem is NodeBbInstallation item) { if (!Equals(InstallationsList.SelectedItem, item)) InstallationsList.SelectedItem = item; SelectedInstallationText.Text = $"{item.Path}  ·  {item.Version}"; } }

    private void UpdateActionAvailability()
    {
        var enabled = !_isBusy && InstallationsList.SelectedItem is NodeBbInstallation;
        if (StartButton is not null) StartButton.IsEnabled = enabled;
        if (StopButton is not null) StopButton.IsEnabled = enabled;
        if (RestartButton is not null) RestartButton.IsEnabled = enabled;
        if (BuildButton is not null) BuildButton.IsEnabled = enabled;
        if (UpgradeButton is not null) UpgradeButton.IsEnabled = enabled;
        if (PackagesButton is not null) PackagesButton.IsEnabled = enabled;
        if (BackupsButton is not null) BackupsButton.IsEnabled = enabled;
        if (DatabaseButton is not null) DatabaseButton.IsEnabled = enabled;
        if (LogsButton is not null) LogsButton.IsEnabled = enabled;
        if (SecurityButton is not null) SecurityButton.IsEnabled = enabled;
        if (DiscoverButton is not null) DiscoverButton.IsEnabled = !_isBusy;
        if (AddInstallationButton is not null) AddInstallationButton.IsEnabled = !_isBusy;
    }

    private async void Discover_Click(object sender, RoutedEventArgs e) =>
        await DiscoverInstallationsAsync();

    private async void AddInstallation_Click(object sender, RoutedEventArgs e)
    {
        var path = ManualPathTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(path))
        {
            StatusText.Text = T("הזן נתיב התקנה מלא.");
            ManualPathTextBox.Focus();
            return;
        }

        try
        {
            SetBusy(true, T("בודק את נתיב ההתקנה..."));
            var installation = await _service.InspectInstallationAsync(path);
            var existing = _installations.FirstOrDefault(item =>
                string.Equals(item.Path, installation.Path, StringComparison.Ordinal));
            if (existing is not null)
            {
                _installationStore.Add(existing.Path);
                InstallationsList.SelectedItem = existing;
                InstallationPicker.SelectedItem = existing;
                StatusText.Text = T("הנתיב כבר מופיע ברשימה; ההתקנה נבחרה.");
                return;
            }

            _installationStore.Add(installation.Path);
            _installations.Add(installation);
            InstallationsList.SelectedItem = installation;
            InstallationPicker.SelectedItem = installation;
            ManualPathTextBox.Clear();
            StatusText.Text = string.Format(T("התקנת NodeBB נוספה ונבחרה: {0} ({1})"), installation.Path, installation.Version);
            DiagnosticLogService.Instance.Info("NodeBBManagerUI", "ManualInstallationAdded",
                $"Manually added NodeBB installation {installation.Path} ({installation.Version}).", _session.Profile.Name);
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Instance.Error("NodeBBManagerUI", "ManualInstallationFailed", ex, _session.Profile.Name);
            StatusText.Text = string.Format(T("הוספת ההתקנה נכשלה: {0}"), ex.Message);
        }
        finally { SetBusy(false, StatusText.Text); }
    }

    private async Task DiscoverInstallationsAsync()
    {
        try
        {
            SetBusy(true, T("סורק נתיבים נפוצים..."));
            var found = (await _service.DiscoverAsync()).ToList();
            try
            {
                foreach (var savedPath in _installationStore.Load())
                {
                    if (found.Any(item => string.Equals(item.Path, savedPath, StringComparison.Ordinal)))
                        continue;
                    try
                    {
                        found.Add(await _service.InspectInstallationAsync(savedPath));
                    }
                    catch (Exception ex)
                    {
                        DiagnosticLogService.Instance.Warning("NodeBBManagerUI", "SavedInstallationUnavailable",
                            $"Saved NodeBB installation path {savedPath} could not be validated; it remains saved for a later attempt.", ex, _session.Profile.Name);
                    }
                }
            }
            catch (Exception ex)
            {
                DiagnosticLogService.Instance.Warning("NodeBBManagerUI", "SavedInstallationLoadFailed",
                    "Could not load saved manual NodeBB installation paths; automatic discovery results will still be shown.", ex, _session.Profile.Name);
            }
            _installations.Clear();
            foreach (var item in found.OrderBy(item => item.Path, StringComparer.Ordinal)) _installations.Add(item);
            if (found.Count > 0) { InstallationsList.SelectedIndex = 0; InstallationPicker.SelectedIndex = 0; }
            StatusText.Text = found.Count == 0
                ? T("לא נמצאה התקנת NodeBB בנתיבים שנבדקו. אפשר לסרוק שוב או לבדוק ידנית דרך מסוף SSH.")
                : F($"נמצאו {found.Count} התקנות. נבחרה ההתקנה הראשונה.");
            DiagnosticLogService.Instance.Info("NodeBBManagerUI", "DiscoveryCompleted",
                $"Discovered {found.Count} NodeBB installations.", _session.Profile.Name);
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Instance.Error("NodeBBManagerUI", "DiscoveryFailed", ex, _session.Profile.Name);
            StatusText.Text = T("הסריקה נכשלה: ") + ex.Message;
        }
        finally { SetBusy(false, StatusText.Text); }
    }

    private void Security_Click(object sender, RoutedEventArgs e)
    {
        if (InstallationsList.SelectedItem is not NodeBbInstallation installation)
        {
            StatusText.Text = T("בחר התקנת NodeBB תחילה.");
            return;
        }
        new SecurityAuditWindow(_session, installation) { Owner = this }.ShowDialog();
    }

    private void Logs_Click(object sender, RoutedEventArgs e)
    {
        if (InstallationsList.SelectedItem is not NodeBbInstallation installation)
        {
            StatusText.Text = T("בחר התקנת NodeBB תחילה.");
            return;
        }
        new NodeBbLogsWindow(_session, installation) { Owner = this }.ShowDialog();
    }

    private void Database_Click(object sender, RoutedEventArgs e)
    {
        if (InstallationsList.SelectedItem is not NodeBbInstallation installation)
        {
            StatusText.Text = T("בחר התקנת NodeBB תחילה.");
            return;
        }
        new DatabaseManagerWindow(_session, installation) { Owner = this }.ShowDialog();
    }

    private void Backups_Click(object sender, RoutedEventArgs e)
    {
        if (InstallationsList.SelectedItem is not NodeBbInstallation installation)
        {
            StatusText.Text = T("בחר התקנת NodeBB תחילה.");
            return;
        }
        new BackupWindow(_session, installation) { Owner = this }.ShowDialog();
    }

    private void Packages_Click(object sender, RoutedEventArgs e)
    {
        if (InstallationsList.SelectedItem is not NodeBbInstallation installation)
        {
            StatusText.Text = T("בחר התקנת NodeBB תחילה.");
            return;
        }
        new PluginThemeManagerWindow(_session, installation) { Owner = this }.ShowDialog();
    }

    private async void Start_Click(object sender, RoutedEventArgs e) => await RunActionAsync("start");
    private async void Stop_Click(object sender, RoutedEventArgs e) => await RunActionAsync("stop");
    private async void Restart_Click(object sender, RoutedEventArgs e) => await RunActionAsync("restart");
    private async void Build_Click(object sender, RoutedEventArgs e) => await RunActionAsync("build");

    private void Upgrade_Click(object sender, RoutedEventArgs e)
    {
        if (InstallationsList.SelectedItem is not NodeBbInstallation installation)
        { StatusText.Text = T("בחר התקנת NodeBB תחילה."); return; }
        new NodeBbUpgradeWindow(_session, installation) { Owner = this }.ShowDialog();
    }

    private async Task RunActionAsync(string action)
    {
        if (InstallationsList.SelectedItem is not NodeBbInstallation installation)
        {
            StatusText.Text = T("בחר התקנת NodeBB תחילה.");
            return;
        }
        var label = action switch { "start" => T("להפעיל"), "stop" => T("לעצור"), "restart" => T("להפעיל מחדש"), _ => T("לבנות מחדש") };
        if (MessageBox.Show(this, F($"האם {label} את NodeBB בנתיב {installation.Path}?"), T("אישור פעולה"),
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            SetBusy(true, F($"מבצע {label}..."));
            var result = await _service.RunLifecycleAsync(installation, action);
            var details = string.Join(Environment.NewLine + Environment.NewLine,
                new[] { result.Output.Trim(), result.Error.Trim() }.Where(value => !string.IsNullOrWhiteSpace(value)));
            StatusText.Text = result.ExitCode == 0 ? F($"הפעולה {label} הסתיימה בהצלחה.") : F($"הפעולה נכשלה (קוד {result.ExitCode}).");
            DiagnosticLogService.Instance.Info("NodeBBManagerUI", "LifecycleActionCompleted",
                $"NodeBB action {action} completed with exit code {result.ExitCode}.", _session.Profile.Name);
            MessageBox.Show(this, $"{StatusText.Text}{(string.IsNullOrWhiteSpace(details) ? string.Empty : Environment.NewLine + Environment.NewLine + details)}", T("תוצאת פעולה"),
                MessageBoxButton.OK, result.ExitCode == 0 ? MessageBoxImage.Information : MessageBoxImage.Error);
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Instance.Error("NodeBBManagerUI", "LifecycleActionFailed", ex, _session.Profile.Name);
            StatusText.Text = T("שגיאה: ") + ex.Message;
            MessageBox.Show(this, ex.Message, T("פעולת NodeBB נכשלה"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { SetBusy(false, StatusText.Text); }
    }

    private void Terminal_Click(object sender, RoutedEventArgs e) => new SshTerminalWindow(_session) { Owner = this }.Show();

    private void Monitoring_Click(object sender, RoutedEventArgs e) => new ServerMonitoringWindow(_session) { Owner = this }.ShowDialog();
    private void Diagnostics_Click(object sender, RoutedEventArgs e) => new ServerDiagnosticsWindow(_session) { Owner = this }.ShowDialog();
    private void Services_Click(object sender, RoutedEventArgs e) => new SystemServicesWindow(_session) { Owner = this }.ShowDialog();

    private void SetBusy(bool busy, string message)
    {
        _isBusy = busy;
        UpdateActionAvailability();
        StatusText.Text = message;
    }
    private static string T(string text) => LocalizationService.Instance.Translate(text);

    private static string F(FormattableString text) => LocalizationService.Instance.TranslateFormat(text);

}
