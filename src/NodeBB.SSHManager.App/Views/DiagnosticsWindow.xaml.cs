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
        var localization = LocalizationService.Instance;
        var dialog = new SaveFileDialog
        {
            Title = localization["Diagnostics_SaveTitle"],
            FileName = $"NodeBB-SSH-Diagnostics-{DateTime.Now:yyyyMMdd-HHmm}.zip",
            Filter = localization["Diagnostics_ZipFilter"],
            DefaultExt = ".zip",
            AddExtension = true
        };
        if (dialog.ShowDialog(this) != true) return;

        IsEnabled = false;
        StatusText.Text = localization["Diagnostics_Collecting"];
        try
        {
            await DiagnosticLogService.Instance.ExportReportAsync(dialog.FileName);
            StatusText.Text = localization["Diagnostics_Saved"];
            MessageBox.Show(this, localization.TranslateFormat($"הדוח נשמר בנתיב:\n{dialog.FileName}"),
                localization["Diagnostics_ExportDone"], MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Instance.Error("Diagnostics", "ReportExportFailed", ex);
            StatusText.Text = localization["Diagnostics_ExportFailed"];
            MessageBox.Show(this, ex.Message, localization["Diagnostics_ExportError"], MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { IsEnabled = true; }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}