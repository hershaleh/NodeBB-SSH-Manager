using System.Windows;
using System.Windows.Threading;
using NodeBB.SSHManager.Services;

namespace NodeBB.SSHManager;

public partial class App : Application
{
    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        DiagnosticLogService.Instance.Info("Application", "Startup", "Application process initialized.");
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        DiagnosticLogService.Instance.Error("Application", "DispatcherUnhandledException", e.Exception);
        // Keep WPF's default fatal-error behavior; logging must not hide an unknown application failure.
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
