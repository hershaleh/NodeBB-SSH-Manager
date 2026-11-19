using System.Windows;
using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using NodeBB.SSHManager.Services;

namespace NodeBB.SSHManager.Views;

public partial class RestrictedNodeBbManagerWindow : Window
{
    private readonly RestrictedNodeBbCliService _service;
    private readonly SshServerSession _session;
    private bool _busy;
    private string _lastOutput = "";
    private readonly ObservableCollection<PluginCard> _allPlugins = [];
    private IReadOnlyList<ServerActionHistoryEntry> _historyEntries = [];

    public RestrictedNodeBbManagerWindow(SshServerSession session, string initialHelp)
    {
        InitializeComponent();
        _session = session;
        _service = new RestrictedNodeBbCliService(session);
        ServerLabel.Text = $"{session.Profile.Name} · {session.Profile.Host}:{session.Profile.Port} · nodebb-plugins";
        TerminalOutputBox.Text = initialHelp;
        PluginCards.ItemsSource = _allPlugins;
        SummaryTitle.Text = "החיבור לשרת מוכן";
        SummaryText.Text = "אפשר לנהל תוספים, ליצור גיבוי, לבדוק את מצב הפורום ולהפעיל פעולות תחזוקה. המידע יוצג כאן בעברית.";
        UploadZipButton.Visibility = InstallSourceBox.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
        RefreshActionHistory();
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

    private async Task RunAsync(Func<Task<(int ExitCode, string Output, string Error)>> operation, string label, string? historyTarget = null)
    {
        if (_busy) return;
        var keepPluginView = label is "רשימת תוספים" or "התקנה" or "העלאת ZIP" or "עדכון"
            or "הפעלה" or "השבתה" or "הסרה" or "מחיקת ZIP"
            or "עדכון מרובה" or "השבתה מרובה" or "הסרה מרובה";
        var refreshPlugins = label is "התקנה" or "עדכון" or "הפעלה" or "השבתה" or "הסרה"
            or "עדכון מרובה" or "השבתה מרובה" or "הסרה מרובה";
        if (!keepPluginView) ManagerTabs.SelectedIndex = 2;
        _busy = true;
        IsEnabled = false;
        SummaryTitle.Text = label + " — בביצוע";
        SummaryText.Text = "הבקשה נשלחה לשרת. ממתין לתשובה...";
        if (keepPluginView) PluginActionStatus.Text = label + " מתבצע…";
        try
        {
            var result = await operation();
            var raw = string.Join(Environment.NewLine + Environment.NewLine,
                new[] { result.Output.Trim(), result.Error.Trim() }
                    .Where(value => !string.IsNullOrWhiteSpace(value)));
            _lastOutput = $"[{label}] {(result.ExitCode == 0 ? "הסתיים בהצלחה" : $"נכשל (קוד {result.ExitCode})")}\r\n\r\n"
                + (string.IsNullOrWhiteSpace(raw) ? "השרת לא החזיר פלט." : raw);
            TerminalOutputBox.Text = _lastOutput;
            ShowHebrewSummary(label, result.ExitCode, raw);
            SaveHistory(label, historyTarget ?? ResolveHistoryTarget(label), result.ExitCode == 0, SummaryText.Text);
            if (label == "רשימת תוספים") UpdatePluginCards(raw);
            if (keepPluginView)
            {
                PluginActionStatus.Text = result.ExitCode == 0
                    ? "✓ " + label + " הושלם."
                    : "⚠ " + label + " לא הושלם. " + TranslateError(raw);
                ManagerTabs.SelectedIndex = 1;
            }
            // Refresh cards after successful mutations, while preserving the original action output.
            if (result.ExitCode == 0 && refreshPlugins)
            {
                try
                {
                    var refreshed = await _service.ListAsync();
                    if (refreshed.ExitCode == 0)
                    {
                        var refreshedRaw = string.Join(Environment.NewLine,
                            new[] { refreshed.Output, refreshed.Error }
                                .Where(value => !string.IsNullOrWhiteSpace(value)));
                        UpdatePluginCards(refreshedRaw);
                        PluginActionStatus.Text += " רשימת התוספים עודכנה.";
                    }
                    else PluginActionStatus.Text += " הפעולה הושלמה, אך רענון הרשימה נכשל; אפשר לרענן ידנית.";
                }
                catch (Exception refreshError)
                {
                    PluginActionStatus.Text += " הפעולה הושלמה, אך רענון הרשימה נכשל; אפשר לרענן ידנית.";
                    DiagnosticLogService.Instance.Warning("RestrictedCLI", "PluginRefreshAfterActionFailed",
                        "Could not refresh plugin list after a successful action.", refreshError, "restricted server");
                }
            }
        }
        catch (Exception ex)
        {
            _lastOutput = $"[{label}] שגיאה\r\n\r\n{ex}";
            TerminalOutputBox.Text = _lastOutput;
            SummaryTitle.Text = label + " — הפעולה לא הושלמה";
            SummaryText.Text = TranslateError(ex.Message);
            SaveHistory(label, historyTarget ?? ResolveHistoryTarget(label), false, SummaryText.Text);
            if (keepPluginView)
            {
                PluginActionStatus.Text = "⚠ " + label + " לא הושלם. " + TranslateError(ex.Message);
                ManagerTabs.SelectedIndex = 1;
            }
            DiagnosticLogService.Instance.Error("RestrictedCLI", "UiActionFailed", ex);
        }
        finally
        {
            _busy = false;
            IsEnabled = true;
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
        // Check negative states first: "not running" also contains "running".
        if (lower.Contains("not running") || lower.Contains("inactive") || lower.Contains("stopped")
            || lower.Contains("failed") || lower.Contains("dead"))
            return "לפי תשובת השרת, שירות NodeBB אינו פועל כרגע.";
        if (lower.Contains("active: active") || lower.Contains("active (running)") || lower.Contains("running"))
            return "לפי תשובת השרת, שירות NodeBB פועל כרגע.";
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
        var found = RestrictedNodeBbCliService.ExtractPluginPackageNames(raw);
        _allPlugins.Clear();
        foreach (var name in found)
            _allPlugins.Add(new PluginCard(name, RestrictedNodeBbCliService.GetPackageKindLabel(name)));
        ApplyPluginFilter();
        PluginCountText.Text = found.Count == 0 ? "לא זוהו שמות חבילות בתשובת השרת" : $"נמצאו {found.Count} פריטים שהשרת דיווח עליהם כמותקנים ופעילים";
    }

    private void PluginSearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => ApplyPluginFilter();

    private void ApplyPluginFilter()
    {
        if (PluginCards is null || PluginSearchBox is null) return;
        var query = PluginSearchBox.Text.Trim();
        var kind = PluginKindFilter?.SelectedIndex ?? 0;
        var filtered = _allPlugins.Where(p =>
            p.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
            && (kind == 0 || (kind == 1 && p.KindLabel == "תוסף") || (kind == 2 && p.KindLabel == "ערכת עיצוב"))).ToList();
        PluginCards.ItemsSource = filtered;
        UpdatePluginSelectionCount();
        PluginEmptyState.Visibility = filtered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        PluginCards.Visibility = filtered.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        PluginEmptyTitle.Text = _allPlugins.Count == 0 ? "עדיין לא נטענה רשימת התוספים" : "לא נמצאו תוצאות לחיפוש";
        PluginEmptyDescription.Text = _allPlugins.Count == 0 ? "לחץ על «רענון מהשרת» כדי להציג את התוספים." : "נסה מונח חיפוש אחר.";
    }

    private void PluginKindFilter_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (PluginCards is not null) ApplyPluginFilter();
    }

    private void CopyPluginList_Click(object sender, RoutedEventArgs e)
    {
        var names = _allPlugins.Select(plugin => plugin.Name).OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();
        if (names.Length == 0)
        {
            PluginActionStatus.Text = "אין רשימת תוספים להעתקה. רענן תחילה מהשרת.";
            return;
        }
        try
        {
            Clipboard.SetText(string.Join(Environment.NewLine, names));
            PluginActionStatus.Text = $"הועתקו {names.Length} שמות חבילות ללוח.";
        }
        catch (Exception ex)
        {
            PluginActionStatus.Text = "לא ניתן להעתיק את רשימת התוספים.";
            DiagnosticLogService.Instance.Warning("RestrictedCLI", "CopyPluginListFailed",
                "Could not copy plugin package list to clipboard.", ex, ServerLabel.Text);
        }
    }

    private void ExportPluginList_Click(object sender, RoutedEventArgs e)
    {
        var names = _allPlugins.Select(plugin => plugin.Name).OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();
        if (names.Length == 0)
        {
            PluginActionStatus.Text = "אין רשימת תוספים לייצוא. רענן תחילה מהשרת.";
            return;
        }
        var dialog = new SaveFileDialog
        {
            Title = "ייצוא רשימת תוספי NodeBB",
            Filter = "קובץ טקסט (*.txt)|*.txt",
            FileName = "nodebb-packages.txt",
            AddExtension = true,
            DefaultExt = ".txt"
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            File.WriteAllText(dialog.FileName, string.Join(Environment.NewLine, names), new System.Text.UTF8Encoding(true));
            PluginActionStatus.Text = $"הרשימה יוצאה בהצלחה ({names.Length} פריטים).";
        }
        catch (Exception ex)
        {
            PluginActionStatus.Text = "ייצוא הרשימה נכשל. בדוק הרשאות כתיבה ליעד.";
            DiagnosticLogService.Instance.Warning("RestrictedCLI", "ExportPluginListFailed",
                "Could not export plugin package list.", ex, ServerLabel.Text);
        }
    }

    private void UpdatePluginSelectionCount()
    {
        var count = _allPlugins.Count(plugin => plugin.IsSelected);
        PluginSelectionCount.Text = count == 0 ? "לא נבחרו פריטים" : $"נבחרו {count} פריטים";
    }

    private void SelectVisiblePlugins_Click(object sender, RoutedEventArgs e)
    {
        var visible = PluginCards.ItemsSource?.Cast<PluginCard>().ToArray() ?? [];
        foreach (var plugin in visible) plugin.IsSelected = true;
        PluginCards.Items.Refresh();
        UpdatePluginSelectionCount();
    }

    private void ClearPluginSelection_Click(object sender, RoutedEventArgs e)
    {
        foreach (var plugin in _allPlugins) plugin.IsSelected = false;
        PluginCards.Items.Refresh();
        UpdatePluginSelectionCount();
    }

    private async void BulkUpdate_Click(object sender, RoutedEventArgs e) =>
        await RunBulkPluginActionAsync("עדכון מרובה", _service.UpdateAsync);

    private async void BulkDisable_Click(object sender, RoutedEventArgs e) =>
        await RunBulkPluginActionAsync("השבתה מרובה", _service.DisableAsync);

    private async void BulkRemove_Click(object sender, RoutedEventArgs e) =>
        await RunBulkPluginActionAsync("הסרה מרובה", _service.RemoveAsync);

    private async Task RunBulkPluginActionAsync(
        string label,
        Func<string, CancellationToken, Task<(int ExitCode, string Output, string Error)>> action)
    {
        var selected = _allPlugins.Where(plugin => plugin.IsSelected)
            .Select(plugin => plugin.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (selected.Length == 0)
        {
            PluginActionStatus.Text = "בחר לפחות פריט אחד בכרטיסי התוספים.";
            return;
        }

        var destructive = label == "הסרה מרובה";
        var warning = destructive
            ? "הפעולה תסיר את כל החבילות שנבחרו. השרת מבצע גיבוי אוטומטי לפני כל שינוי. לא ניתן לבטל הסרה שכבר הושלמה."
            : "הפעולה תתבצע על כל החבילות שנבחרו, אחת אחרי השנייה. השרת מבצע גיבוי אוטומטי לפני כל שינוי.";
        if (MessageBox.Show(this,
            $"{label} עבור {selected.Length} פריטים?\n\n{string.Join(Environment.NewLine, selected)}\n\n{warning}",
            "אישור פעולה מרובה", MessageBoxButton.YesNo,
            destructive ? MessageBoxImage.Warning : MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        await RunAsync(async () =>
        {
            var outputs = new List<string>();
            var failures = 0;
            foreach (var package in selected)
            {
                var result = await action(package, CancellationToken.None);
                var details = string.Join(Environment.NewLine,
                    new[] { result.Output.Trim(), result.Error.Trim() }
                        .Where(value => !string.IsNullOrWhiteSpace(value)));
                outputs.Add($"[{(result.ExitCode == 0 ? "הצלחה" : "כישלון")}] {package}" +
                    (string.IsNullOrWhiteSpace(details) ? string.Empty : Environment.NewLine + details));
                if (result.ExitCode != 0) failures++;
            }
            var summary = $"הפעולה הסתיימה עבור {selected.Length} פריטים. הצלחות: {selected.Length - failures}; כישלונות: {failures}.";
            return (failures == 0 ? 0 : 1, summary, string.Join(Environment.NewLine + Environment.NewLine, outputs));
        }, label, string.Join(", ", selected));
    }

    private string SelectedPlugin(object sender)
    {
        if (sender is System.Windows.Controls.Button button && button.Tag is string name) { TargetBox.Text = name; return name; }
        return TargetBox.Text.Trim();
    }

    private void PluginCopy_Click(object sender, RoutedEventArgs e)
    {
        var package = SelectedPlugin(sender);
        if (string.IsNullOrWhiteSpace(package)) return;
        try
        {
            Clipboard.SetText(package);
            PluginActionStatus.Text = $"שם החבילה {package} הועתק ללוח.";
        }
        catch (Exception ex)
        {
            PluginActionStatus.Text = "לא ניתן להעתיק את שם החבילה.";
            DiagnosticLogService.Instance.Warning("RestrictedCLI", "CopyPluginNameFailed",
                "Could not copy plugin package name to clipboard.", ex, ServerLabel.Text);
        }
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

    private string ResolveHistoryTarget(string label)
    {
        return label is "התקנה" or "עדכון" or "הפעלה" or "השבתה" or "הסרה" or "מחיקת ZIP" or "העלאת ZIP"
            ? TargetBox.Text.Trim()
            : string.Empty;
    }

    private void SaveHistory(string action, string target, bool succeeded, string summary)
    {
        try
        {
            new ServerActionHistoryService().Add(new ServerActionHistoryEntry(
                DateTimeOffset.UtcNow, _session.Profile.Name, _session.Profile.Host, _session.Profile.Port,
                action, target, succeeded, summary));
            RefreshActionHistory();
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Instance.Warning("ActionHistory", "SaveFailed",
                "Could not save the local server action history.", ex, ServerLabel.Text);
        }
    }

    private void RefreshActionHistory()
    {
        _historyEntries = new ServerActionHistoryService().GetForServer(_session.Profile.Host, _session.Profile.Port);
        HistoryCountText.Text = _historyEntries.Count == 0 ? "עדיין לא נשמרו פעולות" : $"נשמרו {_historyEntries.Count} פעולות אחרונות";
        ApplyHistoryFilter();
    }

    private void ApplyHistoryFilter()
    {
        var filter = HistoryFilterBox?.SelectedIndex ?? 0;
        var entries = _historyEntries.Where(entry =>
            filter == 0 || (filter == 1 && entry.Succeeded) || (filter == 2 && !entry.Succeeded)).ToArray();
        HistoryItems.ItemsSource = entries.Select(entry => new HistoryDisplayItem(
            entry.Timestamp.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss"),
            entry.Action,
            string.IsNullOrWhiteSpace(entry.Target) ? "—" : entry.Target,
            entry.Succeeded ? "הצליח" : "נכשל",
            entry.Succeeded ? "#18794E" : "#B42318",
            entry.Summary)).ToArray();
        HistoryEmptyState.Visibility = entries.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_historyEntries.Count > 0 && entries.Length == 0)
            HistoryCountText.Text = "אין פעולות התואמות למסנן";
        else if (_historyEntries.Count > 0)
            HistoryCountText.Text = $"מוצגות {entries.Length} מתוך {_historyEntries.Count} פעולות";
    }

    private void HistoryFilterBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (HistoryItems is not null) ApplyHistoryFilter();
    }

    private void ExportHistory_Click(object sender, RoutedEventArgs e)
    {
        var filter = HistoryFilterBox?.SelectedIndex ?? 0;
        var entries = _historyEntries.Where(entry =>
            filter == 0 || (filter == 1 && entry.Succeeded) || (filter == 2 && !entry.Succeeded)).ToArray();
        if (entries.Length == 0)
        {
            MessageBox.Show(this, "אין פעולות לייצוא במסנן הנוכחי.", "ייצוא היסטוריה",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "ייצוא היסטוריית פעולות",
            Filter = "קובץ CSV (*.csv)|*.csv",
            FileName = "nodebb-action-history.csv",
            AddExtension = true,
            DefaultExt = ".csv"
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            File.WriteAllText(dialog.FileName, ServerActionHistoryService.ExportCsv(entries), new System.Text.UTF8Encoding(true));
            MessageBox.Show(this, $"יוצאו {entries.Length} פעולות בהצלחה.", "ייצוא היסטוריה",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "ייצוא ההיסטוריה נכשל. בדוק הרשאות כתיבה ליעד.", "שגיאה",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            DiagnosticLogService.Instance.Warning("ActionHistory", "ExportFailed",
                "Could not export server action history.", ex, ServerLabel.Text);
        }
    }

    private void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "למחוק את היסטוריית הפעולות המקומית של השרת הזה? הפעולה אינה משנה דבר בשרת עצמו.",
            "מחיקת היסטוריה", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        new ServerActionHistoryService().ClearForServer(_session.Profile.Host, _session.Profile.Port);
        RefreshActionHistory();
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


public sealed record HistoryDisplayItem(string Timestamp, string Action, string Target, string Result, string ResultColor, string Summary);

public sealed class PluginCard(string name, string kindLabel)
{
    public string Name { get; } = name;
    public string KindLabel { get; } = kindLabel;
    public bool IsSelected { get; set; }
}
