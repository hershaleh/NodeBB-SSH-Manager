using System.Windows;
using System.Windows.Threading;
using System.ComponentModel;
using NodeBB.SSHManager.Services;
using NodeBB.SSHManager.Localization;

namespace NodeBB.SSHManager;

public partial class App : Application
{
    private const string StartupSmokeTestArgument = "--startup-smoke-test";

    public App()
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler(ClampWindowToWorkArea));
        LocalizationService.Instance.PropertyChanged += OnLocalizationChanged;
        DiagnosticLogService.Instance.Info("Application", "Startup", "Application process initialized.");
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Length == 2 &&
            string.Equals(e.Args[0], "--scheduled-backup", StringComparison.Ordinal) &&
            Guid.TryParseExact(e.Args[1], "N", out var scheduleId))
        {
            try
            {
                await ScheduledBackupRunner.RunAsync(scheduleId);
                Shutdown(0);
            }
            catch (Exception ex)
            {
                DiagnosticLogService.Instance.Error("ScheduledBackup", "ScheduledBackupProcessFailed", ex);
                Shutdown(1);
            }
            return;
        }

        var startupSmokeTest = e.Args.Length == 1 &&
            string.Equals(e.Args[0], StartupSmokeTestArgument, StringComparison.Ordinal);

        try
        {
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            var window = new MainWindow();
            MainWindow = window;

            if (startupSmokeTest)
            {
                window.Loaded += (_, _) =>
                    Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => Shutdown(0)));
            }

            window.Show();
            DiagnosticLogService.Instance.Info("Application", "MainWindowShown",
                startupSmokeTest ? "Startup smoke test displayed the main window." : "Main window displayed.");
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Instance.Error("Application", "StartupFailed", ex);
            if (!startupSmokeTest)
            {
                var logPath = Path.Combine(DiagnosticLogService.Instance.LogDirectory,
                    $"application-{DateTime.UtcNow:yyyyMMdd}.jsonl");
                MessageBox.Show(
                    $"התוכנה נכשלה בעת ההפעלה.\n\n{ex.GetType().Name}: {ex.Message}\n\nדוח האבחון נשמר כאן:\n{logPath}",
                    "NodeBB SSH Manager — שגיאת הפעלה",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            Shutdown(1);
        }
    }

    private static void OnLocalizationChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(LocalizationService.FlowDirection)) return;
        foreach (Window window in Current.Windows)
            window.FlowDirection = LocalizationService.Instance.FlowDirection;
    }

    private static void ClampWindowToWorkArea(object sender, RoutedEventArgs e)
    {
        if (sender is not Window window) return;

        var workArea = SystemParameters.WorkArea;
        var maxWidth = Math.Max(320, workArea.Width - 24);
        var maxHeight = Math.Max(320, workArea.Height - 24);
        window.FlowDirection = LocalizationService.Instance.FlowDirection;
        window.MinWidth = Math.Min(window.MinWidth, maxWidth);
        window.MinHeight = Math.Min(window.MinHeight, maxHeight);
        window.MaxWidth = maxWidth;
        window.MaxHeight = maxHeight;

        if (window.WindowState != WindowState.Maximized)
        {
            if (window.Width > maxWidth) window.Width = maxWidth;
            if (window.Height > maxHeight) window.Height = maxHeight;
        }

        var left = double.IsFinite(window.Left)
            ? window.Left
            : workArea.Left + (workArea.Width - window.Width) / 2;
        var top = double.IsFinite(window.Top)
            ? window.Top
            : workArea.Top + (workArea.Height - window.Height) / 2;
        window.Left = Math.Clamp(left, workArea.Left, Math.Max(workArea.Left, workArea.Right - window.Width));
        window.Top = Math.Clamp(top, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - window.Height));
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        DiagnosticLogService.Instance.Error("Application", "DispatcherUnhandledException", e.Exception);
    }

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
            DiagnosticLogService.Instance.Error("Application", "UnhandledException", exception);
        else
            DiagnosticLogService.Instance.Error("Application", "UnhandledException", "An unhandled non-Exception object was thrown.");
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        DiagnosticLogService.Instance.Error("Application", "UnobservedTaskException", e.Exception);
        e.SetObserved();
    }
}
