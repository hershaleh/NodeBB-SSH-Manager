using System.Collections.ObjectModel;
using System.Windows;
using NodeBB.SSHManager.Models;
using NodeBB.SSHManager.Services;
using NodeBB.SSHManager.Views;

namespace NodeBB.SSHManager;

public partial class MainWindow : Window
{
    private readonly ServerProfileStore _profileStore = new();
    private readonly SshConnectionService _sshService = new();
    private readonly SshSessionManager _sessionManager = new();
    private readonly ObservableCollection<ServerProfile> _profiles = [];

    public MainWindow()
    {
        InitializeComponent();
        ServersList.ItemsSource = _profiles;
        LoadProfiles();
        DiagnosticLogService.Instance.Info("MainWindow", "DashboardLoaded", "Main dashboard loaded.");
        Closed += (_, _) =>
        {
            DiagnosticLogService.Instance.Info("MainWindow", "ApplicationClosing", "Main window closed.");
            _sessionManager.Dispose();
        };
    }

    private void LoadProfiles()
    {
        try
        {
            foreach (var profile in _profileStore.Load()) _profiles.Add(profile);
            RefreshDashboard();
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Instance.Error("Profiles", "LoadFailed", ex);
            MessageBox.Show(this, ex.Message, "שגיאה בטעינת שרתים", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void AddServer_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new AddServerWindow { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Profile is null) return;
        if (_profiles.Any(p => string.Equals(p.Host, dialog.Profile.Host, StringComparison.OrdinalIgnoreCase)
                               && p.Port == dialog.Profile.Port
                               && string.Equals(p.Username, dialog.Profile.Username, StringComparison.Ordinal)))
        {
            MessageBox.Show(this, "שרת עם אותה כתובת, יציאה ומשתמש כבר קיים.", "שרת קיים",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _profiles.Add(dialog.Profile);
        DiagnosticLogService.Instance.Info("Profiles", "ServerProfileAdded", "A server profile was added.", dialog.Profile.Name);
        SaveProfiles();
        ServersList.SelectedItem = dialog.Profile;
        RefreshDashboard();
    }

    private void RemoveServer_Click(object sender, RoutedEventArgs e)
    {
        if (ServersList.SelectedItem is not ServerProfile selected)
        {
            MessageBox.Show(this, "בחר שרת להסרה.", "לא נבחר שרת", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (MessageBox.Show(this, $"להסיר את הפרופיל '{selected.Name}' מהמחשב? הפעולה אינה מוחקת דבר מהשרת עצמו.",
                "אישור הסרת פרופיל", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _sessionManager.Disconnect(selected.Id);
        DiagnosticLogService.Instance.Info("Profiles", "ServerProfileRemoved", "A server profile was removed.", selected.Name);
        _profiles.Remove(selected);
        SaveProfiles();
        RefreshDashboard();
    }

    private async void TestConnection_Click(object sender, RoutedEventArgs e)
    {
        if (ServersList.SelectedItem is not ServerProfile selected)
        {
            MessageBox.Show(this, "בחר שרת לבדיקה.", "לא נבחר שרת", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var prompt = new SecretPromptWindow(selected) { Owner = this };
        if (prompt.ShowDialog() != true) return;
        SetBusy(true);
        try
        {
            var result = await _sshService.TestConnectionAsync(selected, prompt.Secret);
            if (result.Status == SshTestStatus.HostKeyVerificationRequired && result.Fingerprint is not null)
            {
                if (!ConfirmHostFingerprint(selected, result.Fingerprint)) return;
                selected.KnownHostFingerprint = result.Fingerprint;
                SaveProfiles();
                result = await _sshService.TestConnectionAsync(selected, prompt.Secret);
            }
            ShowConnectionResult(result);
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Instance.Error("SSH", "ConnectionTestFailed", ex, selected.Name);
            MessageBox.Show(this, ex.Message, "שגיאת חיבור", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { SetBusy(false); }
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        var profile = GetSelectedProfile();
        if (profile is null) return;
        await EnsureConnectedAsync(profile);
    }

    private void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        var profile = GetSelectedProfile();
        if (profile is null) return;
        _sessionManager.Disconnect(profile.Id);
        RefreshDashboard();
        MessageBox.Show(this, $"נותק החיבור אל {profile.Name}.", "החיבור נותק", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void OpenTerminal_Click(object sender, RoutedEventArgs e)
    {
        var session = await GetSelectedSessionAsync();
        if (session is null) return;
        new SshTerminalWindow(session) { Owner = this }.Show();
    }

    private async void OpenFiles_Click(object sender, RoutedEventArgs e)
    {
        var session = await GetSelectedSessionAsync();
        if (session is null) return;
        new SftpFileManagerWindow(session) { Owner = this }.Show();
    }

    private async void ManageNodeBb_Click(object sender, RoutedEventArgs e)
    {
        var session = await GetSelectedSessionAsync();
        if (session is null) return;
        new NodeBbManagerWindow(session) { Owner = this }.ShowDialog();
    }

    private async Task<SshServerSession?> GetSelectedSessionAsync()
    {
        var profile = GetSelectedProfile();
        return profile is null ? null : await EnsureConnectedAsync(profile);
    }

    private ServerProfile? GetSelectedProfile()
    {
        if (ServersList.SelectedItem is ServerProfile profile) return profile;
        MessageBox.Show(this, "בחר שרת תחילה.", "לא נבחר שרת", MessageBoxButton.OK, MessageBoxImage.Information);
        return null;
    }

    private async Task<SshServerSession?> EnsureConnectedAsync(ServerProfile profile)
    {
        if (_sessionManager.TryGet(profile.Id, out var active)) return active;
        var prompt = new SecretPromptWindow(profile) { Owner = this };
        if (prompt.ShowDialog() != true) return null;
        SetBusy(true);
        try
        {
            var check = await _sshService.TestConnectionAsync(profile, prompt.Secret);
            if (check.Status == SshTestStatus.HostKeyVerificationRequired && check.Fingerprint is not null)
            {
                if (!ConfirmHostFingerprint(profile, check.Fingerprint)) return null;
                profile.KnownHostFingerprint = check.Fingerprint;
                SaveProfiles();
                check = await _sshService.TestConnectionAsync(profile, prompt.Secret);
            }
            if (check.Status != SshTestStatus.Success)
            {
                ShowConnectionResult(check);
                return null;
            }
            var session = await _sessionManager.ConnectAsync(profile, prompt.Secret);
            RefreshDashboard();
            MessageBox.Show(this, $"חיבור SSH מתמשך נוצר בהצלחה אל {profile.Name}.",
                "החיבור פעיל", MessageBoxButton.OK, MessageBoxImage.Information);
            return session;
        }
        catch (SshHostKeyException ex)
        {
            DiagnosticLogService.Instance.Warning("SSH", "HostKeyRejected", ex.Message, ex, profile.Name);
            MessageBox.Show(this, $"טביעת האצבע לא אומתה: {ex.Fingerprint}", "אימות שרת נכשל",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return null;
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Instance.Error("SSH", "PersistentConnectionFailed", ex, profile.Name);
            MessageBox.Show(this, ex.Message, "שגיאת חיבור", MessageBoxButton.OK, MessageBoxImage.Error);
            return null;
        }
        finally { SetBusy(false); }
    }

    private bool ConfirmHostFingerprint(ServerProfile profile, string fingerprint)
    {
        var changed = !string.IsNullOrWhiteSpace(profile.KnownHostFingerprint);
        var warning = changed
            ? "טביעת האצבע של השרת השתנתה. ייתכן שהשרת הותקן מחדש, אך הדבר עלול גם להעיד על התחזות. אמת אותה בערוץ נפרד לפני האישור."
            : "זו הפעם הראשונה שמתחברים לשרת. מומלץ לאמת את טביעת האצבע מול ספק השרת או במסוף השרת לפני האישור.";
        return MessageBox.Show(this,
            $"{warning}\n\n{profile.Host}:{profile.Port}\n\n{fingerprint}\n\nלאשר ולשמור טביעת אצבע זו?",
            changed ? "אזהרת שינוי מפתח שרת" : "אישור שרת SSH חדש",
            MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
    }

    private void ShowConnectionResult(SshTestResult result)
    {
        if (result.Status == SshTestStatus.Success)
            MessageBox.Show(this, $"{result.Message}\n\nמידע מהשרת:\n{result.RemoteSummary}\n\nטביעת אצבע:\n{result.Fingerprint}",
                "בדיקת SSH הצליחה", MessageBoxButton.OK, MessageBoxImage.Information);
        else
            MessageBox.Show(this, result.Message, "בדיקת SSH נכשלה", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private async void ServerMonitoring_Click(object sender, RoutedEventArgs e)
    {
        var session = await GetSelectedSessionAsync();
        if (session is null) return;
        new ServerMonitoringWindow(session) { Owner = this }.ShowDialog();
    }

    private async void ServerDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        var session = await GetSelectedSessionAsync();
        if (session is null) return;
        new ServerDiagnosticsWindow(session) { Owner = this }.ShowDialog();
    }

    private void Diagnostics_Click(object sender, RoutedEventArgs e)
    {
        new DiagnosticsWindow { Owner = this }.ShowDialog();
    }

    private void SaveProfiles()
    {
        try { _profileStore.Save(_profiles); }
        catch (Exception ex)
        {
            DiagnosticLogService.Instance.Error("Profiles", "SaveFailed", ex);
            MessageBox.Show(this, $"לא ניתן לשמור את פרופילי השרתים.\n{ex.Message}", "שגיאת שמירה",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RefreshDashboard()
    {
        ServerCountText.Text = _profiles.Count.ToString();
        ServerTotalText.Text = $"{_profiles.Count} שרתים";
        ConnectedCountText.Text = _sessionManager.ConnectedCount.ToString();
        EmptyState.Visibility = _profiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ServerListPanel.Visibility = _profiles.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SetBusy(bool isBusy)
    {
        IsEnabled = !isBusy;
        Cursor = isBusy ? System.Windows.Input.Cursors.Wait : System.Windows.Input.Cursors.Arrow;
    }
}
