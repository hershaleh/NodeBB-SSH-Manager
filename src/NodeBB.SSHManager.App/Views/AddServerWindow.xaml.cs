using System.Windows;
using Microsoft.Win32;
using NodeBB.SSHManager.Models;

namespace NodeBB.SSHManager.Views;

public partial class AddServerWindow : Window
{
    public ServerProfile? Profile { get; private set; }

    public AddServerWindow()
    {
        InitializeComponent();
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
            Name = name,
            Host = host,
            Port = port,
            Username = username,
            AuthenticationType = authType,
            PrivateKeyPath = keyPath
        };

        DialogResult = true;
    }
}
