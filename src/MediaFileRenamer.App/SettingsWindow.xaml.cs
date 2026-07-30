using MediaFileRenamer.App.Services;
using System.IO;
using System.Windows;
using Forms = System.Windows.Forms;

namespace MediaFileRenamer.App;

public partial class SettingsWindow : Window
{
    private readonly MetadataProviderTestService _providerTester;

    public AppSettings Settings { get; }

    public SettingsWindow(
        AppSettings settings,
        string settingsPath,
        MetadataProviderTestService? providerTester = null,
        bool isFirstRun = false)
    {
        InitializeComponent();
        _providerTester = providerTester ?? new MetadataProviderTestService();
        Settings = new AppSettings
        {
            TmdbApiKey = settings.TmdbApiKey,
            UseTmdbLookup = settings.UseTmdbLookup,
            TvdbApiKey = settings.TvdbApiKey,
            TvdbPin = settings.TvdbPin,
            UseTvdbFallback = settings.UseTvdbFallback,
            AutoMatchConfidencePercent = settings.AutoMatchConfidencePercent,
            DefaultOutputFolder = settings.DefaultOutputFolder,
            DefaultOperation = settings.DefaultOperation
        };

        TmdbApiKeyBox.Text = Settings.TmdbApiKey;
        UseTmdbLookupCheckBox.IsChecked = Settings.UseTmdbLookup;
        TvdbApiKeyBox.Text = Settings.TvdbApiKey;
        TvdbPinBox.Text = Settings.TvdbPin;
        UseTvdbFallbackCheckBox.IsChecked = Settings.UseTvdbFallback;
        DefaultOutputFolderTextBox.Text = Settings.DefaultOutputFolder;
        AutoMatchConfidenceTextBox.Text = Settings.AutoMatchConfidencePercent.ToString();
        SettingsPathTextBox.Text = settingsPath;
        DefaultOperationComboBox.SelectedIndex =
            Settings.DefaultOperation == FileOperation.Copy ? 1 : 0;
        if (isFirstRun)
        {
            Title = "Welcome to Media File Renamer";
            FirstRunBanner.Visibility = Visibility.Visible;
            SaveSettingsButton.Content = "_Save and Start";
            DefaultOperationComboBox.SelectedIndex = 1;
        }
        RegisterCredentialsForRedaction();
        ValidateSettings();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var validation = ValidateSettings(checkFolderAccessibility: true);
        if (!validation.IsValid)
        {
            if (!string.IsNullOrEmpty(validation.ConfidenceError))
            {
                AutoMatchConfidenceTextBox.Focus();
                AutoMatchConfidenceTextBox.SelectAll();
            }
            else
            {
                DefaultOutputFolderTextBox.Focus();
                DefaultOutputFolderTextBox.SelectAll();
            }

            return;
        }

        Settings.TmdbApiKey = TmdbApiKeyBox.Text.Trim();
        Settings.UseTmdbLookup = UseTmdbLookupCheckBox.IsChecked == true;
        Settings.TvdbApiKey = TvdbApiKeyBox.Text.Trim();
        Settings.TvdbPin = TvdbPinBox.Text.Trim();
        Settings.UseTvdbFallback = UseTvdbFallbackCheckBox.IsChecked == true;
        Settings.AutoMatchConfidencePercent = validation.Confidence;
        Settings.DefaultOutputFolder = validation.OutputFolder;
        Settings.DefaultOperation = DefaultOperationComboBox.SelectedIndex == 1
            ? FileOperation.Copy
            : FileOperation.Move;
        RegisterCredentialsForRedaction();
        DiagnosticLog.Current.Information("Settings updated.");
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void BrowseOutput_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "Choose the default staging folder",
            SelectedPath = DefaultOutputFolderTextBox.Text,
            UseDescriptionForTitle = true
        };

        if (dialog.ShowDialog() == Forms.DialogResult.OK)
        {
            DefaultOutputFolderTextBox.Text = dialog.SelectedPath;
        }
    }

    private void ValidationField_Changed(
        object sender,
        System.Windows.Controls.TextChangedEventArgs e)
    {
        if (SaveSettingsButton is not null)
        {
            ValidateSettings();
        }
    }

    private async void TestTmdb_Click(object sender, RoutedEventArgs e)
    {
        TestTmdbButton.IsEnabled = false;
        TmdbTestStatusTextBlock.Foreground =
            (System.Windows.Media.Brush)FindResource("MutedTextBrush");
        TmdbTestStatusTextBlock.Text = "Testing TMDB...";
        DiagnosticLog.Current.RegisterSensitiveValue(TmdbApiKeyBox.Text);

        try
        {
            var result = await _providerTester.TestTmdbAsync(TmdbApiKeyBox.Text);
            ShowProviderResult(TmdbTestStatusTextBlock, result);
            DiagnosticLog.Current.Information(
                result.IsSuccess ? "TMDB connection test succeeded." : "TMDB connection test failed.");
        }
        finally
        {
            TestTmdbButton.IsEnabled = true;
        }
    }

    private async void TestTvdb_Click(object sender, RoutedEventArgs e)
    {
        TestTvdbButton.IsEnabled = false;
        TvdbTestStatusTextBlock.Foreground =
            (System.Windows.Media.Brush)FindResource("MutedTextBrush");
        TvdbTestStatusTextBlock.Text = "Testing TVDB...";
        DiagnosticLog.Current.RegisterSensitiveValue(TvdbApiKeyBox.Text);
        DiagnosticLog.Current.RegisterSensitiveValue(TvdbPinBox.Text);

        try
        {
            var result = await _providerTester.TestTvdbAsync(
                TvdbApiKeyBox.Text,
                TvdbPinBox.Text);
            ShowProviderResult(TvdbTestStatusTextBlock, result);
            DiagnosticLog.Current.Information(
                result.IsSuccess ? "TVDB connection test succeeded." : "TVDB connection test failed.");
        }
        finally
        {
            TestTvdbButton.IsEnabled = true;
        }
    }

    private void RegisterCredentialsForRedaction()
    {
        DiagnosticLog.Current.RegisterSensitiveValue(Settings.TmdbApiKey);
        DiagnosticLog.Current.RegisterSensitiveValue(Settings.TvdbApiKey);
        DiagnosticLog.Current.RegisterSensitiveValue(Settings.TvdbPin);
    }

    private void ShowProviderResult(
        System.Windows.Controls.TextBlock textBlock,
        ProviderConnectionResult result)
    {
        textBlock.Foreground = (System.Windows.Media.Brush)FindResource(
            result.IsSuccess ? "MatchBrush" : "BlockedBrush");
        textBlock.Text = result.Message;
    }

    private SettingsValidationResult ValidateSettings(
        bool checkFolderAccessibility = false)
    {
        var result = ValidateSettingsValues(
            AutoMatchConfidenceTextBox.Text,
            DefaultOutputFolderTextBox.Text,
            checkFolderAccessibility);

        ShowValidationError(
            AutoMatchConfidenceTextBox,
            ConfidenceValidationTextBlock,
            result.ConfidenceError);
        ShowValidationError(
            DefaultOutputFolderTextBox,
            OutputFolderValidationTextBlock,
            result.OutputFolderError);
        SaveSettingsButton.IsEnabled = result.IsValid;
        return result;
    }

    private void ShowValidationError(
        System.Windows.Controls.Control control,
        System.Windows.Controls.TextBlock errorText,
        string message)
    {
        errorText.Text = message;
        errorText.Visibility = string.IsNullOrEmpty(message)
            ? Visibility.Collapsed
            : Visibility.Visible;
        System.Windows.Automation.AutomationProperties.SetHelpText(control, message);
        if (string.IsNullOrEmpty(message))
        {
            control.SetResourceReference(
                System.Windows.Controls.Control.BorderBrushProperty,
                "BorderBrush");
        }
        else
        {
            control.SetResourceReference(
                System.Windows.Controls.Control.BorderBrushProperty,
                "BlockedBrush");
        }
    }

    internal static SettingsValidationResult ValidateSettingsValues(
        string confidenceText,
        string outputFolderText,
        bool checkFolderAccessibility = true)
    {
        var confidenceError = "";
        var confidence = 0;
        if (!int.TryParse(confidenceText, out confidence))
        {
            confidenceError = "Enter a whole-number confidence from 80 through 100.";
        }
        else if (confidence is < 80 or > 100)
        {
            confidenceError = "Confidence must be from 80 through 100.";
        }

        var (outputFolder, outputFolderError) =
            ValidateOutputFolder(outputFolderText, checkFolderAccessibility);
        return new SettingsValidationResult(
            confidence,
            outputFolder,
            confidenceError,
            outputFolderError);
    }

    private static (string OutputFolder, string Error) ValidateOutputFolder(
        string value,
        bool checkAccessibility)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return ("", "Choose an output folder.");
        }

        string fullPath;
        try
        {
            var trimmed = value.Trim();
            if (!Path.IsPathFullyQualified(trimmed))
            {
                return ("", "Enter a complete folder path, such as C:\\Media\\Renamed.");
            }

            fullPath = Path.GetFullPath(trimmed);
        }
        catch (Exception ex) when (
            ex is ArgumentException
                or NotSupportedException
                or PathTooLongException)
        {
            return ("", "The output folder path is not valid.");
        }

        if (!checkAccessibility)
        {
            return (fullPath, "");
        }

        string existingPath;
        try
        {
            existingPath = fullPath;
            while (!Directory.Exists(existingPath))
            {
                var parent = Directory.GetParent(existingPath);
                if (parent is null)
                {
                    return (
                        "",
                        "The output folder is unavailable. Connect the drive or network location and try again.");
                }

                existingPath = parent.FullName;
            }
        }
        catch (Exception ex) when (
            ex is ArgumentException
                or IOException
                or UnauthorizedAccessException
                or System.Security.SecurityException)
        {
            return ("", "The output folder path is not valid or cannot be accessed.");
        }

        try
        {
            _ = Directory.EnumerateFileSystemEntries(existingPath).Take(1).ToList();
        }
        catch (Exception ex) when (
            ex is IOException
                or UnauthorizedAccessException
                or System.Security.SecurityException)
        {
            return (
                "",
                "The output folder cannot be accessed. Check its permissions or connection.");
        }

        return (fullPath, "");
    }
}

internal sealed record SettingsValidationResult(
    int Confidence,
    string OutputFolder,
    string ConfidenceError,
    string OutputFolderError)
{
    public bool IsValid =>
        string.IsNullOrEmpty(ConfidenceError)
        && string.IsNullOrEmpty(OutputFolderError);
}
