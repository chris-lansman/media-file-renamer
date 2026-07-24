using MediaFileRenamer.App.Services;
using System.Windows;
using Forms = System.Windows.Forms;

namespace MediaFileRenamer.App;

public partial class SettingsWindow : Window
{
    public AppSettings Settings { get; }

    public SettingsWindow(AppSettings settings, string settingsPath)
    {
        InitializeComponent();
        Settings = new AppSettings
        {
            TmdbApiKey = settings.TmdbApiKey,
            UseTmdbLookup = settings.UseTmdbLookup,
            TvdbApiKey = settings.TvdbApiKey,
            UseTvdbFallback = settings.UseTvdbFallback,
            AutoMatchConfidencePercent = settings.AutoMatchConfidencePercent,
            DefaultOutputFolder = settings.DefaultOutputFolder
        };

        TmdbApiKeyBox.Password = Settings.TmdbApiKey;
        UseTmdbLookupCheckBox.IsChecked = Settings.UseTmdbLookup;
        TvdbApiKeyBox.Password = Settings.TvdbApiKey;
        UseTvdbFallbackCheckBox.IsChecked = Settings.UseTvdbFallback;
        DefaultOutputFolderTextBox.Text = Settings.DefaultOutputFolder;
        AutoMatchConfidenceTextBox.Text = Settings.AutoMatchConfidencePercent.ToString();
        SettingsPathTextBox.Text = settingsPath;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        Settings.TmdbApiKey = TmdbApiKeyBox.Password.Trim();
        Settings.UseTmdbLookup = UseTmdbLookupCheckBox.IsChecked == true;
        Settings.TvdbApiKey = TvdbApiKeyBox.Password.Trim();
        Settings.UseTvdbFallback = UseTvdbFallbackCheckBox.IsChecked == true;
        Settings.AutoMatchConfidencePercent = ParseConfidence(AutoMatchConfidenceTextBox.Text);
        Settings.DefaultOutputFolder = string.IsNullOrWhiteSpace(DefaultOutputFolderTextBox.Text)
            ? AppSettings.GetDefaultOutputFolder()
            : DefaultOutputFolderTextBox.Text.Trim();
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

    private static int ParseConfidence(string value)
    {
        if (!int.TryParse(value, out var confidence))
        {
            return 92;
        }

        return Math.Clamp(confidence, 80, 100);
    }
}
