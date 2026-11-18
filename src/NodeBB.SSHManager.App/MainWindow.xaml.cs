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
    private readonly ObservableCollection<ServerProfile> _profiles = [];

    public MainWindow()
    {
        InitializeComponent();
        ServersList.ItemsSource = _profiles;
        LoadProfiles();
    }

    private void LoadProfiles()
    {
        try
        {
            foreach (var profile in _profileStore.Load())
                _profiles.Add(profile);

            RefreshDashboard();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "שגיאה בטעינת שרתים",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void AddServer_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new AddServerWindow { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Profile is null)
            return;

        if (_profiles.Any(p => string.Equals(p.Host, dialog.Profile.Host, StringComparison.OrdinalIgnoreCase)
                               && p.Port == dialog.Profile.Port
                               && string.Equals(p.Username, dialog.Profile.Username, StringComparison.Ordinal)))
        {
            MessageBox.Show(this, "שרת עם אותה כתובת, יציאה ומשתמש כבר קיים.", "שרת קיים",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _profiles.Add(dialog.Profile);
        SaveProfiles();
        ServersList.SelectedItem = dialog.Profile;
        RefreshDashboard();
    }

    private void RemoveServer_Click(object sender, RoutedEventArgs e)
    {
        if (ServersList.SelectedItem is not ServerProfile selected)
        {
            MessageBox.Show(this, "בחר שרת להסרה.", "לא נבחר שרת",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var answer = MessageBox.Show(this,
            $"להסיר את הפרופיל '{selected.Name}' מהמחשב? הפעולה אינה מוחקת דבר מהשרת עצמו.",
            "אישור הסרת פרופיל", MessageBoxButton.YesNo, MessageBoxImage.Warning);

        if (answer != MessageBoxResult.Yes)
            return;

        _profiles.Remove(selected);
        SaveProfiles();
        RefreshDashboard();
    }

    private async void TestConnection_Click(object sender, RoutedEventArgs e)
    {
        if (ServersList.SelectedItem is not ServerProfile selected)
        {
            MessageBox.Show(this, "בחר שרת לבדיקה.", "לא נבחר שרת",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var prompt = new SecretPromptWindow(selected) { Owner = this };
        if (prompt.ShowDialog() != true)
            return;

        SetBusy(true);
        try
        {
            var result = await _sshService.TestConnectionAsync(selected, prompt.Secret);

            if (result.Status == SshTestStatus.HostKeyVerificationRequired
                && !string.IsNullOrWhiteSpace(result.Fingerprint))
            {
                var changed = !string.IsNullOrWhiteSpace(selected.KnownHostFingerprint);
                var title = changed ? "אזהרת שינוי מפתח שרת" : "אישור שרת SSH חדש";
                var warning = changed
                    ? "טביעת האצבע של השרת השתנתה. ייתכן שהשרת הותקן מחדש, אך שינוי כזה עלול גם להעיד על התחזות. אמת את הטביעה מול מנהל השרת בערוץ נפרד לפני האישור."
                    : "זו הפעם הראשונה שמתחברים לשרת זה. מומלץ לאמת את טביעת האצבע מול ספק השרת או במסוף השרת לפני האישור.";

                var answer = MessageBox.Show(this,
                    $"{warning}\n\n{selected.Host}:{selected.Port}\n\n{result.Fingerprint}\n\nלאשר ולשמור טביעת אצבע זו?",
                    title, MessageBoxButton.YesNo, MessageBoxImage.Warning);

                if (answer != MessageBoxResult.Yes)
                    return;

                selected.KnownHostFingerprint = result.Fingerprint;
                SaveProfiles();
                result = await _sshService.TestConnectionAsync(selected, prompt.Secret);
            }

            if (result.Status == SshTestStatus.Success)
            {
                MessageBox.Show(this,
                    $"{result.Message}\n\nמידע מהשרת:\n{result.RemoteSummary}\n\nטביעת אצבע:\n{result.Fingerprint}",
                    "בדיקת SSH הצליחה", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show(this, result.Message, "בדיקת SSH נכשלה",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "שגיאת חיבור",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SaveProfiles()
    {
        try
        {
            _profileStore.Save(_profiles);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"לא ניתן לשמור את פרופילי השרתים.\n{ex.Message}",
                "שגיאת שמירה", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RefreshDashboard()
    {
        ServerCountText.Text = _profiles.Count.ToString();
        ServerTotalText.Text = $"{_profiles.Count} שרתים";
        EmptyState.Visibility = _profiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ServerListPanel.Visibility = _profiles.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SetBusy(bool isBusy)
    {
        IsEnabled = !isBusy;
        Cursor = isBusy ? System.Windows.Input.Cursors.Wait : System.Windows.Input.Cursors.Arrow;
    }
}
