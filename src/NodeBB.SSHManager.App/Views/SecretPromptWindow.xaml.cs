using System.Windows;
using NodeBB.SSHManager.Models;

namespace NodeBB.SSHManager.Views;

public partial class SecretPromptWindow : Window
{
    public string Secret => SecretBox.Password;

    public SecretPromptWindow(ServerProfile profile)
    {
        InitializeComponent();
        PromptTitle.Text = $"התחברות אל {profile.Name}";
        PromptDescription.Text = profile.AuthenticationType == SshAuthenticationType.Password
            ? "הזן את סיסמת משתמש ה-SSH. הסיסמה משמשת לחיבור הנוכחי בלבד ואינה נשמרת בפרופיל."
            : "אם המפתח הפרטי מוגן ב-Passphrase, הזן אותו כאן. השדה יכול להישאר ריק עבור מפתח שאינו מוצפן.";
        Loaded += (_, _) => SecretBox.Focus();
    }

    private void Continue_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }
}
