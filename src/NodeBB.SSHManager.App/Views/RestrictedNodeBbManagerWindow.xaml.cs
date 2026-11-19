using System.Windows;
using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using NodeBB.SSHManager.Services;

namespace NodeBB.SSHManager.Views;

public partial class RestrictedNodeBbManagerWindow : Window
{
    private readonly RestrictedNodeBbCliService _service;
    private bool _busy;
    private string _lastOutput = "";
    private readonly ObservableCollection<PluginCard> _allPlugins = [];

    public RestrictedNodeBbManagerWindow(SshServerSession session, string initialHelp)
    {
        InitializeComponent();
        _service = new RestrictedNodeBbCliService(session);
        ServerLabel.Text = $"{session.Profile.Name} · {session.Profile.Host}:{session.Profile.Port} · nodebb-plugins";
        TerminalOutputBox.Text = initialHelp;
        PluginCards.ItemsSource = _allPlugins;
        SummaryTitle.Text = "החיבור לשרת מוכן";
        SummaryText.Text = "אפשר לנהל תוספים, ליצור גיבוי, לבדוק את מצב הפורום ולהפעיל פעולות תחזוקה. המידע יוצג כאן בעברית.";
        UploadZipButton.Visibility = InstallSourceBox.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void List_Click(object sender, RoutedEventArgs e)
    {
        ManagerTabs.SelectedIndex = 1;
        await RunAsync(() => _service.ListAsync(), "רשימת תוספים");
    }
    private async void Backup_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "ליצור גיבוי מלא של הפורום ורשימת התוספים לפני המשך?", "גיבוי", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await RunAsync(() => _service.BackupAsync(), "גיבוי מלא");
    }
    private async void Status_Click(object sender, RoutedEventArgs e) => await RunAsync(() => _service.StatusAsync(), "בדיקת מצב הפורום");
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
    private async void Help_Click(object sender, RoutedEventArgs e) { await RunAsync(() => _service.HelpAsync(), "הצגת עזרה"); SummaryTitle.Text = "העזרה הטכנית זמינה בטרמינל"; SummaryText.Text = "רשימת הפקודות והתחביר המקורי הועברו לחלון הטרמינל. ממשק הניהול עצמו נשאר בעברית."; }

    private void InstallSourceBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (UploadZipButton is not null)
            UploadZipButton.Visibility = InstallSourceBox.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void UploadZip_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "בחירת תוסף ZIP מהמחשב",
            Filter = "קובצי ZIP (*.zip)|*.zip",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true) return;
        var info = new FileInfo(dialog.FileName);
        if (info.Length > 25L * 1024 * 1024)
        {
            MessageBox.Show(this, "גודל קובץ ZIP מוגבל ל־25MB.", "הקובץ גדול מדי",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (MessageBox.Show(this, $"להעלות לשרת את הקובץ {info.Name} ({info.Length / 1024.0 / 1024.0:F1}MB)?",
                "אישור העלאת ZIP", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        await RunAsync(() => _service.UploadZipAsync(dialog.FileName), "העלאת ZIP");
        if (_lastOutput.Contains("הסתיים בהצלחה", StringComparison.Ordinal))
        {
            TargetBox.Text = info.Name;
            InstallSourceBox.SelectedIndex = 2;
            ManagerTabs.SelectedIndex = 1;
            MessageBox.Show(this, $"הקובץ {info.Name} הועלה לשרת בהצלחה. כעת לחץ על 'התקן' כדי להתקין אותו.",
                "העלאת ZIP הושלמה", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        var target = TargetBox.Text.Trim();
        var source = (InstallSourceBox.SelectedIndex);
        try { if (source == 0) RestrictedNodeBbCliService.ValidatePackage(target); else if (source == 1) RestrictedNodeBbCliService.ValidateGitHubTarget(target); else RestrictedNodeBbCliService.ValidateZipName(target); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "יעד התקנה לא תקין", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        if (MessageBox.Show(this, $"להתקין את היעד הבא?\n\n{target}\n\nהשרת מבצע גיבוי אוטומטי לפני התקנה.", "אישור התקנה", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await RunAsync(() => source switch { 0 => _service.InstallNpmAsync(target), 1 => _service.InstallGitHubAsync(target), _ => _service.InstallZipAsync(target) }, "התקנה");
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
        var keepPluginView = label is "השבתה" or "הסרה" or "עדכון" or "הפעלה" or "רשימת תוספים";
        if (!keepPluginView) ManagerTabs.SelectedIndex = 2;
        _busy = true; IsEnabled = false;
        SummaryTitle.Text = label + " — בביצוע";
        SummaryText.Text = "הבקשה נשלחה לשרת. ממתין לתשובה...";
        if (PluginActionStatus is not null && keepPluginView)
            PluginActionStatus.Text = label + " מתבצע…";
        try
        {
            var result = await operation();
            var raw = string.Join(Environment.NewLine + Environment.NewLine, new[] { result.Output.Trim(), result.Error.Trim() }.Where(v => !string.IsNullOrWhiteSpace(v)));
            _lastOutput = $"[{label}] {(result.ExitCode == 0 ? "הסתיים בהצלחה" : $"נכשל (קוד {result.ExitCode})")}\r\n\r\n{(string.IsNullOrWhiteSpace(raw) ? "השרת לא החזיר פלט." : raw)}";
            TerminalOutputBox.Text = _lastOutput;
            ShowHebrewSummary(label, result.ExitCode, raw);
            if (label == "רשימת תוספים") UpdatePluginCards(raw);
            if (PluginActionStatus is not null && keepPluginView)
                PluginActionStatus.Text = result.ExitCode == 0
                    ? "✓ " + label + " הושלם. " + (label == "רשימת תוספים" ? "הרשימה עודכנה." : "אפשר להמשיך לנהל תוספים.")
                    : "⚠ " + label + " לא הושלם. " + TranslateError(raw);
            if (keepPluginView) ManagerTabs.SelectedIndex = 1;
        }
        catch (Exception ex)
        {
            _lastOutput = $"[{label}] שגיאה\r\n\r\n{ex}";
            TerminalOutputBox.Text = _lastOutput;
            SummaryTitle.Text = label + " — הפעולה לא הושלמה";
            SummaryText.Text = TranslateError(ex.Message);
            DiagnosticLogService.Instance.Error("RestrictedCLI", "UiActionFailed", ex);
        }
        finally
        {
            _busy = false;
            IsEnabled = true;
            if (label is "השבתה" or "הסרה")
            {
                ManagerTabs.SelectedIndex = 1;
                try
                {
                    var refreshed = await _service.ListAsync();
                    if (refreshed.ExitCode == 0)
                    {
                        var refreshedRaw = string.Join(Environment.NewLine,
                            new[] { refreshed.Output, refreshed.Error }.Where(v => !string.IsNullOrWhiteSpace(v)));
                        _lastOutput = "[רענון רשימת תוספים]" + Environment.NewLine + refreshedRaw.Trim();
                        TerminalOutputBox.Text = _lastOutput;
                        UpdatePluginCards(refreshedRaw);
                    }
                }
                catch (Exception refreshError)
                {
                    if (PluginActionStatus is not null)
                        PluginActionStatus.Text += " רענון הרשימה נכשל; אפשר לנסות שוב.";
                    DiagnosticLogService.Instance.Warning("RestrictedCLI", "PluginRefreshAfterActionFailed",
                        "Could not refresh plugin list after an action.", refreshError, "restricted server");
                }
            }
        }
    }

    private void ShowHebrewSummary(string label, int exitCode, string raw)
    {
        if (exitCode != 0) { SummaryTitle.Text = label + " — הפעולה נכשלה"; SummaryText.Text = TranslateError(raw); return; }
        SummaryTitle.Text = label + " — הושלם בהצלחה";
        SummaryText.Text = label switch
        {
            "רשימת תוספים" => "הרשימה עודכנה. התוספים שהשרת דיווח עליהם כמותקנים ופעילים מוצגים בכרטיסים.",
            "בדיקת מצב הפורום" => SummarizeStatus(raw),
            "גיבוי מלא" => "השרת אישר את פעולת הגיבוי. פרטי המיקום והקבצים המקוריים זמינים בטרמינל.",
            "בנייה" => "בניית קובצי NodeBB הסתיימה בהצלחה.",
            "הפעלה מחדש" => "בקשת ההפעלה מחדש הסתיימה בהצלחה. מומלץ לבדוק כעת את מצב הפורום.",
            "לוגים" => "יומני המערכת התקבלו. להצגת השורות המקוריות פתח את הטרמינל.",
            "קובצי ZIP" => "רשימת קובצי ZIP בשרת התקבלה. שמות הקבצים המלאים מופיעים בטרמינל.",
            "העלאת ZIP" => "קובץ ה־ZIP הועבר לשרת בהצלחה.",
            "התקנה" => "התקנת החבילה הסתיימה. מומלץ לבדוק את מצב הפורום ולבנות מחדש לפי הצורך.",
            "עדכון" => "עדכון החבילה הסתיים. מומלץ לבדוק תאימות ולבנות מחדש לפי הצורך.",
            "הפעלה" => "השרת אישר את הפעלת התוסף.",
            "השבתה" => "השרת אישר את השבתת התוסף.",
            "הסרה" => "השרת אישר את הסרת החבילה.",
            "מחיקת ZIP" => "קובץ ההעלאה נמחק מהשרת.",
            _ => "השרת אישר את הפעולה. לפרטים טכניים נוספים פתח את הטרמינל."
        };
    }

    private static string SummarizeStatus(string raw)
    {
        var lower = raw.ToLowerInvariant();
        if (lower.Contains("active: active") || lower.Contains("active (running)") || lower.Contains("running"))
            return "לפי תשובת השרת, שירות NodeBB פועל כרגע.";
        if (lower.Contains("inactive") || lower.Contains("stopped") || lower.Contains("not running"))
            return "לפי תשובת השרת, שירות NodeBB אינו פועל כרגע.";
        return "בדיקת המצב הושלמה. לא ניתן לקבוע מהתשובה באופן חד־משמעי אם השירות פעיל; ניתן לעיין בפלט המקורי בטרמינל.";
    }

    private static string TranslateError(string raw)
    {
        var lower = raw.ToLowerInvariant();
        if (lower.Contains("permission denied") || lower.Contains("not permitted")) return "לחשבון השרת אין הרשאה לבצע את הפעולה המבוקשת.";
        if (lower.Contains("not found") || lower.Contains("no such file")) return "הפריט המבוקש לא נמצא בשרת.";
        if (lower.Contains("timed out") || lower.Contains("timeout")) return "השרת לא השיב בזמן. בדוק את החיבור ונסה שוב.";
        if (lower.Contains("connection") && lower.Contains("closed")) return "החיבור לשרת נסגר במהלך הפעולה. יש להתחבר מחדש ולנסות שוב.";
        if (lower.Contains("incompatible") || lower.Contains("compatibility")) return "השרת דיווח על בעיית תאימות. בדוק את גרסת NodeBB והתוסף.";
        if (lower.Contains("already exists")) return "הפריט כבר קיים בשרת.";
        if (lower.Contains("invalid") || lower.Contains("malformed")) return "השרת דחה את הבקשה מפני שהנתונים או הקובץ אינם תקינים.";
        return "השרת לא הצליח להשלים את הפעולה. פתח את הטרמינל כדי לראות את הודעת השגיאה המקורית.";
    }

    private void UpdatePluginCards(string raw)
    {
        var found = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in raw.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            foreach (Match match in Regex.Matches(line, @"(?<![A-Za-z0-9@._/-])((?:@[A-Za-z0-9._-]+/)?nodebb-(?:plugin|theme)-[A-Za-z0-9._-]+)", RegexOptions.IgnoreCase))
                found.Add(match.Groups[1].Value.TrimEnd('.', ',', ':', ';'));
        _allPlugins.Clear();
        foreach (var name in found)
            _allPlugins.Add(new PluginCard(name, name.Contains("theme", StringComparison.OrdinalIgnoreCase) ? "ערכת עיצוב" : "תוסף"));
        ApplyPluginFilter();
        PluginCountText.Text = found.Count == 0 ? "לא זוהו שמות חבילות בתשובת השרת" : $"נמצאו {found.Count} פריטים שהשרת דיווח עליהם כמותקנים ופעילים";
    }

    private void PluginSearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => ApplyPluginFilter();

    private void ApplyPluginFilter()
    {
        if (PluginCards is null || PluginSearchBox is null) return;
        var query = PluginSearchBox.Text.Trim();
        var filtered = _allPlugins.Where(p => p.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        PluginCards.ItemsSource = filtered;
        PluginEmptyState.Visibility = filtered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        PluginCards.Visibility = filtered.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        PluginEmptyTitle.Text = _allPlugins.Count == 0 ? "עדיין לא נטענה רשימת התוספים" : "לא נמצאו תוצאות לחיפוש";
        PluginEmptyDescription.Text = _allPlugins.Count == 0 ? "לחץ על «רענון מהשרת» כדי להציג את התוספים." : "נסה מונח חיפוש אחר.";
    }

    private string SelectedPlugin(object sender)
    {
        if (sender is System.Windows.Controls.Button button && button.Tag is string name) { TargetBox.Text = name; return name; }
        return TargetBox.Text.Trim();
    }

    private async void PluginUpdate_Click(object sender, RoutedEventArgs e) => await RunPluginCardActionAsync(sender, "עדכון", _service.UpdateAsync);
    private async void PluginDisable_Click(object sender, RoutedEventArgs e) => await RunPluginCardActionAsync(sender, "השבתה", _service.DisableAsync);
    private async void PluginRemove_Click(object sender, RoutedEventArgs e) => await RunPluginCardActionAsync(sender, "הסרה", _service.RemoveAsync);

    private async Task RunPluginCardActionAsync(object sender, string label, Func<string, CancellationToken, Task<(int ExitCode, string Output, string Error)>> action)
    {
        var package = SelectedPlugin(sender);
        if (string.IsNullOrWhiteSpace(package)) return;
        if (MessageBox.Show(this, $"האם לבצע {label} עבור {package}? השרת מפעיל גיבוי אוטומטי לפני שינוי.", "אישור פעולה", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await RunAsync(() => action(package, CancellationToken.None), label);
        ManagerTabs.SelectedIndex = 1;
    }

    private void ToggleTerminal_Click(object sender, RoutedEventArgs e)
    {
        TerminalPanel.Visibility = TerminalPanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed : Visibility.Visible;
        if (TerminalPanel.Visibility == Visibility.Visible)
            TerminalOutputBox.Text = string.IsNullOrWhiteSpace(_lastOutput) ? "עדיין אין פלט טכני להצגה." : _lastOutput;
    }

    private void ManagerTabs_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        // Tab selection is intentionally local; command execution remains explicit.
    }
}


public sealed record PluginCard(string Name, string KindLabel);
