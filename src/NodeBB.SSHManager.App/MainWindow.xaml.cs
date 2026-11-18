using System.Collections.ObjectModel;
using System.Windows;
using Microsoft.Win32;
using NodeBB.SSHManager.Models;
using NodeBB.SSHManager.Services;
using NodeBB.SSHManager.Views;

namespace NodeBB.SSHManager;

public partial class MainWindow : Window
{
    private readonly ServerProfileStore _profileStore = new();
    private readonly SshConnectionService _sshService = new();
    private readonly SshSessionManager _sessionManager = new();
    private readonly CredentialVaultService _credentialVault = new();
    private readonly ServerProfileTransferService _profileTransfer = new();
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

    private void ExportProfiles_Click(object sender, RoutedEventArgs e)
    {
        if (_profiles.Count == 0)
        {
            MessageBox.Show(this, "אין פרופילים לייצוא.", "ייצוא פרופילים", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dialog = new SaveFileDialog { Title = "ייצוא פרופילי שרתים", Filter = "NodeBB SSH profiles (*.nbbprofiles.json)|*.nbbprofiles.json|JSON (*.json)|*.json", FileName = "nodebb-ssh-profiles.nbbprofiles.json", AddExtension = true, DefaultExt = ".json" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var json = _profileTransfer.Export(_profiles);
            File.WriteAllText(dialog.FileName, json, new System.Text.UTF8Encoding(false));
            MessageBox.Show(this, $"יוצאו {_profiles.Count} פרופילים. סיסמאות וטביעות אצבע SSH אינן כלולות בקובץ.", "הייצוא הושלם", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Instance.Error("Profiles", "ExportFailed", ex);
            MessageBox.Show(this, ex.Message, "ייצוא נכשל", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ImportProfiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "ייבוא פרופילי שרתים", Filter = "NodeBB SSH profiles (*.json)|*.json|All files (*.*)|*.*", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var fileInfo = new FileInfo(dialog.FileName);
            if (fileInfo.Length > 2 * 1024 * 1024) throw new InvalidDataException("קובץ הפרופילים גדול מהמותר (2MB).");
            var json = File.ReadAllText(dialog.FileName);
            var imported = _profileTransfer.Import(json);
            var knownEndpoints = _profiles.Select(p => (Host: p.Host.ToUpperInvariant(), p.Port, p.Username)).ToHashSet();
            var additions = imported.Where(candidate => knownEndpoints.Add((candidate.Host.ToUpperInvariant(), candidate.Port, candidate.Username))).ToList();
            var skipped = imported.Count - additions.Count;
            if (additions.Count == 0)
            {
                MessageBox.Show(this, $"לא נוספו פרופילים. {skipped} פרופילים כבר קיימים.", "ייבוא פרופילים", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (MessageBox.Show(this, $"נמצאו {imported.Count} פרופילים תקינים. להוסיף {additions.Count}?\nפרופילים קיימים ידולגו. סיסמאות וטביעות אצבע SSH לא מיובאות, ולכן תידרש בדיקת אמון מחדש בשרת.", "אישור ייבוא", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            foreach (var profile in additions) _profiles.Add(profile);
            if (!SaveProfiles())
            {
                foreach (var profile in additions) _profiles.Remove(profile);
                RefreshDashboard();
                return;
            }
            RefreshDashboard();
            MessageBox.Show(this, $"נוספו {additions.Count} פרופילים. דולגו {skipped} כפילויות.", "הייבוא הושלם", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Instance.Error("Profiles", "ImportFailed", ex);
            MessageBox.Show(this, ex.Message, "ייבוא נכשל", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ServersList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        UpdateServerActions();
        RefreshDashboard();
    }

    private void UpdateServerActions()
    {
        var selected = ServersList.SelectedItem is ServerProfile;
        var connected = selected && _sessionManager.TryGet(((ServerProfile)ServersList.SelectedItem).Id, out _);
        ConnectButton.IsEnabled = selected && !connected;
        DisconnectButton.IsEnabled = connected;
        TerminalButton.IsEnabled = connected;
        FilesButton.IsEnabled = connected;
        ManageButton.IsEnabled = connected;
        TestButton.IsEnabled = selected;
        RemoveButton.IsEnabled = selected;
        EditButton.IsEnabled = selected;
    }

    private void EditServer_Click(object sender, RoutedEventArgs e)
    {
        if (ServersList.SelectedItem is not ServerProfile current) return;
        var dialog = new AddServerWindow(current) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Profile is null) return;
        var updated = dialog.Profile;
        if (_profiles.Any(p => p.Id != current.Id
            && string.Equals(p.Host, updated.Host, StringComparison.OrdinalIgnoreCase)
            && p.Port == updated.Port
            && string.Equals(p.Username, updated.Username, StringComparison.Ordinal)))
        {
            MessageBox.Show(this, "שרת עם אותה כתובת, יציאה ומשתמש כבר קיים.", "שרת קיים", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var connectionChanged = !string.Equals(current.Host, updated.Host, StringComparison.OrdinalIgnoreCase)
            || current.Port != updated.Port || !string.Equals(current.Username, updated.Username, StringComparison.Ordinal)
            || current.AuthenticationType != updated.AuthenticationType
            || !string.Equals(current.PrivateKeyPath, updated.PrivateKeyPath, StringComparison.Ordinal);
        if (connectionChanged)
        {
            _sessionManager.Disconnect(current.Id);
            try { _credentialVault.Remove(current.Id); }
            catch (Exception ex) { DiagnosticLogService.Instance.Warning("CredentialVault", "CredentialRemovalFailed", "Could not remove an outdated saved credential.", ex, current.Name); }
        }
        current.Name = updated.Name;
        current.Host = updated.Host;
        current.Port = updated.Port;
        current.Username = updated.Username;
        current.AuthenticationType = updated.AuthenticationType;
        current.PrivateKeyPath = updated.PrivateKeyPath;
        current.KnownHostFingerprint = updated.KnownHostFingerprint;
        DiagnosticLogService.Instance.Info("Profiles", "ServerProfileUpdated", "A server profile was updated.", current.Name);
        SaveProfiles();
        ServersList.Items.Refresh();
        UpdateServerActions();
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
        try { _credentialVault.Remove(selected.Id); }
        catch (Exception ex) { DiagnosticLogService.Instance.Warning("CredentialVault", "CredentialRemovalFailed", "Could not remove the saved credential.", ex, selected.Name); }
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
        var prompt = new SecretPromptWindow(selected, _credentialVault.Get(selected.Id)) { Owner = this };
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
            if (result.Status == SshTestStatus.Success) SaveCredentialChoice(selected, prompt);
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
        var prompt = new SecretPromptWindow(profile, _credentialVault.Get(profile.Id)) { Owner = this };
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
            SaveCredentialChoice(profile, prompt);
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


    private void SaveCredentialChoice(ServerProfile profile, SecretPromptWindow prompt)
    {
        try
        {
            if (prompt.RememberSecret && !string.IsNullOrEmpty(prompt.Secret))
            {
                _credentialVault.Set(profile.Id, prompt.Secret);
                DiagnosticLogService.Instance.Info("CredentialVault", "CredentialSaved", "A credential was saved using Windows DPAPI.", profile.Name);
            }
            else
            {
                _credentialVault.Remove(profile.Id);
                DiagnosticLogService.Instance.Info("CredentialVault", "CredentialNotRetained", "No saved credential is retained for this server.", profile.Name);
            }
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Instance.Error("CredentialVault", "CredentialSaveFailed", "Could not update the encrypted credential vault: " + ex.Message, profile.Name);
            MessageBox.Show(this, "החיבור הצליח, אך לא ניתן היה לעדכן את כספת הסיסמאות המוצפנת. פרטי ההתחברות לא נשמרו.",
                "שמירת פרטי התחברות נכשלה", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
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

    private async void SystemServices_Click(object sender, RoutedEventArgs e)
    {
        var session = await GetSelectedSessionAsync();
        if (session is null) return;
        new SystemServicesWindow(session) { Owner = this }.ShowDialog();
    }

    private void Diagnostics_Click(object sender, RoutedEventArgs e)
    {
        new DiagnosticsWindow { Owner = this }.ShowDialog();
    }

    private bool SaveProfiles()
    {
        try { _profileStore.Save(_profiles); return true; }
        catch (Exception ex)
        {
            DiagnosticLogService.Instance.Error("Profiles", "SaveFailed", ex);
            MessageBox.Show(this, $"לא ניתן לשמור את פרופילי השרתים.\n{ex.Message}", "שגיאת שמירה",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private void RefreshDashboard()
    {
        ServerCountText.Text = _profiles.Count.ToString();
        ServerTotalText.Text = $"{_profiles.Count} שרתים";
        ConnectedCountText.Text = _sessionManager.ConnectedCount.ToString();
        EmptyState.Visibility = _profiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ServerListPanel.Visibility = _profiles.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        UpdateServerActions();
    }

    private void SetBusy(bool isBusy)
    {
        IsEnabled = !isBusy;
        Cursor = isBusy ? System.Windows.Input.Cursors.Wait : System.Windows.Input.Cursors.Arrow;
        if (!isBusy) UpdateServerActions();
    }
}
