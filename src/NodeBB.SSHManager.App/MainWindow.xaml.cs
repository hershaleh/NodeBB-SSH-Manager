using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
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
        var profilesView = CollectionViewSource.GetDefaultView(_profiles);
        profilesView.Filter = FilterServerProfile;
        profilesView.SortDescriptions.Add(new SortDescription(nameof(ServerProfile.IsFavorite), ListSortDirection.Descending));
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
            if (_profiles.Count > 0) ServersList.SelectedItem = _profiles[0];
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
        if (!SaveProfiles()) { _profiles.Remove(dialog.Profile); RefreshDashboard(); return; }
        DiagnosticLogService.Instance.Info("Profiles", "ServerProfileAdded", "A server profile was added.", dialog.Profile.Name);
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

    private void ServersNavigation_Click(object sender, RoutedEventArgs e)
    {
        if (_profiles.Count == 0)
        {
            AddServer_Click(sender, e);
            return;
        }
        ServerListPanel.BringIntoView();
        ServerSearchBox.Focus();
    }

    private void ServersList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (ServersList.SelectedItem is not ServerProfile profile) return;
        _ = OpenServerManagerAsync(profile);
    }

    private async Task OpenServerManagerAsync(ServerProfile profile)
    {
        var session = await EnsureConnectedAsync(profile);
        if (session is null) return;
        await OpenNodeBbManagerAsync(session);
        RefreshDashboard();
    }

    private async Task OpenNodeBbManagerAsync(SshServerSession session)
    {
        try
        {
            // Detect the restricted account before opening any window. This prevents
            // a disabled full-manager window from remaining behind the restricted UI.
            var probe = await session.ExecuteAsync("help");
            var help = probe.Output + Environment.NewLine + probe.Error;
            var restricted = help.Contains("NodeBB Plugin Management CLI (Restricted)", StringComparison.OrdinalIgnoreCase)
                || help.Contains("nodebb-plugins>", StringComparison.OrdinalIgnoreCase);

            Window manager = restricted
                ? new RestrictedNodeBbManagerWindow(session, help)
                : new NodeBbManagerWindow(session);
            manager.Owner = this;
            manager.ShowDialog();
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Instance.Error("NodeBBManagerUI", "InterfaceDetectionFailed", ex, session.Profile.Name);
            MessageBox.Show(this, "לא ניתן לפתוח את ממשק הניהול: " + ex.Message,
                "שגיאה בפתיחת מנהל NodeBB", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ServerRow_Open_Click(object sender, RoutedEventArgs e)
    {
        if (ServersList.SelectedItem is ServerProfile profile)
            _ = OpenServerManagerAsync(profile);
    }

    private void ServerRowMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.ContextMenu is { } menu)
        {
            menu.PlacementTarget = element;
            menu.IsOpen = true;
        }
    }

    private void ServerContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.ContextMenu menu) return;
        if (menu.PlacementTarget is not System.Windows.DependencyObject target) return;
        var container = System.Windows.Controls.ItemsControl.ContainerFromElement(ServersList, target)
            as System.Windows.Controls.ListBoxItem;
        if (container?.DataContext is ServerProfile profile)
        {
            ServersList.SelectedItem = profile;
            if (menu.Items.OfType<System.Windows.Controls.MenuItem>().FirstOrDefault(item => item.Name == "FavoriteMenuItem") is { } favoriteItem)
                favoriteItem.Header = profile.IsFavorite ? "הסר מהמועדפים" : "הוסף למועדפים";
        }
    }

    private async void CheckAllServers_Click(object sender, RoutedEventArgs e)
    {
        if (_profiles.Count == 0) return;
        var button = sender as System.Windows.Controls.Button;
        if (button is not null) { button.IsEnabled = false; button.Content = "בודק שרתים…"; }
        try
        {
            using var throttle = new SemaphoreSlim(4, 4);
            var checks = _profiles.ToArray().Select(async profile =>
            {
                if (_sessionManager.TryGet(profile.Id, out _))
                    return (Profile: profile, Label: "חיבור SSH פעיל");
                var secret = _credentialVault.Get(profile.Id);
                if (secret is null)
                    return (Profile: profile, Label: "נדרשים פרטי התחברות שמורים");
                await throttle.WaitAsync();
                try
                {
                    var result = await _sshService.TestConnectionAsync(profile, secret);
                    var label = result.Status switch
                    {
                        SshTestStatus.Success => "נגיש · SSH תקין",
                        SshTestStatus.HostKeyVerificationRequired => "נדרשת בדיקת טביעת אצבע",
                        _ => "לא נגיש · " + result.Message
                    };
                    return (Profile: profile, Label: label);
                }
                catch (Exception ex)
                {
                    return (Profile: profile, Label: "בדיקה נכשלה · " + SshConnectionService.GetFriendlyErrorMessage(ex));
                }
                finally { throttle.Release(); }
            }).ToArray();

            var results = await Task.WhenAll(checks);
            foreach (var result in results) result.Profile.ConnectionCheckLabel = result.Label;
            ServersList.Items.Refresh();
            var reachable = results.Count(x => x.Label == "נגיש · SSH תקין" || x.Label == "חיבור SSH פעיל");
            var needsCredentials = results.Count(x => x.Label == "נדרשים פרטי התחברות שמורים");
            var failed = results.Length - reachable - needsCredentials;
            MessageBox.Show(this,
                $"הבדיקה הסתיימה.\nנגישים: {reachable}\nלא נגישים או דורשים אימות: {failed}\nללא פרטי התחברות שמורים: {needsCredentials}\n\nלא אושרה אוטומטית אף טביעת אצבע SSH חדשה.",
                "בדיקת שרתים מרוכזת", MessageBoxButton.OK, MessageBoxImage.Information);
            DiagnosticLogService.Instance.Info("SSH", "BulkConnectionCheckCompleted",
                $"Bulk check completed: {reachable} reachable, {failed} failed or awaiting host verification, {needsCredentials} requiring credentials.");
        }
        finally
        {
            if (button is not null) { button.IsEnabled = true; button.Content = "בדוק את כל השרתים"; }
        }
    }

    private void FavoriteButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.DataContext is ServerProfile profile)
            SetFavorite(profile);
    }

    private void ToggleFavorite_Click(object sender, RoutedEventArgs e)
    {
        if (ServersList.SelectedItem is ServerProfile profile)
            SetFavorite(profile);
    }

    private void SetFavorite(ServerProfile profile)
    {
        var previous = profile.IsFavorite;
        profile.IsFavorite = !previous;
        if (!SaveProfiles())
        {
            profile.IsFavorite = previous;
            return;
        }
        CollectionViewSource.GetDefaultView(_profiles).Refresh();
        ServersList.Items.Refresh();
        ServersList.SelectedItem = profile;
        DiagnosticLogService.Instance.Info("Profiles", profile.IsFavorite ? "ServerFavorited" : "ServerUnfavorited",
            profile.IsFavorite ? "Server profile added to favorites." : "Server profile removed from favorites.", profile.Name);
    }

    private void GlobalMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.ContextMenu is { } menu)
        {
            menu.PlacementTarget = element;
            menu.IsOpen = true;
        }
    }

    private void ServersList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        UpdateServerActions();
        RefreshDashboard();
    }

    private bool FilterServerProfile(object item)
    {
        if (item is not ServerProfile profile) return false;
        var query = ServerSearchBox?.Text?.Trim();
        if (string.IsNullOrWhiteSpace(query)) return true;
        return profile.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
            || profile.Host.Contains(query, StringComparison.OrdinalIgnoreCase)
            || profile.Username.Contains(query, StringComparison.OrdinalIgnoreCase)
            || profile.Port.ToString().Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private void ServerSearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        var view = CollectionViewSource.GetDefaultView(_profiles);
        view.Refresh();
        if (ServersList.SelectedItem is ServerProfile selected && !view.Cast<ServerProfile>().Any(p => p.Id == selected.Id))
            ServersList.SelectedItem = null;
        UpdateVisibleServerCount();
    }

    private void UpdateVisibleServerCount()
    {
        if (VisibleServerCountText is null) return;
        var visible = CollectionViewSource.GetDefaultView(_profiles).Cast<object>().Count();
        VisibleServerCountText.Text = visible == _profiles.Count
            ? $"{visible} שרתים מוצגים"
            : $"מוצגים {visible} מתוך {_profiles.Count} שרתים";
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
        ForgetCredentialButton.IsEnabled = selected && _credentialVault.Has(((ServerProfile)ServersList.SelectedItem).Id);
    }

    private void EditServer_Click(object sender, RoutedEventArgs e)
    {
        if (ServersList.SelectedItem is not ServerProfile current) return;
        var dialog = new AddServerWindow(current) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Profile is null) return;
        var updated = dialog.Profile;
        if (_profiles.Any(p => p.Id != current.Id && string.Equals(p.Host, updated.Host, StringComparison.OrdinalIgnoreCase)
            && p.Port == updated.Port && string.Equals(p.Username, updated.Username, StringComparison.Ordinal)))
        {
            MessageBox.Show(this, "שרת עם אותה כתובת, יציאה ומשתמש כבר קיים.", "שרת קיים", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var connectionChanged = !string.Equals(current.Host, updated.Host, StringComparison.OrdinalIgnoreCase)
            || current.Port != updated.Port || !string.Equals(current.Username, updated.Username, StringComparison.Ordinal)
            || current.AuthenticationType != updated.AuthenticationType
            || !string.Equals(current.PrivateKeyPath, updated.PrivateKeyPath, StringComparison.Ordinal);
        var previous = new ServerProfile
        {
            Id = current.Id, Name = current.Name, Host = current.Host, Port = current.Port,
            Username = current.Username, AuthenticationType = current.AuthenticationType,
            PrivateKeyPath = current.PrivateKeyPath, KnownHostFingerprint = current.KnownHostFingerprint
        };
        current.Name = updated.Name; current.Host = updated.Host; current.Port = updated.Port;
        current.Username = updated.Username; current.AuthenticationType = updated.AuthenticationType;
        current.PrivateKeyPath = updated.PrivateKeyPath; current.KnownHostFingerprint = updated.KnownHostFingerprint;
        if (!SaveProfiles())
        {
            current.Name = previous.Name; current.Host = previous.Host; current.Port = previous.Port;
            current.Username = previous.Username; current.AuthenticationType = previous.AuthenticationType;
            current.PrivateKeyPath = previous.PrivateKeyPath; current.KnownHostFingerprint = previous.KnownHostFingerprint;
            ServersList.Items.Refresh(); RefreshDashboard(); return;
        }
        if (connectionChanged)
        {
            _sessionManager.Disconnect(current.Id);
            try { _credentialVault.Remove(current.Id); }
            catch (Exception ex) { DiagnosticLogService.Instance.Warning("CredentialVault", "CredentialRemovalFailed", "Could not remove an outdated saved credential.", ex, current.Name); }
        }
        DiagnosticLogService.Instance.Info("Profiles", "ServerProfileUpdated", "A server profile was updated.", current.Name);
        ServersList.Items.Refresh(); UpdateServerActions(); RefreshDashboard();
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
        _profiles.Remove(selected);
        if (!SaveProfiles())
        {
            _profiles.Add(selected);
            RefreshDashboard();
            return;
        }
        // Only tear down the session and remove its credential after the profile deletion
        // is durably stored. A disk error must not disconnect a server or lose its credential.
        _sessionManager.Disconnect(selected.Id);
        try { _credentialVault.Remove(selected.Id); }
        catch (Exception ex) { DiagnosticLogService.Instance.Warning("CredentialVault", "CredentialRemovalFailed", "Could not remove the saved credential.", ex, selected.Name); }
        DiagnosticLogService.Instance.Info("Profiles", "ServerProfileRemoved", "A server profile was removed.", selected.Name);
        RefreshDashboard();
    }

    private void ForgetCredential_Click(object sender, RoutedEventArgs e)
    {
        if (ServersList.SelectedItem is not ServerProfile selected) return;
        if (!_credentialVault.Has(selected.Id)) { UpdateServerActions(); return; }
        if (MessageBox.Show(this, $"למחוק מהמחשב את פרטי ההתחברות השמורים עבור '{selected.Name}'? החיבור הפעיל, אם קיים, לא ינותק.", "מחיקת פרטי התחברות", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            _credentialVault.Remove(selected.Id);
            DiagnosticLogService.Instance.Info("CredentialVault", "CredentialForgotten", "Saved credential was explicitly removed.", selected.Name);
            RefreshDashboard();
            MessageBox.Show(this, "פרטי ההתחברות השמורים נמחקו מהמחשב.", "המחיקה הושלמה", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Instance.Error("CredentialVault", "CredentialForgetFailed", ex, selected.Name);
            MessageBox.Show(this, "לא ניתן היה למחוק את פרטי ההתחברות השמורים. " + ex.Message, "מחיקה נכשלה", MessageBoxButton.OK, MessageBoxImage.Error);
        }
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
        await OpenNodeBbManagerAsync(session);
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
                profile.HasSavedCredential = true;
                DiagnosticLogService.Instance.Info("CredentialVault", "CredentialSaved", "A credential was saved using Windows DPAPI.", profile.Name);
            }
            else
            {
                _credentialVault.Remove(profile.Id);
                profile.HasSavedCredential = false;
                DiagnosticLogService.Instance.Info("CredentialVault", "CredentialNotRetained", "No saved credential is retained for this server.", profile.Name);
            }
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Instance.Error("CredentialVault", "CredentialSaveFailed", "Could not update the encrypted credential vault: " + ex.Message, profile.Name);
            MessageBox.Show(this, "החיבור הצליח, אך לא ניתן היה לעדכן את כספת הסיסמאות המוצפנת. פרטי ההתחברות לא נשמרו.",
                "שמירת פרטי התחברות נכשלה", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        RefreshDashboard();
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

    private void AllServerHistory_Click(object sender, RoutedEventArgs e)
    {
        new ServerActionHistoryOverviewWindow { Owner = this }.ShowDialog();
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
        foreach (var profile in _profiles)
        {
            profile.HasSavedCredential = _credentialVault.Has(profile.Id);
            profile.IsConnected = _sessionManager.TryGet(profile.Id, out _);
        }
        ServersList.Items.Refresh();
        ServerCountText.Text = _profiles.Count.ToString();
        ServerTotalText.Text = $"{_profiles.Count} שרתים";
        ConnectedCountText.Text = _sessionManager.ConnectedCount.ToString();
        SavedCredentialCountText.Text = _profiles.Count(p => p.HasSavedCredential).ToString();
        if (ServersList.SelectedItem is ServerProfile selected)
        {
            SelectedServerText.Text = selected.Name;
            SelectedServerStatusText.Text = _sessionManager.TryGet(selected.Id, out _)
                ? "מחובר באמצעות SSH"
                : $"{selected.Host}:{selected.Port} · לא מחובר";
        }
        else
        {
            SelectedServerText.Text = "—";
            SelectedServerStatusText.Text = "בחר שרת מהרשימה";
        }
        EmptyState.Visibility = _profiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ServerListPanel.Visibility = _profiles.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        UpdateVisibleServerCount();
        UpdateServerActions();
    }

    private void SetBusy(bool isBusy)
    {
        IsEnabled = !isBusy;
        Cursor = isBusy ? System.Windows.Input.Cursors.Wait : System.Windows.Input.Cursors.Arrow;
        if (!isBusy) UpdateServerActions();
    }
}
