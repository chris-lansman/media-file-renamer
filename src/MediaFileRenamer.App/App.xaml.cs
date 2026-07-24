using MediaFileRenamer.App.Services;
using System.Windows;
using System.Windows.Threading;

namespace MediaFileRenamer.App;

public partial class App : System.Windows.Application
{
    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        try
        {
            var options = AppStartupOptions.Parse(e.Args);
            if (options.DataPaths is not null)
            {
                AppDataPaths.Configure(options.DataPaths);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            System.Windows.MessageBox.Show(
                ex.Message,
                "Invalid startup option",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(2);
            return;
        }

        DiagnosticLog.Current.Information("Application starting.");
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (DiagnosticLog.IsInitialized)
        {
            DiagnosticLog.Current.Information(
                $"Application exiting with code {e.ApplicationExitCode}.");
        }

        base.OnExit(e);
    }

    private static void OnDispatcherUnhandledException(
        object sender,
        DispatcherUnhandledExceptionEventArgs e)
    {
        if (DiagnosticLog.IsInitialized)
        {
            DiagnosticLog.Current.Error("An unhandled UI error occurred.", e.Exception);
        }
        try
        {
            System.Windows.MessageBox.Show(
                "Media File Renamer encountered an unexpected error. "
                + "A redacted diagnostic log was saved and the app will close. "
                + "Review any active operation and its files before retrying.",
                "Unexpected error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        catch
        {
            // The UI may no longer be available.
        }

        e.Handled = true;
        System.Windows.Application.Current?.Shutdown(1);
    }

    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (DiagnosticLog.IsInitialized)
        {
            DiagnosticLog.Current.Error(
                "An unhandled application error occurred.",
                e.ExceptionObject as Exception);
        }
    }

    private static void OnUnobservedTaskException(
        object? sender,
        UnobservedTaskExceptionEventArgs e)
    {
        if (DiagnosticLog.IsInitialized)
        {
            DiagnosticLog.Current.Error(
                "An unobserved background task error occurred.",
                e.Exception);
        }

        e.SetObserved();
    }
}
