using System.Collections.ObjectModel;
using System.Windows;
using NodeBB.SSHManager.Services;

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
        StatusText.Text = "סורק שירותי מערכת...";
        try
        {
            var result = await _service.ListAsync();
            _manager = result.Manager;
            ManagerText.Text = $"מנהל שירותים: {_manager}";
            _items.Clear();
            foreach (var item in result.Services) _items.Add(item);
            StatusText.Text = $"נמצאו {_items.Count} שירותים.";
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Instance.Error("SystemServicesUI", "InventoryFailed", ex, _session.Profile.Name);
            StatusText.Text = "סריקת השירותים נכשלה: " + ex.Message;
        }
        finally { IsEnabled = true; }
    }

    private async void Action_Click(object sender, RoutedEventArgs e)
    {
        if (ServicesList.SelectedItem is not RemoteServiceEntry selected) { StatusText.Text = "בחר שירות תחילה."; return; }
        var action = (sender as FrameworkElement)?.Tag?.ToString() ?? "";
        var verb = action switch { "start" => "להפעיל", "stop" => "לעצור", "restart" => "לאתחל", _ => "לבצע פעולה על" };
        if (MessageBox.Show(this, $"האם {verb} את השירות '{selected.Name}' בשרת {_session.Profile.Name}?",
                "אישור שינוי שירות", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        IsEnabled = false;
        try
        {
            var result = await _service.ExecuteAsync(_manager, selected.Name, action);
            StatusText.Text = result.ExitCode == 0 ? $"הפעולה הסתיימה בהצלחה עבור {selected.Name}." :
                $"הפעולה נכשלה (קוד {result.ExitCode}): {result.Error}";
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Instance.Error("SystemServicesUI", "ActionFailed", ex, _session.Profile.Name);
            StatusText.Text = "הפעולה נכשלה: " + ex.Message;
        }
        finally { IsEnabled = true; }
    }
}