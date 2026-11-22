using NodeBB.SSHManager.Localization;
using System.Windows;
using NodeBB.SSHManager.Models;

namespace NodeBB.SSHManager.Views;

public partial class SecretPromptWindow : Window
{
    public string Secret => SecretBox.Password;
    public bool RememberSecret => RememberCheckBox.IsChecked == true;

    public SecretPromptWindow(ServerProfile profile, string? savedSecret = null)
    {
        InitializeComponent();
        PromptTitle.Text = LocalizationService.Instance.TranslateFormat($"התחברות אל {profile.Name}");
        PromptDescription.Text = profile.AuthenticationType == SshAuthenticationType.Password
            ? LocalizationService.Instance["SshPrompt_Password"]
            : LocalizationService.Instance["SshPrompt_Passphrase"];
        SecretBox.Password = savedSecret ?? "";
        RememberCheckBox.IsChecked = !string.IsNullOrEmpty(savedSecret);
        Loaded += (_, _) => SecretBox.Focus();
    }

    private void Continue_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}