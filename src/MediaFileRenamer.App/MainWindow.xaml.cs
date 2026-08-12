using MediaFileRenamer.App.Services;
using MediaFileRenamer.App.ViewModels;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Forms = System.Windows.Forms;

namespace MediaFileRenamer.App;

public partial class MainWindow : Window
{
    private readonly MediaScanner _scanner = new();
    private readonly RenamePlanner _planner = new();
    private readonly OperationJournalService _journalService = new();
    private readonly RenameApplier _applier;
    private readonly TvEpisodeMetadataResolver _metadataResolver = new();
    private readonly MetadataMatchService _matchService = new();
    private readonly AppSettingsService _settingsService = new();
    private AppSettings _settings = new();
    private bool _isBusy;
    private bool _firstRunPromptShown;
    private readonly bool _showFirstRun;
    private CancellationTokenSource? _operationCancellation;

    public ObservableCollection<MediaPreviewItem> PreviewItems { get; } = [];

    public MainWindow()
    {
        _applier = new RenameApplier(_journalService);
        InitializeComponent();
        EpisodeOrderComboBox.ItemsSource = Enum.GetValues<EpisodeOrder>();
        _showFirstRun = !_settingsService.SettingsExist;
        _settings = _settingsService.Load();
        DiagnosticLog.Current.RegisterSensitiveValue(_settings.TmdbApiKey);
        DiagnosticLog.Current.RegisterSensitiveValue(_settings.TvdbApiKey);
        DiagnosticLog.Current.RegisterSensitiveValue(_settings.TvdbPin);
        OutputFolderTextBox.Text = _settings.DefaultOutputFolder;
        OperationComboBox.SelectedIndex =
            _settings.DefaultOperation == FileOperation.Copy ? 1 : 0;
        DataContext = this;
        UpdateCustomFormatVisibility();
        UpdateActionState();
    }

    protected override async void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        var recoveries = await GetStartupRecoveriesAsync();
        if (recoveries is { Count: > 0 })
        {
            ShowRecoveryCenter(recoveries);
        }

        if (!_showFirstRun || _firstRunPromptShown)
        {
            return;
        }

        _firstRunPromptShown = true;
        ShowSettings(isFirstRun: true);
    }

    private void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Multiselect = true,
            Filter = "Media files|*.mkv;*.mp4;*.m4v;*.avi;*.mov;*.wmv;*.ts;*.mpeg;*.mpg;*.m2ts;*.mts;*.webm;*.vob|All files|*.*"
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
        await RunBusyAsync(MatchAllAsync);
    }

    private async Task MatchAllAsync()
    {
        await MatchItemsAsync(PreviewItems.ToList(), retryExistingMatches: false);
    }

    private async void RetryUnresolved_Click(object sender, RoutedEventArgs e)
    {
        await RunBusyAsync(RetryUnresolvedAsync);
    }

    private async Task RetryUnresolvedAsync()
    {
        var unresolved = PreviewItems.Where(item => item.RequiresReview).ToList();
        if (unresolved.Count == 0)
        {
            StatusTextBlock.Text = "There are no unresolved files to retry.";
            return;
        }

        await MatchItemsAsync(unresolved, retryExistingMatches: true);
    }

    private async Task MatchItemsAsync(
        IReadOnlyList<MediaPreviewItem> items,
        bool retryExistingMatches)
    {
        var key = _settings.TmdbApiKey.Trim();
        var useTmdb = _settings.UseTmdbLookup && !string.IsNullOrWhiteSpace(key);
        var client = useTmdb ? new TmdbClient(key) : null;
        var tvdbClient = CreateTvdbFallback();
        var resolutionClient = client ?? new TmdbClient("");

        StatusTextBlock.Text = useTmdb
            ? retryExistingMatches
                ? $"Retrying {items.Count} unresolved file(s) with configured providers..."
                : "Matching all files with configured providers..."
            : "Planning filenames from local names; provider matching is not configured.";

        foreach (var item in items)
        {
            var rememberedMapping = FindSavedShowMapping(item);
            var canUseRememberedMapping = rememberedMapping is not null
                && IsUsableRememberedMapping(
                    rememberedMapping,
                    client is not null,
                    tvdbClient is not null);
            if ((client is not null || canUseRememberedMapping)
                && (retryExistingMatches || (item.TmdbId is null && item.TvdbId is null)))
            {
                var selected = await MatchItemAsync(
                    resolutionClient,
                    tvdbClient,
                    item,
                    showPickerForUncertain: true,
                    canSearchTmdb: client is not null);
                if (selected is not null)
                {
                    await ApplyTvShowIdentityToRelatedItemsAsync(
                        resolutionClient,
                        tvdbClient,
                        item,
                        selected);
                }
            }

            UpdateDestination(item);
        }

        StatusTextBlock.Text = FormatMatchSummary(PreviewItems);
        FocusFirstUnresolved();
    }

    private async void MatchSelected_Click(object sender, RoutedEventArgs e)
    {
        await RunBusyAsync(MatchSelectedAsync);
    }

    private async Task MatchSelectedAsync()
    {
        if (OriginalGrid.SelectedItem is not MediaPreviewItem item)
        {
            System.Windows.MessageBox.Show(this, "Select one media file before matching.", "No file selected", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var key = _settings.TmdbApiKey.Trim();
        var client = _settings.UseTmdbLookup && !string.IsNullOrWhiteSpace(key)
            ? new TmdbClient(key)
            : null;
        var tvdbClient = CreateTvdbFallback();
        var rememberedMapping = FindSavedShowMapping(item);
        if (client is null
            && (rememberedMapping is null
                || !IsUsableRememberedMapping(
                    rememberedMapping,
                    hasTmdbClient: false,
                    hasTvdbClient: tvdbClient is not null)))
        {
            System.Windows.MessageBox.Show(
                this,
                "Add your TMDB API key in File > Settings before matching, "
                + "or remember a TVDB-backed show mapping for this folder.",
                "Metadata credentials needed",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        StatusTextBlock.Text = $"Matching {item.SourceFileName}...";
        var resolutionClient = client ?? new TmdbClient("");
        var selected = await MatchItemAsync(
            resolutionClient,
            tvdbClient,
            item,
            showPickerForUncertain: true,
            canSearchTmdb: client is not null);
        if (selected is not null)
        {
            await ApplyTvShowIdentityToRelatedItemsAsync(
                resolutionClient,
                tvdbClient,
                item,
                selected);
        }

        UpdateDestination(item);
        StatusTextBlock.Text = $"Match result: {item.Status}.";
    }

    private async void ChooseSelected_Click(object sender, RoutedEventArgs e)
    {
        await RunBusyAsync(ChooseSelectedAsync);
    }

    private async Task ChooseSelectedAsync()
    {
        if (OriginalGrid.SelectedItem is not MediaPreviewItem item)
        {
            System.Windows.MessageBox.Show(this, "Select one media file before choosing a match.", "No file selected", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var key = _settings.TmdbApiKey.Trim();
        if (string.IsNullOrWhiteSpace(key))
        {
            _ = ShowMatchPicker(item, null, null, item.TitleGuess, [], out var localMediaType);
            if (localMediaType is not null)
            {
                ApplyLocalChoice(item, localMediaType);
                UpdateDestination(item);
                StatusTextBlock.Text = $"Match result: {item.Status}.";
            }
            return;
        }

        StatusTextBlock.Text = $"Loading choices for {item.SourceFileName}...";
        var client = new TmdbClient(key);
        var tvdbClient = CreateTvdbFallback();
        var selected = await MatchItemAsync(client, tvdbClient, item, showPickerForUncertain: true, alwaysShowPicker: true);
        if (selected is not null)
        {
            await ApplyTvShowIdentityToRelatedItemsAsync(client, tvdbClient, item, selected);
        }

        UpdateDestination(item);
        StatusTextBlock.Text = $"Match result: {item.Status}.";
    }

    private async void ResolveReview_Click(object sender, RoutedEventArgs e)
    {
        await RunBusyAsync(ResolveSelectedReviewAsync);
    }

    private async Task ResolveSelectedReviewAsync()
    {
        if (OriginalGrid.SelectedItem is not MediaPreviewItem item)
        {
            return;
        }

        var key = _settings.TmdbApiKey.Trim();
        if (item.MediaType != "TV"
            || string.IsNullOrWhiteSpace(key)
            || (item.TmdbId is null && item.TvdbId is null))
        {
            await ChooseSelectedAsync();
            return;
        }

        StatusTextBlock.Text = $"Loading episodes for {item.MatchedTitle}...";
        var client = new TmdbClient(key);
        var tmdbId = item.TmdbId;
        if (tmdbId is null && item.TvdbId is not null)
        {
            var show = await client.FindTvByTvdbIdAsync(
                item.TvdbId.Value,
                _operationCancellation?.Token ?? CancellationToken.None);
            tmdbId = show?.TmdbId;
            item.TmdbId = tmdbId;
        }

        if (tmdbId is null)
        {
            StatusTextBlock.Text =
                "This show has no TMDB identity for episode browsing. "
                + "Edit and confirm the episode details, or choose a different show.";
            return;
        }

        var choices = await client.GetEpisodeChoicesAsync(
            tmdbId.Value,
            _operationCancellation?.Token ?? CancellationToken.None);
        if (choices.Count == 0)
        {
            StatusTextBlock.Text =
                "No episodes were returned for this show. "
                + "Edit and confirm the episode details, or choose a different show.";
            return;
        }

        var picker = new EpisodePickerWindow(item, choices)
        {
            Owner = this
        };
        if (picker.ShowDialog() != true || picker.SelectedChoice is null)
        {
            StatusTextBlock.Text = "Episode selection canceled; no details were changed.";
            return;
        }

        var selected = picker.SelectedChoice;
        item.Season = selected.Season;
        item.Episode = selected.Episode;
        item.EpisodeTitle = selected.Title;
        item.Status = "Manual TMDB episode choice";
        UpdateDestination(item);
        StatusTextBlock.Text =
            $"Resolved {item.SourceFileName} as {selected.DisplayLabel}.";
        FocusFirstUnresolved();
    }

    private void ConfirmManualDetails_Click(object sender, RoutedEventArgs e)
    {
        if (OriginalGrid.SelectedItem is not MediaPreviewItem item)
        {
            return;
        }

        if (!TryConfirmManualDetails(item, out var validationMessage))
        {
            System.Windows.MessageBox.Show(
                this,
                validationMessage,
                "Complete the media details",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        UpdateDestination(item);
        StatusTextBlock.Text = $"Confirmed the edited details for {item.SourceFileName}.";
        UpdateActionState();
        FocusFirstUnresolved();
    }

    internal static bool TryConfirmManualDetails(
        MediaPreviewItem item,
        out string validationMessage)
    {
        if (string.IsNullOrWhiteSpace(item.MatchedTitle))
        {
            validationMessage = "Enter a title before confirming this file.";
            return false;
        }

        if (item.MediaType == "TV")
        {
            if (item.Season is null || item.Season < 0)
            {
                validationMessage = "Enter a season number of 0 or greater.";
                return false;
            }

            if (item.Episode is null || item.Episode <= 0)
            {
                validationMessage = "Enter an episode number greater than 0.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(item.EpisodeTitle))
            {
                validationMessage = "Enter an episode title before confirming this TV episode.";
                return false;
            }

            item.Status = "Manual TV details confirmed";
            validationMessage = "";
            return true;
        }

        if (item.MediaType == "Movie")
        {
            item.Status = "Manual movie details confirmed";
            validationMessage = "";
            return true;
        }

        validationMessage = "Choose Movie or TV in the Type field before confirming.";
        return false;
    }

    private bool FocusFirstUnresolved()
    {
        var unresolved = PreviewItems.FirstOrDefault(item => item.RequiresReview);
        if (unresolved is null)
        {
            return false;
        }

        OriginalGrid.SelectedItem = unresolved;
        OriginalGrid.ScrollIntoView(unresolved);
        return true;
    }

    private async void ApplyRename_Click(object sender, RoutedEventArgs e)
    {
        await RunBusyAsync(ApplyRenameAsync);
    }

    private async Task ApplyRenameAsync()
    {
        if (PreviewItems.Count == 0)
        {
            System.Windows.MessageBox.Show(this, "Add files or a folder before applying a rename.", "Nothing to rename", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var operation = OperationComboBox.SelectedIndex == 0 ? FileOperation.Move : FileOperation.Copy;
        RefreshDestinations();
        var firstUnresolved = PreviewItems.FirstOrDefault(item => item.RequiresReview);
        if (firstUnresolved is not null)
        {
            OriginalGrid.SelectedItem = firstUnresolved;
            OriginalGrid.ScrollIntoView(firstUnresolved);
            StatusTextBlock.Text = "Review every flagged item before applying this batch.";
            System.Windows.MessageBox.Show(
                this,
                $"{PreviewItems.Count(item => item.RequiresReview)} item(s) still need review. "
                + "Choose or match each flagged item before applying; no files were moved or copied.",
                "Review required",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var companionCount = PreviewItems.Sum(item => item.CompanionCount);
        var totalBytes = PreviewItems
            .SelectMany(item => new[] { item.SourcePath }.Concat(item.CompanionPaths))
            .Where(File.Exists)
            .Sum(path => new FileInfo(path).Length);
        var confirmation = System.Windows.MessageBox.Show(
            this,
            $"{operation} {PreviewItems.Count} media file(s)"
            + (companionCount > 0 ? $" and {companionCount} companion file(s)" : "")
            + $" ({FormatBytes(totalBytes)})?\n\nDestination:\n{OutputFolderTextBox.Text}\n\n"
            + "The batch will be preflighted and rolled back if a transfer fails.",
            "Confirm staged operation",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question);
        if (confirmation != MessageBoxResult.OK)
        {
            StatusTextBlock.Text = "Operation canceled before any files were changed.";
            return;
        }

        StatusTextBlock.Text = "Preflighting staged operation...";
        OperationProgressBar.IsIndeterminate = false;
        OperationProgressBar.Value = 0;
        var transferProgress = new Progress<FileTransferProgress>(value =>
        {
            OperationProgressBar.Value = value.TotalBytes == 0
                ? 0
                : Math.Clamp(value.BytesTransferred * 100d / value.TotalBytes, 0, 100);
            StatusTextBlock.Text =
                $"{operation}: {value.CompletedFiles}/{value.TotalFiles} files · {value.CurrentFile}";
        });
        var result = await _applier.ApplyAsync(
            PreviewItems,
            operation,
            transferProgress,
            _operationCancellation?.Token ?? CancellationToken.None);

        if (!string.IsNullOrWhiteSpace(result.FailureMessage))
        {
            DiagnosticLog.Current.Warning(result.FailureMessage);
            StatusTextBlock.Text = result.FailureMessage;
            UpdateActionState();
            return;
        }

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
        OriginalGrid.SelectedItem = null;
        PreviewItems.Clear();

        StatusTextBlock.Text = "Cleared.";
        UpdateActionState();
    }

    private async Task RunBusyAsync(Func<Task> action)
    {
        if (_isBusy)
        {
            return;
        }

        _isBusy = true;
        _operationCancellation = new CancellationTokenSource();
        SetActionButtonsEnabled(false);
        CancelOperationButton.IsEnabled = true;
        CancelOperationButton.Visibility = Visibility.Visible;
        OperationProgressBar.Visibility = Visibility.Visible;
        OperationProgressBar.IsIndeterminate = true;
        try
        {
            await action();
        }
        catch (MetadataLookupException ex)
        {
            DiagnosticLog.Current.Warning(ex.Message);
            StatusTextBlock.Text = ex.Message;
        }
        catch (OperationCanceledException)
        {
            StatusTextBlock.Text = "Operation canceled.";
        }
        catch (Exception ex)
        {
            DiagnosticLog.Current.Error("An operation failed.", ex);
            StatusTextBlock.Text = $"Operation failed: {ex.Message}";
        }
        finally
        {
            _operationCancellation.Dispose();
            _operationCancellation = null;
            _isBusy = false;
            CancelOperationButton.Visibility = Visibility.Collapsed;
            OperationProgressBar.Visibility = Visibility.Collapsed;
            OperationProgressBar.IsIndeterminate = false;
            UpdateActionState();
        }
    }

    private void CancelOperation_Click(object sender, RoutedEventArgs e)
    {
        _operationCancellation?.Cancel();
        StatusTextBlock.Text = "Cancel requested; completing safe rollback if needed...";
        CancelOperationButton.IsEnabled = false;
    }

    private void SetActionButtonsEnabled(bool enabled)
    {
        AddFilesButton.IsEnabled = enabled;
        AddFolderButton.IsEnabled = enabled;
        ClearButton.IsEnabled = enabled;
        MatchAllButton.IsEnabled = enabled;
        MatchSelectedButton.IsEnabled = enabled;
        ChooseSelectedButton.IsEnabled = enabled;
        RetryUnresolvedButton.IsEnabled = enabled;
        RenameButton.IsEnabled = enabled;
        ReviewGuidancePanel.IsEnabled = enabled;
    }

    private void UpdateActionState()
    {
        if (_isBusy
            || OriginalGrid is null
            || AddFilesButton is null
            || AddFolderButton is null
            || ClearButton is null
            || MatchAllButton is null
            || MatchSelectedButton is null
            || ChooseSelectedButton is null
            || RetryUnresolvedButton is null
            || RenameButton is null
            || ReviewCountTextBlock is null
            || EmptyDropPanel is null
            || SelectedFileEditor is null
            || RememberShowMappingButton is null
            || RememberShowMappingStatusTextBlock is null
            || ReviewGuidancePanel is null
            || ResolveReviewButton is null)
        {
            return;
        }

        var hasItems = PreviewItems.Count > 0;
        var selectedItem = OriginalGrid.SelectedItem as MediaPreviewItem;
        var hasSelection = selectedItem is not null;
        var reviewCount = PreviewItems.Count(item => item.RequiresReview);
        AddFilesButton.IsEnabled = true;
        AddFolderButton.IsEnabled = true;
        ClearButton.IsEnabled = hasItems;
        MatchAllButton.IsEnabled = hasItems;
        MatchSelectedButton.IsEnabled = hasSelection;
        ChooseSelectedButton.IsEnabled = hasSelection;
        RetryUnresolvedButton.IsEnabled = hasItems && reviewCount > 0;
        RenameButton.IsEnabled = hasItems
            && reviewCount == 0
            && PreviewItems.All(item => !string.IsNullOrWhiteSpace(item.DestinationPath));
        RenameButton.ToolTip = reviewCount > 0
            ? "Resolve every item marked Review needed, Ready to match, or Blocked first."
            : null;
        EmptyDropPanel.Visibility = hasItems ? Visibility.Collapsed : Visibility.Visible;
        SelectedFileEditor.Visibility = hasSelection ? Visibility.Visible : Visibility.Collapsed;
        ReviewGuidancePanel.IsEnabled = true;
        var canBrowseEpisodes = selectedItem?.MediaType == "TV"
            && (selectedItem.TmdbId is not null || selectedItem.TvdbId is not null)
            && _settings.UseTmdbLookup
            && !string.IsNullOrWhiteSpace(_settings.TmdbApiKey);
        ResolveReviewButton.Content = canBrowseEpisodes
            ? "Browse Episodes..."
            : "Choose Show...";
        UpdateRememberedShowMappingState(selectedItem);

        var operation = OperationComboBox?.SelectedIndex == 1 ? "Copy" : "Move";
        var fileNoun = PreviewItems.Count == 1 ? "file" : "files";
        RenameButton.Content = $"{operation} {PreviewItems.Count} {fileNoun}";
        AutomationProperties.SetName(
            RenameButton,
            $"{operation} {PreviewItems.Count} reviewed {fileNoun}");

        ReviewCountTextBlock.Text = !hasItems
            ? "Add files to begin"
            : reviewCount == 0
                ? "All items reviewed"
                : reviewCount == 1
                    ? "1 item needs review"
                    : $"{reviewCount} items need review";
        ReviewCountTextBlock.Foreground = (System.Windows.Media.Brush)FindResource(
            !hasItems ? "MutedTextBrush" : reviewCount == 0 ? "MatchBrush" : "ReviewBrush");
    }

    private void RemoveCompletedItems(IEnumerable<MediaPreviewItem> completedItems)
    {
        OriginalGrid.SelectedItem = null;
        foreach (var item in completedItems.ToList())
        {
            PreviewItems.Remove(item);
        }

        if (PreviewItems.Count > 0)
        {
            OriginalGrid.SelectedItem = PreviewItems[0];
        }
        UpdateActionState();
    }

    private void SettingsMenuItem_Click(object sender, RoutedEventArgs e)
    {
        ShowSettings(isFirstRun: false);
    }

    private void RememberShowMapping_Click(object sender, RoutedEventArgs e)
    {
        if (OriginalGrid.SelectedItem is not MediaPreviewItem item
            || !CanRememberShowMapping(item))
        {
            RememberShowMappingStatusTextBlock.Text =
                "Choose a confirmed TV match before remembering this folder.";
            return;
        }

        try
        {
            var mapping = _settingsService.UpsertShowMapping(
                new SavedShowMapping
                {
                    SourcePath = item.SourceGroupPath,
                    Title = item.MatchedTitle,
                    MediaType = item.MediaType,
                    TmdbId = item.TmdbId,
                    TvdbId = item.TvdbId,
                    EpisodeOrder = item.EpisodeOrder
                });
            _settings = _settingsService.Load();
            RememberShowMappingStatusTextBlock.Text =
                $"Remembered {mapping.Title} for {mapping.SourcePath}.";
            RememberShowMappingButton.Content = "Update remembered show";
            DiagnosticLog.Current.Information(
                "A show mapping was remembered for a source folder.");
        }
        catch (Exception ex) when (
            ex is ArgumentException
                or IOException
                or UnauthorizedAccessException)
        {
            DiagnosticLog.Current.Error(
                "Could not save a remembered show mapping.",
                ex);
            RememberShowMappingStatusTextBlock.Text =
                "The show mapping could not be saved. Check the settings folder and try again.";
        }
    }

    private void UpdateRememberedShowMappingState(MediaPreviewItem? item)
    {
        if (item is null)
        {
            RememberShowMappingButton.IsEnabled = false;
            RememberShowMappingStatusTextBlock.Text = "";
            return;
        }

        var existing = FindSavedShowMapping(item);
        RememberShowMappingButton.IsEnabled = CanRememberShowMapping(item);
        RememberShowMappingButton.Content = existing is null
            ? "Always use this show for this folder"
            : "Update remembered show";
        RememberShowMappingStatusTextBlock.Text = existing is null
            ? CanRememberShowMapping(item)
                ? "Remember this confirmed match to skip future series searches in this folder."
                : "A confirmed TV provider match is required before this folder can be remembered."
            : $"Remembered as {existing.Title} using {existing.EpisodeOrder} order.";
    }

    private static bool CanRememberShowMapping(MediaPreviewItem item)
    {
        return string.Equals(item.MediaType, "TV", StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(item.MatchedTitle)
            && !string.IsNullOrWhiteSpace(item.SourceGroupPath)
            && (item.TmdbId is > 0 || item.TvdbId is > 0);
    }

    private void ShowSettings(bool isFirstRun)
    {
        var window = new SettingsWindow(
            _settings,
            _settingsService.SettingsPath,
            isFirstRun: isFirstRun)
        {
            Owner = this
        };

        if (window.ShowDialog() == true)
        {
            _settings = window.Settings;
            _settingsService.Save(_settings);
            DiagnosticLog.Current.RegisterSensitiveValue(_settings.TmdbApiKey);
            DiagnosticLog.Current.RegisterSensitiveValue(_settings.TvdbApiKey);
            DiagnosticLog.Current.RegisterSensitiveValue(_settings.TvdbPin);
            OutputFolderTextBox.Text = _settings.DefaultOutputFolder;
            OperationComboBox.SelectedIndex =
                _settings.DefaultOperation == FileOperation.Copy ? 1 : 0;
            RefreshDestinations();
            StatusTextBlock.Text = isFirstRun
                ? "Setup complete. Add a small test batch to begin."
                : "Settings saved.";
        }
        else if (isFirstRun)
        {
            StatusTextBlock.Text =
                "First-run setup was skipped. Open File > Settings when you are ready.";
        }
    }

    private void OperationHistoryMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var window = new OperationHistoryWindow(_journalService)
        {
            Owner = this
        };
        window.ShowDialog();
    }

    private void RecoveryCenterMenuItem_Click(object sender, RoutedEventArgs e)
    {
        ShowRecoveryCenter();
    }

    private void ShowRecoveryCenter(
        IReadOnlyList<InterruptedOperationRecovery>? initialRecoveries = null)
    {
        var window = new RecoveryWindow(_journalService, initialRecoveries)
        {
            Owner = this
        };
        window.ShowDialog();
        if (window.Recoveries.Count == 0)
        {
            StatusTextBlock.Text = "Interrupted operations have been reviewed.";
        }
    }

    private async Task<IReadOnlyList<InterruptedOperationRecovery>?>
        GetStartupRecoveriesAsync()
    {
        var inspection = Task.Run(_journalService.GetInterruptedOperations);
        var completed = await Task.WhenAny(
            inspection,
            Task.Delay(TimeSpan.FromSeconds(5)));
        if (completed != inspection)
        {
            _ = inspection.ContinueWith(
                task => DiagnosticLog.Current.Error(
                    "The background recovery inspection failed.",
                    task.Exception),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
            DiagnosticLog.Current.Warning(
                "Recovery inspection exceeded the startup time limit.");
            StatusTextBlock.Text =
                "Recovery inspection is taking longer than expected. "
                + "The app remains usable; open File > Recovery Center after network paths are available.";
            return null;
        }

        try
        {
            return await inspection;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Current.Error("Could not inspect interrupted operations.", ex);
            StatusTextBlock.Text =
                "Recovery journals could not be inspected. Open File > Recovery Center to retry.";
            return null;
        }
    }

    private void AboutMenuItem_Click(object sender, RoutedEventArgs e)
    {
        new AboutWindow
        {
            Owner = this
        }.ShowDialog();
    }

    private void UndoLastOperationMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var confirmation = System.Windows.MessageBox.Show(
            this,
            "Undo the most recent completed move or copy operation? "
            + "Undo will stop if any original path is now occupied.",
            "Undo last operation",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question);
        if (confirmation != MessageBoxResult.OK)
        {
            return;
        }

        var result = _journalService.UndoLastCompleted();
        StatusTextBlock.Text = result.Message;
        System.Windows.MessageBox.Show(
            this,
            result.Message,
            result.Success ? "Undo complete" : "Undo unavailable",
            MessageBoxButton.OK,
            result.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    private void ExitMenuItem_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void PresetComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateCustomFormatVisibility();
        RefreshDestinations();
    }

    private void OperationComboBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        UpdateActionState();
    }

    private void UpdateCustomFormatVisibility()
    {
        if (CustomFormatLabel is null || CustomFormatTextBox is null || PresetComboBox is null)
        {
            return;
        }

        var visibility = GetPreset() == RenamePreset.Custom ? Visibility.Visible : Visibility.Collapsed;
        CustomFormatLabel.Visibility = visibility;
        CustomFormatTextBox.Visibility = visibility;
    }

    private void CustomFormatTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        RefreshDestinations();
    }

    private void ReviewFilterComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized)
        {
            return;
        }

        ICollectionView view = CollectionViewSource.GetDefaultView(PreviewItems);
        view.Filter = ReviewFilterComboBox.SelectedIndex switch
        {
            1 => item => item is MediaPreviewItem preview && preview.RequiresReview,
            2 => item => item is MediaPreviewItem preview && preview.MatchState == "Matched",
            3 => item => item is MediaPreviewItem preview && preview.MatchState == "Blocked",
            _ => null
        };
        view.Refresh();
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
        UpdateActionState();
    }

    private async void MatchRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { DataContext: MediaPreviewItem item })
        {
            OriginalGrid.SelectedItem = item;
            OriginalGrid.ScrollIntoView(item);
            await RunBusyAsync(MatchSelectedAsync);
        }
    }

    private async void ChooseRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { DataContext: MediaPreviewItem item })
        {
            OriginalGrid.SelectedItem = item;
            OriginalGrid.ScrollIntoView(item);
            await RunBusyAsync(ResolveSelectedReviewAsync);
        }
    }

    private void Window_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
        {
            if (e.Data.GetData(System.Windows.DataFormats.FileDrop) is string[] paths)
            {
                AddSources(paths);
            }
        }
    }

    private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _isBusy)
        {
            CancelOperation_Click(sender, e);
            e.Handled = true;
            return;
        }

        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0 || _isBusy)
        {
            return;
        }

        if (e.Key == Key.O && (Keyboard.Modifiers & ModifierKeys.Shift) != 0)
        {
            AddFolder_Click(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.O)
        {
            AddFiles_Click(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.M && MatchAllButton.IsEnabled)
        {
            MatchAll_Click(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && RenameButton.IsEnabled)
        {
            ApplyRename_Click(sender, e);
            e.Handled = true;
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
                else if (args.PropertyName is nameof(MediaPreviewItem.MatchState)
                         or nameof(MediaPreviewItem.RequiresReview))
                {
                    UpdateActionState();
                    CollectionViewSource.GetDefaultView(PreviewItems).Refresh();
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
        UpdateActionState();
    }

    private void RefreshDestinations()
    {
        foreach (var item in PreviewItems)
        {
            UpdateDestination(item);
        }
        UpdateActionState();
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
            UpdateActionState();
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            item.DestinationPath = "";
            item.Status = $"Invalid destination: {ex.Message}";
            UpdateActionState();
            return false;
        }
    }

    private async Task<TmdbCandidate?> MatchItemAsync(
        TmdbClient client,
        TvdbClient? tvdbClient,
        MediaPreviewItem item,
        bool showPickerForUncertain,
        bool alwaysShowPicker = false,
        bool canSearchTmdb = true)
    {
        var cancellationToken = _operationCancellation?.Token ?? CancellationToken.None;
        var rememberedMapping = alwaysShowPicker ? null : FindSavedShowMapping(item);
        if (rememberedMapping is not null
            && IsUsableRememberedMapping(
                rememberedMapping,
                canSearchTmdb,
                tvdbClient is not null))
        {
            item.EpisodeOrder = rememberedMapping.EpisodeOrder;
            var rememberedCandidate = CreateRememberedCandidate(
                rememberedMapping,
                includeTmdbId: canSearchTmdb);
            var rememberedResolution = await _metadataResolver.ResolveAsync(
                client,
                tvdbClient,
                rememberedCandidate,
                item,
                cancellationToken);
            var rememberedMatch = rememberedResolution.Match;
            item.MediaType = rememberedMapping.MediaType;
            item.TmdbId = rememberedMapping.TmdbId ?? rememberedMatch.TmdbId;
            item.TvdbId = rememberedMapping.TvdbId ?? rememberedMatch.TvdbId;
            item.MatchedTitle = rememberedMapping.Title;
            item.Year = rememberedMatch.Year ?? item.Year;
            item.Season = rememberedMatch.Season ?? item.Season;
            item.Episode = rememberedMatch.Episode ?? item.Episode;
            item.EpisodeTitle =
                rememberedMatch.EpisodeTitle ?? item.EpisodeTitle;
            item.Status = item.Season is null || item.Episode is null
                ? "TV matched; episode number needs review"
                : string.IsNullOrWhiteSpace(item.EpisodeTitle)
                    ? "TV matched; episode title needs review"
                    : rememberedResolution.EpisodeSource
                        == EpisodeMetadataSource.Tvdb
                        ? "TVDB remembered folder match"
                        : "TMDB remembered folder match";
            RememberShowMappingStatusTextBlock.Text =
                $"Used the remembered mapping for {rememberedMapping.Title}.";
            return rememberedCandidate;
        }

        if (!canSearchTmdb)
        {
            item.Status = "Needs review";
            return null;
        }

        var primaryQuery = item.TitleGuess;
        var primaryResult = await _matchService.SearchAsync(
            item,
            client,
            tvdbClient,
            _settings.AutoMatchConfidencePercent,
            primaryQuery,
            cancellationToken);
        var candidates = primaryResult.Candidates;
        var initialQuery = primaryQuery;

        TmdbCandidate? selected = null;
        TmdbAutoMatch? primaryAutoMatch = null;
        int? autoConfidence = null;
        if (!alwaysShowPicker)
        {
            primaryAutoMatch = primaryResult.AutoMatch;
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
                var parentResult = await _matchService.SearchAsync(
                    item,
                    client,
                    tvdbClient,
                    _settings.AutoMatchConfidencePercent,
                    parentQuery,
                    cancellationToken);
                var parentCandidates = parentResult.Candidates;
                if (parentCandidates.Count > 0)
                {
                    parentEvidenceFound = true;
                    initialQuery = parentQuery;
                    candidates = MetadataMatchService.MergeCandidates(parentCandidates, candidates);

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
            selected = ShowMatchPicker(
                item,
                client,
                tvdbClient,
                initialQuery,
                candidates,
                out var localMediaType);
            if (localMediaType is not null)
            {
                ApplyLocalChoice(item, localMediaType);
                return null;
            }
        }

        if (selected is null)
        {
            item.Status = "Needs review";
            return null;
        }

        var resolution = await _metadataResolver.ResolveAsync(
            client,
            tvdbClient,
            selected,
            item,
            cancellationToken);
        var match = resolution.Match;
        item.MediaType = selected.MediaType;
        item.TmdbId = match.TmdbId;
        item.TvdbId = match.TvdbId;
        item.MatchedTitle = match.Title;
        item.Year = match.Year ?? item.Year;
        item.Season = match.Season ?? item.Season;
        item.Episode = match.Episode ?? item.Episode;
        item.EpisodeTitle = match.EpisodeTitle ?? item.EpisodeTitle;
        item.Status = selected.MediaType == "TV" && (item.Season is null || item.Episode is null)
            ? "TV matched; episode number needs review"
                : selected.MediaType == "TV" && string.IsNullOrWhiteSpace(match.EpisodeTitle)
                    ? "TV matched; episode title needs review"
                    : resolution.EpisodeSource == EpisodeMetadataSource.Tvdb
                        ? "TVDB episode fallback"
                        : alwaysShowPicker
                            ? $"Manual {selected.ProviderLabel} match"
                            : autoConfidence is null
                                ? $"{selected.ProviderLabel} match"
                                : $"{selected.ProviderLabel} auto {autoConfidence}%";
        return selected;
    }

    private SavedShowMapping? FindSavedShowMapping(MediaPreviewItem item)
    {
        if (string.IsNullOrWhiteSpace(item.SourceGroupPath))
        {
            return null;
        }

        string identity;
        try
        {
            identity = AppSettingsService.NormalizeSourceIdentity(
                item.SourceGroupPath);
        }
        catch (Exception ex) when (
            ex is ArgumentException
                or NotSupportedException
                or PathTooLongException)
        {
            return null;
        }

        return _settings.SavedShowMappings.FirstOrDefault(mapping =>
            string.Equals(
                mapping.SourceIdentity,
                identity,
                StringComparison.OrdinalIgnoreCase));
    }

    internal static bool IsUsableRememberedMapping(
        SavedShowMapping mapping,
        bool hasTmdbClient,
        bool hasTvdbClient)
    {
        return string.Equals(mapping.MediaType, "TV", StringComparison.Ordinal)
            && ((hasTmdbClient && mapping.TmdbId is not null)
                || (hasTvdbClient && mapping.TvdbId is not null));
    }

    internal static TmdbCandidate CreateRememberedCandidate(
        SavedShowMapping mapping,
        bool includeTmdbId)
    {
        var tmdbId = includeTmdbId ? mapping.TmdbId : null;
        var provider = tmdbId is not null && mapping.TvdbId is not null
            ? MetadataProvider.TmdbAndTvdb
            : tmdbId is not null
                ? MetadataProvider.Tmdb
                : MetadataProvider.Tvdb;
        return new TmdbCandidate(
            tmdbId ?? 0,
            mapping.MediaType,
            mapping.Title,
            null,
            "",
            "",
            "")
        {
            TmdbId = tmdbId,
            TvdbId = mapping.TvdbId,
            Provider = provider
        };
    }

    private TvdbClient? CreateTvdbFallback()
    {
        var key = _settings.TvdbApiKey.Trim();
        return _settings.UseTvdbFallback && !string.IsNullOrWhiteSpace(key)
            ? new TvdbClient(key, _settings.TvdbPin)
            : null;
    }

    private async Task ApplyTvShowIdentityToRelatedItemsAsync(
        TmdbClient client,
        TvdbClient? tvdbClient,
        MediaPreviewItem matchedItem,
        TmdbCandidate selected)
    {
        if (matchedItem.MediaType != "TV" || selected.MediaType != "TV")
        {
            return;
        }

        var cancellationToken = _operationCancellation?.Token ?? CancellationToken.None;
        var identity = TvShowIdentityMatcher.FromCandidate(selected);
        foreach (var relatedItem in PreviewItems.Where(item => item != matchedItem && TvShowIdentityMatcher.IsRelatedEpisode(matchedItem, item)))
        {
            if (relatedItem.MediaType == "Unknown" && string.IsNullOrWhiteSpace(relatedItem.EpisodeTitle))
            {
                relatedItem.EpisodeTitle = relatedItem.TitleGuess;
            }

            TvShowIdentityMatcher.Apply(relatedItem, identity);
            relatedItem.EpisodeOrder = matchedItem.EpisodeOrder;
            var resolution = await _metadataResolver.ResolveAsync(
                client,
                tvdbClient,
                selected,
                relatedItem,
                cancellationToken);
            var match = resolution.Match;
            relatedItem.TmdbId = match.TmdbId;
            relatedItem.TvdbId = match.TvdbId;
            relatedItem.Season = match.Season ?? relatedItem.Season;
            relatedItem.Episode = match.Episode ?? relatedItem.Episode;
            relatedItem.EpisodeTitle = match.EpisodeTitle ?? relatedItem.EpisodeTitle;
            relatedItem.Status = relatedItem.Season is null || relatedItem.Episode is null
                ? "TV matched; episode number needs review"
                : string.IsNullOrWhiteSpace(match.EpisodeTitle)
                    ? "TV matched; episode title needs review"
                    : resolution.EpisodeSource == EpisodeMetadataSource.Tvdb
                        ? "TVDB episode fallback"
                        : "TMDB show match";
            UpdateDestination(relatedItem);
        }
    }

    private static void ApplyLocalChoice(MediaPreviewItem item, string localMediaType)
    {
        item.MediaType = localMediaType;
        if (localMediaType == "TV" && string.IsNullOrWhiteSpace(item.EpisodeTitle))
        {
            item.EpisodeTitle = item.TitleGuess;
        }
        item.Status = localMediaType == "TV" ? "Local TV choice" : "Local movie choice";
    }

    private static string FormatBytes(long bytes)
    {
        var units = new[] { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.##} {units[unit]}";
    }

    internal static string FormatMatchSummary(IEnumerable<MediaPreviewItem> items)
    {
        var materialized = items.ToList();
        var failed = materialized.Count(item => item.MatchState == "Blocked");
        var review = materialized.Count(item =>
            item.RequiresReview && item.MatchState != "Blocked");
        var matched = materialized.Count - review - failed;
        return $"Match complete: {matched} matched/planned, "
            + $"{review} need review, {failed} failed.";
    }

    private TmdbCandidate? ShowMatchPicker(
        MediaPreviewItem item,
        TmdbClient? client,
        TvdbClient? tvdbClient,
        string initialQuery,
        IReadOnlyList<TmdbCandidate> candidates,
        out string? localMediaType)
    {
        var picker = new MatchPickerWindow(
            item,
            client,
            initialQuery,
            candidates,
            tvdbClient,
            _matchService,
            _settings.AutoMatchConfidencePercent)
        {
            Owner = this
        };

        var result = picker.ShowDialog();
        localMediaType = picker.LocalMediaType;
        return result == true ? picker.SelectedCandidate : null;
    }

    private RenamePreset GetPreset() => PresetComboBox.SelectedIndex switch
    {
        1 => RenamePreset.FlatReview,
        2 => RenamePreset.Custom,
        _ => RenamePreset.PlexStandard
    };

}
