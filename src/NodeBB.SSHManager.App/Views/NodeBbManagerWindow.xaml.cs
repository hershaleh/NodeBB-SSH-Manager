using System.Collections.ObjectModel;
using System.Windows;
using NodeBB.SSHManager.Services;

namespace NodeBB.SSHManager.Views;

public partial class NodeBbManagerWindow : Window
{
    private readonly NodeBbRemoteService _service;
    private readonly SshServerSession _session;
    private readonly ObservableCollection<NodeBbInstallation> _installations = [];

    public NodeBbManagerWindow(SshServerSession session)
    {
        InitializeComponent();
        _session = session;
        _service = new NodeBbRemoteService(session);
        ServerLabel.Text = $"{session.Profile.Name} · {session.Profile.Host}";
        InstallationsList.ItemsSource = _installations;
    }

    private async void Discover_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SetBusy(true, "סורק נתיבים נפוצים...");
            var found = await _service.DiscoverAsync();
            _installations.Clear();
            foreach (var item in found) _installations.Add(item);
            StatusText.Text = found.Count == 0 ? "לא נמצאה התקנת NodeBB בנתיבים שנבדקו." : $"נמצאו {found.Count} התקנות.";
        }
        catch (Exception ex) { StatusText.Text = "הסריקה נכשלה: " + ex.Message; }
        finally { SetBusy(false, StatusText.Text); }
    }

    private void Packages_Click(object sender, RoutedEventArgs e)
    {
        if (InstallationsList.SelectedItem is not NodeBbInstallation installation)
        {
            StatusText.Text = "בחר התקנת NodeBB תחילה.";
            return;
        }
        new PluginThemeManagerWindow(_session, installation) { Owner = this }.ShowDialog();
    }

    private async void Start_Click(object sender, RoutedEventArgs e) => await RunActionAsync("start");
    private async void Stop_Click(object sender, RoutedEventArgs e) => await RunActionAsync("stop");
    private async void Restart_Click(object sender, RoutedEventArgs e) => await RunActionAsync("restart");
    private async void Build_Click(object sender, RoutedEventArgs e) => await RunActionAsync("build");

    private async Task RunActionAsync(string action)
    {
        if (InstallationsList.SelectedItem is not NodeBbInstallation installation)
        {
            StatusText.Text = "בחר התקנת NodeBB תחילה.";
            return;
        }
        var label = action switch { "start" => "להפעיל", "stop" => "לעצור", "restart" => "להפעיל מחדש", _ => "לבנות מחדש" };
        if (MessageBox.Show(this, $"האם {label} את NodeBB בנתיב {installation.Path}?", "אישור פעולה",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            SetBusy(true, $"מבצע {action}...");
            var result = await _service.RunLifecycleAsync(installation, action);
            var details = string.IsNullOrWhiteSpace(result.Output) ? result.Error : result.Output;
            StatusText.Text = result.ExitCode == 0 ? $"הפעולה {action} הסתיימה בהצלחה." : $"הפעולה נכשלה (קוד {result.ExitCode}).";
            MessageBox.Show(this, $"{StatusText.Text}\n\n{details}", "תוצאת פעולה",
                MessageBoxButton.OK, result.ExitCode == 0 ? MessageBoxImage.Information : MessageBoxImage.Error);
        }
        catch (Exception ex) { StatusText.Text = "שגיאה: " + ex.Message; }
        finally { SetBusy(false, StatusText.Text); }
    }

    private void SetBusy(bool busy, string message)
    {
        DiscoverButton.IsEnabled = !busy;
        StatusText.Text = message;
    }
}
