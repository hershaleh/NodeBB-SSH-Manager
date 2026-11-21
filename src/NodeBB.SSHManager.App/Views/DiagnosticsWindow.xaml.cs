using NodeBB.SSHManager.Localization;
using System.Windows;
using Microsoft.Win32;
using NodeBB.SSHManager.Services;

namespace NodeBB.SSHManager.Views;

public partial class DiagnosticsWindow : Window
{
    public DiagnosticsWindow()
    {
        InitializeComponent();
        DiagnosticLogService.Instance.Info("Diagnostics", "WindowOpened", "Diagnostics window opened.");
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = LocalizationService.Instance.Translate("שמירת דוח אבחון"),
            FileName = $"NodeBB-SSH-Diagnostics-{DateTime.Now:yyyyMMdd-HHmm}.zip",
            Filter = LocalizationService.Instance.Translate("קובץ ZIP|*.zip"),
            DefaultExt = ".zip",
            AddExtension = true
        };
        if (dialog.ShowDialog(this) != true) return;

        IsEnabled = false;
        StatusText.Text = LocalizationService.Instance.Translate("אוסף יומנים ומכין דוח...");
        try
        {
            await DiagnosticLogService.Instance.ExportReportAsync(dialog.FileName);
            StatusText.Text = LocalizationService.Instance.Translate("דוח האבחון נשמר בהצלחה.");
            MessageBox.Show(this, $"הדוח נשמר בנתיב:\n{dialog.FileName}", "הייצוא הושלם",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Instance.Error("Diagnostics", "ReportExportFailed", ex);
            StatusText.Text = LocalizationService.Instance.Translate("ייצוא הדוח נכשל.");
            MessageBox.Show(this, ex.Message, "שגיאה בייצוא", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { IsEnabled = true; }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
