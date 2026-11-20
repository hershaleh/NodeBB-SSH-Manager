using System.Windows;
using System.Windows.Threading;
using NodeBB.SSHManager.Services;

namespace NodeBB.SSHManager;

public partial class App : Application
{
    public App()
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
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

        ShutdownMode = ShutdownMode.OnMainWindowClose;
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
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