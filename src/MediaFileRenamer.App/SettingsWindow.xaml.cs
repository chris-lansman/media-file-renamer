using MediaFileRenamer.App.Services;
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
        MetadataProviderTestService? providerTester = null)
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
            DefaultOutputFolder = settings.DefaultOutputFolder
        };

        TmdbApiKeyBox.Text = Settings.TmdbApiKey;
        UseTmdbLookupCheckBox.IsChecked = Settings.UseTmdbLookup;
        TvdbApiKeyBox.Text = Settings.TvdbApiKey;
        TvdbPinBox.Text = Settings.TvdbPin;
        UseTvdbFallbackCheckBox.IsChecked = Settings.UseTvdbFallback;
        DefaultOutputFolderTextBox.Text = Settings.DefaultOutputFolder;
        AutoMatchConfidenceTextBox.Text = Settings.AutoMatchConfidencePercent.ToString();
        SettingsPathTextBox.Text = settingsPath;
        RegisterCredentialsForRedaction();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        Settings.TmdbApiKey = TmdbApiKeyBox.Text.Trim();
        Settings.UseTmdbLookup = UseTmdbLookupCheckBox.IsChecked == true;
        Settings.TvdbApiKey = TvdbApiKeyBox.Text.Trim();
        Settings.TvdbPin = TvdbPinBox.Text.Trim();
        Settings.UseTvdbFallback = UseTvdbFallbackCheckBox.IsChecked == true;
        Settings.AutoMatchConfidencePercent = ParseConfidence(AutoMatchConfidenceTextBox.Text);
        Settings.DefaultOutputFolder = string.IsNullOrWhiteSpace(DefaultOutputFolderTextBox.Text)
            ? AppSettings.GetDefaultOutputFolder()
            : DefaultOutputFolderTextBox.Text.Trim();
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

    private static int ParseConfidence(string value)
    {
        if (!int.TryParse(value, out var confidence))
        {
            return 92;
        }

        return Math.Clamp(confidence, 80, 100);
    }
}
