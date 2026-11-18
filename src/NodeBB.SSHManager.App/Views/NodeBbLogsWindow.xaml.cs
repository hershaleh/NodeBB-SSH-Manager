using System.Windows;
using System.Windows.Controls;
using NodeBB.SSHManager.Services;

namespace NodeBB.SSHManager.Views;

public partial class NodeBbLogsWindow : Window
{
    private readonly NodeBbLogService _service;
    private readonly NodeBbInstallation _installation;
    private readonly SshServerSession _session;
    private string _rawLogs = "";

    public NodeBbLogsWindow(SshServerSession session, NodeBbInstallation installation)
    {
        InitializeComponent();
        _session = session;
        _installation = installation;
        _service = new NodeBbLogService(session);
        InstallationLabel.Text = $"{session.Profile.Name} · {_installation.Path}";
        Loaded += async (_, _) => await RefreshAsync();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async Task RefreshAsync()
    {
        RefreshButton.IsEnabled = false;
        StatusText.Text = "קורא לוגים...";
        try
        {
            _rawLogs = await _service.ReadRecentLogsAsync(_installation);
            ApplyFilter();
            StatusText.Text = "הלוגים עודכנו.";
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Instance.Error("NodeBBLogsUI", "RefreshFailed", ex, _session.Profile.Name);
            StatusText.Text = "קריאת הלוגים נכשלה: " + ex.Message;
        }
        finally { RefreshButton.IsEnabled = true; }
    }

    private void Search_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        if (LogTextBox is null) return;
        var query = SearchBox.Text.Trim();
        if (string.IsNullOrEmpty(query))
        {
            LogTextBox.Text = _rawLogs;
            return;
        }

        LogTextBox.Text = string.Join(Environment.NewLine,
            _rawLogs.Split('\n').Where(line => line.Contains(query, StringComparison.OrdinalIgnoreCase)));
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(LogTextBox.Text);
            StatusText.Text = "התצוגה הועתקה ללוח.";
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Instance.Error("NodeBBLogsUI", "CopyFailed", ex, _session.Profile.Name);
            StatusText.Text = "ההעתקה נכשלה: " + ex.Message;
        }
    }
}
