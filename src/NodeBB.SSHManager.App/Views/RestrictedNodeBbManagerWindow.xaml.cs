using System.Windows;
using Microsoft.Win32;
using NodeBB.SSHManager.Services;

namespace NodeBB.SSHManager.Views;

public partial class RestrictedNodeBbManagerWindow : Window
{
    private readonly RestrictedNodeBbCliService _service;
    private bool _busy;

    public RestrictedNodeBbManagerWindow(SshServerSession session, string initialHelp)
    {
        InitializeComponent();
        _service = new RestrictedNodeBbCliService(session);
        ServerLabel.Text = $"{session.Profile.Name} · {session.Profile.Host}:{session.Profile.Port} · nodebb-plugins";
        OutputBox.Text = initialHelp;
    }

    private async void List_Click(object sender, RoutedEventArgs e) => await RunAsync(() => _service.ListAsync(), "רשימת תוספים");
    private async void Backup_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "ליצור גיבוי מלא של הפורום ורשימת התוספים לפני המשך?", "גיבוי", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await RunAsync(() => _service.BackupAsync(), "גיבוי מלא");
    }
    private async void Status_Click(object sender, RoutedEventArgs e) => await RunAsync(() => _service.StatusAsync(), "סטטוס");
    private async void Build_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "להריץ ./nodebb build בשרת?", "בנייה מחדש", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await RunAsync(() => _service.BuildAsync(), "בנייה");
    }
    private async void Restart_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "להפעיל מחדש את שירות NodeBB?", "הפעלה מחדש", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await RunAsync(() => _service.RestartAsync(), "הפעלה מחדש");
    }
    private async void Logs_Click(object sender, RoutedEventArgs e)
    {
        var answer = Microsoft.VisualBasic.Interaction.InputBox("כמה שורות להציג? (1–500)", "לוגים", "80");
        if (!int.TryParse(answer, out var lines) || lines is < 1 or > 500) return;
        await RunAsync(() => _service.LogsAsync(lines), "לוגים");
    }
    private async void Zips_Click(object sender, RoutedEventArgs e) => await RunAsync(() => _service.ListZipsAsync(), "קובצי ZIP");
    private async void Help_Click(object sender, RoutedEventArgs e) => await RunAsync(() => _service.HelpAsync(), "עזרה");

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        var target = TargetBox.Text.Trim();
        try { RestrictedNodeBbCliService.ValidateInstallTarget(target); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "יעד התקנה לא תקין", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        if (MessageBox.Show(this, $"להתקין את היעד הבא?\n\n{target}\n\nהשרת מבצע גיבוי אוטומטי לפני התקנה.", "אישור התקנה", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await RunAsync(() => _service.InstallAsync(target), "התקנה");
    }
    private async void Update_Click(object sender, RoutedEventArgs e) => await PackageActionAsync("עדכון", _service.UpdateAsync);
    private async void Activate_Click(object sender, RoutedEventArgs e) => await PackageActionAsync("הפעלה", _service.ActivateAsync);
    private async void Disable_Click(object sender, RoutedEventArgs e) => await PackageActionAsync("השבתה", _service.DisableAsync);
    private async void Remove_Click(object sender, RoutedEventArgs e) => await PackageActionAsync("הסרה", _service.RemoveAsync);
    private async void DeleteZip_Click(object sender, RoutedEventArgs e)
    {
        var file = TargetBox.Text.Trim();
        try { RestrictedNodeBbCliService.ValidateZipName(file); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "שם קובץ לא תקין", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        if (MessageBox.Show(this, $"למחוק את קובץ ההעלאה {file} מהשרת?", "מחיקת ZIP", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await RunAsync(() => _service.DeleteZipAsync(file), "מחיקת ZIP");
    }

    private async Task PackageActionAsync(string label, Func<string, CancellationToken, Task<(int ExitCode, string Output, string Error)>> action)
    {
        var package = TargetBox.Text.Trim();
        try { RestrictedNodeBbCliService.ValidatePackage(package); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "שם חבילה לא תקין", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        if (MessageBox.Show(this, $"{label} את {package}?", "אישור פעולה", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await RunAsync(() => action(package, CancellationToken.None), label);
    }

    private async Task RunAsync(Func<Task<(int ExitCode, string Output, string Error)>> operation, string label)
    {
        if (_busy) return;
        _busy = true;
        IsEnabled = false;
        OutputBox.Text = $"מבצע: {label}...\r\n";
        try
        {
            var result = await operation();
            var output = string.Join(Environment.NewLine + Environment.NewLine,
                new[] { result.Output.Trim(), result.Error.Trim() }.Where(x => !string.IsNullOrWhiteSpace(x)));
            OutputBox.Text = $"[{label}] {(result.ExitCode == 0 ? "הסתיים בהצלחה" : $"נכשל (קוד {result.ExitCode})")}\r\n\r\n{(string.IsNullOrWhiteSpace(output) ? "השרת לא החזיר פלט." : output)}";
        }
        catch (Exception ex)
        {
            OutputBox.Text = $"[{label}] שגיאה\r\n\r\n{ex.Message}";
            DiagnosticLogService.Instance.Error("RestrictedCLI", "UiActionFailed", ex);
        }
        finally { _busy = false; IsEnabled = true; }
    }
}
