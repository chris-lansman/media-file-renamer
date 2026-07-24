using MediaFileRenamer.App.Services;
using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using WpfSystemColors = System.Windows.SystemColors;

namespace MediaFileRenamer.App;

public partial class App : System.Windows.Application
{
    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        SystemParameters.StaticPropertyChanged += OnSystemParametersChanged;
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

        ApplyHighContrastPalette();
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

        SystemParameters.StaticPropertyChanged -= OnSystemParametersChanged;
        base.OnExit(e);
    }

    private void OnSystemParametersChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.HighContrast))
        {
            ApplyHighContrastPalette();
        }
    }

    private void ApplyHighContrastPalette()
    {
        string[] paletteKeys =
        [
            "AppBackgroundBrush",
            "SurfaceBrush",
            "SurfaceMutedBrush",
            "TextBrush",
            "MutedTextBrush",
            "BorderBrush",
            "AccentBrush",
            "AccentHoverBrush",
            "AccentPressedBrush",
            "AccentSoftBrush",
            "MatchBrush",
            "MatchSoftBrush",
            "ReviewBrush",
            "ReviewSoftBrush",
            "BlockedBrush",
            "BlockedSoftBrush",
        ];

        foreach (var key in paletteKeys)
        {
            Resources.Remove(key);
        }

        if (!SystemParameters.HighContrast)
        {
            return;
        }

        Resources["AppBackgroundBrush"] = WpfSystemColors.WindowBrush;
        Resources["SurfaceBrush"] = WpfSystemColors.WindowBrush;
        Resources["SurfaceMutedBrush"] = WpfSystemColors.WindowBrush;
        Resources["TextBrush"] = WpfSystemColors.WindowTextBrush;
        Resources["MutedTextBrush"] = WpfSystemColors.WindowTextBrush;
        Resources["BorderBrush"] = WpfSystemColors.WindowTextBrush;
        Resources["AccentBrush"] = WpfSystemColors.HighlightBrush;
        Resources["AccentHoverBrush"] = WpfSystemColors.HighlightBrush;
        Resources["AccentPressedBrush"] = WpfSystemColors.HighlightBrush;
        Resources["AccentSoftBrush"] = WpfSystemColors.WindowBrush;
        Resources["MatchBrush"] = WpfSystemColors.HighlightBrush;
        Resources["MatchSoftBrush"] = WpfSystemColors.WindowBrush;
        Resources["ReviewBrush"] = WpfSystemColors.HighlightBrush;
        Resources["ReviewSoftBrush"] = WpfSystemColors.WindowBrush;
        Resources["BlockedBrush"] = WpfSystemColors.HighlightBrush;
        Resources["BlockedSoftBrush"] = WpfSystemColors.WindowBrush;
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
