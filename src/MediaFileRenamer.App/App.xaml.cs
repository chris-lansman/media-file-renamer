using MediaFileRenamer.App.Services;
using Microsoft.Win32;
using System.ComponentModel;
using System.Net.Http;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
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
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        if (UpdateInstallService.TryApplyFromArguments(e.Args, out var updateFailure))
        {
            if (!string.IsNullOrWhiteSpace(updateFailure))
            {
                System.Windows.MessageBox.Show(
                    updateFailure,
                    "Update could not be installed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                Shutdown(1);
            }
            else
            {
                Shutdown(0);
            }

            return;
        }

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

        ApplyColorPalette();
        UpdateInstallService.CleanupStaleSessions();
        DiagnosticLog.Current.Information("Application starting.");
        base.OnStartup(e);
        _ = OfferAvailableUpdateAsync();
    }

    private async Task OfferAvailableUpdateAsync()
    {
        try
        {
            var currentVersion = Assembly.GetEntryAssembly()?.GetName().Version
                ?? new Version(0, 0, 0);
            var update = await new GitHubReleaseUpdateChecker().CheckAsync(
                currentVersion,
                CancellationToken.None);
            if (!update.UpdateAvailable)
            {
                return;
            }

            var choice = System.Windows.MessageBox.Show(
                $"Version {update.LatestVersion.ToString(3)} is available. Open the update window now?",
                "Media File Renamer update available",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information);
            if (choice != MessageBoxResult.Yes)
            {
                return;
            }

            var window = new AboutWindow(
                new GitHubReleaseUpdateChecker(),
                currentVersion,
                initialUpdate: update);
            if (MainWindow is Window owner)
            {
                window.Owner = owner;
            }

            window.Show();
        }
        catch (Exception ex) when (
            ex is HttpRequestException
                or TaskCanceledException
                or InvalidOperationException)
        {
            DiagnosticLog.Current.Information("Automatic update check was unavailable.");
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (DiagnosticLog.IsInitialized)
        {
            DiagnosticLog.Current.Information(
                $"Application exiting with code {e.ApplicationExitCode}.");
        }

        SystemParameters.StaticPropertyChanged -= OnSystemParametersChanged;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        base.OnExit(e);
    }

    private void OnSystemParametersChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.HighContrast))
        {
            ApplyColorPalette();
        }
    }

    private void OnUserPreferenceChanged(
        object sender,
        UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.Color
            or UserPreferenceCategory.General
            or UserPreferenceCategory.VisualStyle)
        {
            _ = Dispatcher.BeginInvoke(ApplyColorPalette);
        }
    }

    private void ApplyColorPalette()
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
            "ControlHoverBrush",
            "ControlPressedBrush",
            "DisabledBackgroundBrush",
            "DisabledBorderBrush",
            "DisabledTextBrush",
            "PrimaryDisabledBrush",
            "PrimaryDisabledTextBrush",
            "GridLineBrush",
        ];

        foreach (var key in paletteKeys)
        {
            Resources.Remove(key);
        }

        if (SystemParameters.HighContrast)
        {
            ApplyHighContrastPalette();
            return;
        }

        if (IsWindowsLightTheme())
        {
            return;
        }

        ApplyDarkPalette();
    }

    private void ApplyHighContrastPalette()
    {
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
        Resources["ControlHoverBrush"] = WpfSystemColors.ControlBrush;
        Resources["ControlPressedBrush"] = WpfSystemColors.ControlBrush;
        Resources["DisabledBackgroundBrush"] = WpfSystemColors.ControlBrush;
        Resources["DisabledBorderBrush"] = WpfSystemColors.GrayTextBrush;
        Resources["DisabledTextBrush"] = WpfSystemColors.GrayTextBrush;
        Resources["PrimaryDisabledBrush"] = WpfSystemColors.ControlBrush;
        Resources["PrimaryDisabledTextBrush"] = WpfSystemColors.GrayTextBrush;
        Resources["GridLineBrush"] = WpfSystemColors.WindowTextBrush;
    }

    private void ApplyDarkPalette()
    {
        SetBrush("AppBackgroundBrush", "#111827");
        SetBrush("SurfaceBrush", "#182235");
        SetBrush("SurfaceMutedBrush", "#233044");
        SetBrush("TextBrush", "#F3F6FA");
        SetBrush("MutedTextBrush", "#B7C4D4");
        SetBrush("BorderBrush", "#3B4A5F");
        SetBrush("AccentBrush", "#38A3D1");
        SetBrush("AccentHoverBrush", "#58B5DA");
        SetBrush("AccentPressedBrush", "#2588B2");
        SetBrush("AccentSoftBrush", "#17384A");
        SetBrush("MatchBrush", "#5FD0A0");
        SetBrush("MatchSoftBrush", "#153A31");
        SetBrush("ReviewBrush", "#FFC76B");
        SetBrush("ReviewSoftBrush", "#44331A");
        SetBrush("BlockedBrush", "#FF8A83");
        SetBrush("BlockedSoftBrush", "#442322");
        SetBrush("ControlHoverBrush", "#263449");
        SetBrush("ControlPressedBrush", "#31435B");
        SetBrush("DisabledBackgroundBrush", "#202B3B");
        SetBrush("DisabledBorderBrush", "#344155");
        SetBrush("DisabledTextBrush", "#7C8B9E");
        SetBrush("PrimaryDisabledBrush", "#28556A");
        SetBrush("PrimaryDisabledTextBrush", "#AFC4CE");
        SetBrush("GridLineBrush", "#3B4A5F");
    }

    private void SetBrush(string key, string color)
    {
        Resources[key] = new SolidColorBrush(
            (System.Windows.Media.Color)
            System.Windows.Media.ColorConverter.ConvertFromString(color));
    }

    internal static bool IsWindowsLightTheme()
    {
        try
        {
            using var personalization = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return personalization?.GetValue("AppsUseLightTheme") is not int value
                || value != 0;
        }
        catch
        {
            return true;
        }
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
