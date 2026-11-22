using System.Collections.ObjectModel;
using System.Windows;
using NodeBB.SSHManager.Services;
using NodeBB.SSHManager.Localization;

namespace NodeBB.SSHManager.Views;

public sealed record ServerComparisonRow(
    string Server, string Host, string OperatingSystem, string Kernel, string Architecture,
    string Node, string Npm, string Git, string Mongo, string Redis, string Docker, string Status);

public partial class ServerComparisonWindow : Window
{
    private readonly SshServerSession[] _sessions;
    private readonly ObservableCollection<ServerComparisonRow> _rows = [];
    private bool _refreshing;

    public ServerComparisonWindow(IEnumerable<SshServerSession> sessions)
    {
        InitializeComponent();
        _sessions = sessions.Where(session => session.IsConnected).ToArray();
        ComparisonGrid.ItemsSource = _rows;
        Loaded += async (_, _) => await RefreshAsync();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async Task RefreshAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        RefreshButton.IsEnabled = false;
        StatusText.Text = LocalizationService.Instance["Compare_Collecting"];
        try
        {
            var results = await Task.WhenAll(_sessions.Select(CollectOneAsync));
            _rows.Clear();
            foreach (var row in results) _rows.Add(row);
            var succeeded = results.Count(row => row.Status == LocalizationService.Instance["Compare_Success"]);
            StatusText.Text = string.Format(LocalizationService.Instance["Compare_SuccessCount"], succeeded, _sessions.Length);
        }
        catch (Exception ex)
        {
            StatusText.Text = string.Format(LocalizationService.Instance["Compare_Failed"], ex.Message);
            DiagnosticLogService.Instance.Error("ServerComparison", "RefreshFailed", ex);
        }
        finally { RefreshButton.IsEnabled = true; _refreshing = false; }
    }

    private static async Task<ServerComparisonRow> CollectOneAsync(SshServerSession session)
    {
        try
        {
            var probe = await session.ExecuteAsync("help");
            var help = probe.Output + Environment.NewLine + probe.Error;
            if (help.Contains("NodeBB Plugin Management CLI (Restricted)", StringComparison.OrdinalIgnoreCase)
                || help.Contains("nodebb-plugins>", StringComparison.OrdinalIgnoreCase))
                return Empty(session, LocalizationService.Instance["Compare_Restricted"]);

            var inventory = await new RemoteCapabilityService(session).DetectAsync();
            return new ServerComparisonRow(session.Profile.Name, session.Profile.HostDisplay,
                inventory.OperatingSystem, inventory.Kernel, inventory.Architecture,
                inventory.NodeVersion, inventory.NpmVersion, inventory.GitVersion,
                inventory.MongoVersion, inventory.RedisVersion, inventory.DockerVersion, LocalizationService.Instance["Compare_Success"]);
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Instance.Warning("ServerComparison", "ServerCollectionFailed",
                "A server could not be included in the comparison.", ex, session.Profile.Name);
            return Empty(session, string.Format(LocalizationService.Instance["Compare_Unavailable"], ex.Message));
        }
    }

    private static ServerComparisonRow Empty(SshServerSession session, string status) =>
        new(session.Profile.Name, session.Profile.HostDisplay, "—", "—", "—", "—", "—", "—", "—", "—", "—", status);
}
