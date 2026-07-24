using MediaFileRenamer.App.Services;
using MediaFileRenamer.App.ViewModels;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Forms = System.Windows.Forms;

namespace MediaFileRenamer.App;

public partial class MainWindow : Window
{
    private readonly MediaScanner _scanner = new();
    private readonly RenamePlanner _planner = new();
    private readonly RenameApplier _applier = new();
    private readonly AppSettingsService _settingsService = new();
    private AppSettings _settings = new();
    private bool _isSyncingSelection;

    public ObservableCollection<MediaPreviewItem> PreviewItems { get; } = [];

    public MainWindow()
    {
        InitializeComponent();
        _settings = _settingsService.Load();
        OutputFolderTextBox.Text = _settings.DefaultOutputFolder;
        DataContext = this;
    }

    private void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Multiselect = true,
            Filter = "Media files|*.mkv;*.mp4;*.m4v;*.avi;*.mov;*.wmv;*.ts;*.mpeg;*.mpg|All files|*.*"
        };

        if (dialog.ShowDialog(this) == true)
        {
            AddSources(dialog.FileNames);
        }
    }

    private void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "Choose a folder containing movies or TV episodes",
            UseDescriptionForTitle = true
        };

        if (dialog.ShowDialog() == Forms.DialogResult.OK)
        {
            AddSources([dialog.SelectedPath]);
        }
    }

    private void BrowseOutput_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "Choose where renamed media should be staged",
            SelectedPath = OutputFolderTextBox.Text,
            UseDescriptionForTitle = true
        };

        if (dialog.ShowDialog() == Forms.DialogResult.OK)
        {
            OutputFolderTextBox.Text = dialog.SelectedPath;
            RefreshDestinations();
        }
    }

    private async void MatchAll_Click(object sender, RoutedEventArgs e)
    {
        await MatchAllAsync();
    }

    private async Task MatchAllAsync()
    {
        var key = _settings.TmdbApiKey.Trim();
        var useTmdb = _settings.UseTmdbLookup && !string.IsNullOrWhiteSpace(key);
        var client = useTmdb ? new TmdbClient(key) : null;
        var tvdbClient = CreateTvdbFallback();

        StatusTextBlock.Text = useTmdb ? "Matching all files with TMDB..." : "Planning all filenames from local names...";

        foreach (var item in PreviewItems)
        {
            if (client is not null && item.TmdbId is null)
            {
                var selected = await MatchItemAsync(client, tvdbClient, item, showPickerForUncertain: true);
                if (selected is not null)
                {
                    await ApplyTvShowIdentityToRelatedItemsAsync(client, item, selected);
                }
            }

            UpdateDestination(item);
        }

        StatusTextBlock.Text = $"Ready: {PreviewItems.Count} item(s) matched/planned.";
    }

    private async void MatchSelected_Click(object sender, RoutedEventArgs e)
    {
        if (OriginalGrid.SelectedItem is not MediaPreviewItem item)
        {
            System.Windows.MessageBox.Show(this, "Select one media file before matching.", "No file selected", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var key = _settings.TmdbApiKey.Trim();
        if (string.IsNullOrWhiteSpace(key))
        {
            System.Windows.MessageBox.Show(this, "Add your TMDB API key in File > Settings before matching.", "TMDB key needed", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        StatusTextBlock.Text = $"Matching {item.SourceFileName}...";
        var client = new TmdbClient(key);
        var selected = await MatchItemAsync(client, CreateTvdbFallback(), item, showPickerForUncertain: true);
        if (selected is not null)
        {
            await ApplyTvShowIdentityToRelatedItemsAsync(client, item, selected);
        }

        UpdateDestination(item);
        StatusTextBlock.Text = $"Match result: {item.Status}.";
    }

    private async void ChooseSelected_Click(object sender, RoutedEventArgs e)
    {
        if (OriginalGrid.SelectedItem is not MediaPreviewItem item)
        {
            System.Windows.MessageBox.Show(this, "Select one media file before choosing a match.", "No file selected", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var key = _settings.TmdbApiKey.Trim();
        if (string.IsNullOrWhiteSpace(key))
        {
            System.Windows.MessageBox.Show(this, "Add your TMDB API key in File > Settings before choosing a match.", "TMDB key needed", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        StatusTextBlock.Text = $"Loading choices for {item.SourceFileName}...";
        var client = new TmdbClient(key);
        var selected = await MatchItemAsync(client, CreateTvdbFallback(), item, showPickerForUncertain: true, alwaysShowPicker: true);
        if (selected is not null)
        {
            await ApplyTvShowIdentityToRelatedItemsAsync(client, item, selected);
        }

        UpdateDestination(item);
        StatusTextBlock.Text = $"Match result: {item.Status}.";
    }

    private void ApplyRename_Click(object sender, RoutedEventArgs e)
    {
        if (PreviewItems.Count == 0)
        {
            System.Windows.MessageBox.Show(this, "Add files or a folder before applying a rename.", "Nothing to rename", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var operation = OperationComboBox.SelectedIndex == 0 ? FileOperation.Move : FileOperation.Copy;
        RefreshDestinations();
        StatusTextBlock.Text = "Applying staged rename...";
        var result = _applier.Apply(PreviewItems, operation);
        RemoveCompletedItems(result.CompletedItems);

        var failedCount = PreviewItems.Count;
        var folderSummary = result.DeletedSourceFolders > 0
            ? $" Removed {result.DeletedSourceFolders} empty source folder(s)."
            : "";
        StatusTextBlock.Text = failedCount > 0
            ? $"Completed {result.CompletedItems.Count} item(s); {failedCount} failed item(s) remain.{folderSummary}"
            : $"Completed {result.CompletedItems.Count} item(s). The list is ready for more files.{folderSummary}";
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _isSyncingSelection = true;
            OriginalGrid.SelectedItem = null;
            NewNamesGrid.SelectedItem = null;
            PreviewItems.Clear();
        }
        finally
        {
            _isSyncingSelection = false;
        }

        StatusTextBlock.Text = "Cleared.";
    }

    private void RemoveCompletedItems(IEnumerable<MediaPreviewItem> completedItems)
    {
        try
        {
            _isSyncingSelection = true;
            OriginalGrid.SelectedItem = null;
            NewNamesGrid.SelectedItem = null;

            foreach (var item in completedItems.ToList())
            {
                PreviewItems.Remove(item);
            }
        }
        finally
        {
            _isSyncingSelection = false;
        }

        if (PreviewItems.Count > 0)
        {
            OriginalGrid.SelectedItem = PreviewItems[0];
        }
    }

    private void SettingsMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var window = new SettingsWindow(_settings, _settingsService.SettingsPath)
        {
            Owner = this
        };

        if (window.ShowDialog() == true)
        {
            _settings = window.Settings;
            _settingsService.Save(_settings);
            OutputFolderTextBox.Text = _settings.DefaultOutputFolder;
            RefreshDestinations();
            StatusTextBlock.Text = "Settings saved.";
        }
    }

    private void ExitMenuItem_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void PresetComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RefreshDestinations();
    }

    private void CustomFormatTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        RefreshDestinations();
    }

    private void OutputFolderTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (IsLoaded)
        {
            RefreshDestinations();
        }
    }

    private void OriginalGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SyncSelection(OriginalGrid, NewNamesGrid);
    }

    private void NewNamesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SyncSelection(NewNamesGrid, OriginalGrid);
    }

    private void Window_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
        {
            AddSources((string[])e.Data.GetData(System.Windows.DataFormats.FileDrop));
        }
    }

    private void AddSources(IEnumerable<string> paths)
    {
        IReadOnlyList<MediaPreviewItem> items;
        try
        {
            items = _scanner.Scan(paths);
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = $"Could not scan media: {ex.Message}";
            return;
        }

        var existingSources = PreviewItems
            .Select(item => Path.GetFullPath(item.SourcePath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var addedCount = 0;
        foreach (var item in items)
        {
            if (!existingSources.Add(Path.GetFullPath(item.SourcePath)))
            {
                continue;
            }

            item.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName is nameof(MediaPreviewItem.MatchedTitle)
                    or nameof(MediaPreviewItem.MediaType)
                    or nameof(MediaPreviewItem.Year)
                    or nameof(MediaPreviewItem.Season)
                    or nameof(MediaPreviewItem.Episode)
                    or nameof(MediaPreviewItem.EpisodeTitle)
                    or nameof(MediaPreviewItem.TmdbId))
                {
                    UpdateDestination(item);
                }
            };
            UpdateDestination(item);
            PreviewItems.Add(item);
            addedCount++;
        }

        if (OriginalGrid.SelectedItem is null && PreviewItems.Count > 0)
        {
            OriginalGrid.SelectedItem = PreviewItems[0];
        }

        StatusTextBlock.Text = addedCount == 0
            ? "No new media files were added."
            : $"Added {addedCount} media file(s); {PreviewItems.Count} total.";
    }

    private void RefreshDestinations()
    {
        foreach (var item in PreviewItems)
        {
            UpdateDestination(item);
        }
    }

    private bool UpdateDestination(MediaPreviewItem item)
    {
        try
        {
            item.DestinationPath = _planner.BuildDestination(
                item,
                OutputFolderTextBox.Text,
                GetPreset(),
                CustomFormatTextBox.Text);
            if (item.Status.StartsWith("Invalid destination:", StringComparison.Ordinal))
            {
                item.Status = "Ready";
            }
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            item.DestinationPath = "";
            item.Status = $"Invalid destination: {ex.Message}";
            return false;
        }
    }

    private async Task<TmdbCandidate?> MatchItemAsync(TmdbClient client, TvdbClient? tvdbClient, MediaPreviewItem item, bool showPickerForUncertain, bool alwaysShowPicker = false)
    {
        var primaryQuery = item.TitleGuess;
        var candidates = await client.SearchCandidatesAsync(item, primaryQuery);
        var initialQuery = primaryQuery;

        if (candidates.Count == 0 && tvdbClient is not null && item.MediaType == "TV")
        {
            candidates = await SearchTvdbFallbackAsync(client, tvdbClient, primaryQuery);
        }

        TmdbCandidate? selected = null;
        TmdbAutoMatch? primaryAutoMatch = null;
        int? autoConfidence = null;
        if (!alwaysShowPicker)
        {
            primaryAutoMatch = TmdbClient.FindAutoMatch(item, candidates, _settings.AutoMatchConfidencePercent);
            if (primaryAutoMatch is not null && item.MediaType != "Unknown")
            {
                selected = primaryAutoMatch.Candidate;
                autoConfidence = primaryAutoMatch.ConfidencePercent;
            }
        }

        var parentEvidenceFound = false;
        if (selected is null)
        {
            var parentFolder = Path.GetFileName(item.SourceGroupPath);
            var parentQuery = string.IsNullOrWhiteSpace(parentFolder)
                ? ""
                : MediaScanner.CleanSearchText(parentFolder);

            if (!string.IsNullOrWhiteSpace(parentQuery)
                && !string.Equals(parentQuery, primaryQuery, StringComparison.OrdinalIgnoreCase))
            {
                var parentCandidates = await client.SearchCandidatesAsync(item, parentQuery);
                if (parentCandidates.Count == 0 && tvdbClient is not null && item.MediaType == "TV")
                {
                    parentCandidates = await SearchTvdbFallbackAsync(client, tvdbClient, parentQuery);
                }
                if (parentCandidates.Count > 0)
                {
                    parentEvidenceFound = true;
                    initialQuery = parentQuery;
                    candidates = parentCandidates
                        .Concat(candidates)
                        .DistinctBy(candidate => (candidate.MediaType, candidate.Id))
                        .ToList();

                    if (!alwaysShowPicker)
                    {
                        var parentAutoMatch = TmdbClient.FindAutoMatch(
                            item,
                            candidates,
                            _settings.AutoMatchConfidencePercent,
                            parentQuery);
                        if (parentAutoMatch is not null)
                        {
                            selected = parentAutoMatch.Candidate;
                            autoConfidence = parentAutoMatch.ConfidencePercent;
                        }
                    }
                }
            }
        }

        if (selected is null
            && !alwaysShowPicker
            && item.MediaType == "Unknown"
            && item.GroupFileCount == 1
            && !parentEvidenceFound
            && primaryAutoMatch is not null)
        {
            selected = primaryAutoMatch.Candidate;
            autoConfidence = primaryAutoMatch.ConfidencePercent;
        }

        if (selected is null && showPickerForUncertain)
        {
            selected = ShowMatchPicker(item, client, initialQuery, candidates, out var useLocalGuess);
            if (useLocalGuess)
            {
                item.Status = "Local guess";
                return null;
            }
        }

        if (selected is null)
        {
            item.Status = "Needs review";
            return null;
        }

        if (selected.MediaType == "TV" && item.MediaType == "Unknown" && string.IsNullOrWhiteSpace(item.EpisodeTitle))
        {
            item.EpisodeTitle = item.TitleGuess;
        }

        var match = await client.BuildMatchAsync(selected, item);
        item.MediaType = selected.MediaType;
        item.TmdbId = match.TmdbId;
        item.MatchedTitle = match.Title;
        item.Year = match.Year ?? item.Year;
        item.Season = match.Season ?? item.Season;
        item.Episode = match.Episode ?? item.Episode;
        item.EpisodeTitle = match.EpisodeTitle ?? item.EpisodeTitle;
        item.Status = selected.MediaType == "TV" && (item.Season is null || item.Episode is null)
            ? "TV matched; episode needs review"
            : alwaysShowPicker
                ? "Manual TMDB match"
                : autoConfidence is null ? "TMDB match" : $"TMDB auto {autoConfidence}%";
        return selected;
    }

    private TvdbClient? CreateTvdbFallback()
    {
        var key = _settings.TvdbApiKey.Trim();
        return _settings.UseTvdbFallback && !string.IsNullOrWhiteSpace(key)
            ? new TvdbClient(key)
            : null;
    }

    private static async Task<IReadOnlyList<TmdbCandidate>> SearchTvdbFallbackAsync(TmdbClient tmdbClient, TvdbClient tvdbClient, string query)
    {
        var tvdbIds = await tvdbClient.SearchSeriesIdsAsync(query);
        var candidates = new List<TmdbCandidate>();
        foreach (var tvdbId in tvdbIds)
        {
            var candidate = await tmdbClient.FindTvByTvdbIdAsync(tvdbId);
            if (candidate is not null)
            {
                candidates.Add(candidate);
            }
        }

        return candidates
            .DistinctBy(candidate => candidate.Id)
            .ToList();
    }

    private async Task ApplyTvShowIdentityToRelatedItemsAsync(TmdbClient client, MediaPreviewItem matchedItem, TmdbCandidate selected)
    {
        if (matchedItem.MediaType != "TV" || selected.MediaType != "TV")
        {
            return;
        }

        var identity = TvShowIdentityMatcher.FromCandidate(selected);
        foreach (var relatedItem in PreviewItems.Where(item => item != matchedItem && TvShowIdentityMatcher.IsRelatedEpisode(matchedItem, item)))
        {
            if (relatedItem.MediaType == "Unknown" && string.IsNullOrWhiteSpace(relatedItem.EpisodeTitle))
            {
                relatedItem.EpisodeTitle = relatedItem.TitleGuess;
            }

            TvShowIdentityMatcher.Apply(relatedItem, identity);
            var match = await client.BuildMatchAsync(selected, relatedItem);
            relatedItem.Season = match.Season ?? relatedItem.Season;
            relatedItem.Episode = match.Episode ?? relatedItem.Episode;
            relatedItem.EpisodeTitle = match.EpisodeTitle ?? relatedItem.EpisodeTitle;
            relatedItem.Status = relatedItem.Season is null || relatedItem.Episode is null
                ? "TV matched; episode needs review"
                : "TMDB show match";
            UpdateDestination(relatedItem);
        }
    }

    private TmdbCandidate? ShowMatchPicker(
        MediaPreviewItem item,
        TmdbClient client,
        string initialQuery,
        IReadOnlyList<TmdbCandidate> candidates,
        out bool useLocalGuess)
    {
        var picker = new MatchPickerWindow(item, client, initialQuery, candidates)
        {
            Owner = this
        };

        var result = picker.ShowDialog();
        useLocalGuess = picker.UseLocalGuess;
        return result == true ? picker.SelectedCandidate : null;
    }

    private RenamePreset GetPreset() => PresetComboBox.SelectedIndex switch
    {
        1 => RenamePreset.FlatReview,
        2 => RenamePreset.Custom,
        _ => RenamePreset.PlexStandard
    };

    private void SyncSelection(System.Windows.Controls.DataGrid source, System.Windows.Controls.DataGrid target)
    {
        if (_isSyncingSelection)
        {
            return;
        }

        try
        {
            _isSyncingSelection = true;
            var selectedItem = source.SelectedItem;
            target.SelectedItem = selectedItem;
            if (selectedItem is not null)
            {
                target.ScrollIntoView(selectedItem);
            }
        }
        finally
        {
            _isSyncingSelection = false;
        }
    }
}
