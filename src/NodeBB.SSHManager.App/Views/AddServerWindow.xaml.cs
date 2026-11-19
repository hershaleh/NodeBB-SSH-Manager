using System.Windows;
using Microsoft.Win32;
using NodeBB.SSHManager.Models;

namespace NodeBB.SSHManager.Views;

public partial class AddServerWindow : Window
{
    public ServerProfile? Profile { get; private set; }
    private readonly ServerProfile? _original;

    public AddServerWindow()
    {
        InitializeComponent();
    }

    public AddServerWindow(ServerProfile profile) : this()
    {
        _original = profile;
        Title = "עריכת שרת";
        DialogTitle.Text = "עריכת פרופיל שרת";
        NameBox.Text = profile.Name;
        GroupBox.Text = profile.Group;
        HostBox.Text = profile.Host;
        PortBox.Text = profile.Port.ToString();
        UsernameBox.Text = profile.Username;
        AuthTypeBox.SelectedIndex = profile.AuthenticationType == SshAuthenticationType.Password ? 1 : 0;
        KeyPathBox.Text = profile.PrivateKeyPath;
    }

    private void BrowseKey_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "בחירת מפתח SSH פרטי",
            Filter = "All files (*.*)|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) == true)
            KeyPathBox.Text = dialog.FileName;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        var host = HostBox.Text.Trim();
        var username = UsernameBox.Text.Trim();
        var group = GroupBox.Text.Trim();

        if (group.Length > 80 || group.Any(char.IsControl))
        {
            MessageBox.Show(this, "שם הקבוצה יכול להכיל עד 80 תווים ואינו יכול להכיל תווי בקרה.", "שם קבוצה לא תקין", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(name)
            || string.IsNullOrWhiteSpace(host)
            || string.IsNullOrWhiteSpace(username)
            || !int.TryParse(PortBox.Text, out var port)
            || port is < 1 or > 65535)
        {
            MessageBox.Show(this, "יש למלא שם, כתובת, משתמש ופורט תקין.", "נתונים חסרים",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var authItem = AuthTypeBox.SelectedItem as System.Windows.Controls.ComboBoxItem;
        var authType = authItem?.Tag?.ToString() == "Password"
            ? SshAuthenticationType.Password
            : SshAuthenticationType.PrivateKey;
        var keyPath = KeyPathBox.Text.Trim();

        if (authType == SshAuthenticationType.PrivateKey
            && (string.IsNullOrWhiteSpace(keyPath) || !File.Exists(keyPath)))
        {
            MessageBox.Show(this, "בחר קובץ מפתח SSH פרטי קיים.", "מפתח SSH חסר",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Profile = new ServerProfile
        {
            Id = _original?.Id ?? Guid.NewGuid(),
            KnownHostFingerprint = _original is not null
                && string.Equals(_original.Host, host, StringComparison.OrdinalIgnoreCase)
                && _original.Port == port
                    ? _original.KnownHostFingerprint : null,
            Name = name,
            Group = group,
            Host = host,
            Port = port,
            Username = username,
            AuthenticationType = authType,
            PrivateKeyPath = keyPath
        };

        DialogResult = true;
    }
}
