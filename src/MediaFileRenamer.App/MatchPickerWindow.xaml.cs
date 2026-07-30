using MediaFileRenamer.App.Services;
using MediaFileRenamer.App.ViewModels;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;

namespace MediaFileRenamer.App;

public partial class MatchPickerWindow : Window
{
    private readonly MediaPreviewItem _item;
    private readonly TmdbClient? _client;
    private readonly TvdbClient? _tvdbClient;
    private readonly MetadataMatchService _matchService;
    private readonly int _confidenceThreshold;

    public ObservableCollection<TmdbCandidate> Candidates { get; }
    public TmdbCandidate? SelectedCandidate { get; private set; }
    public string? LocalMediaType { get; private set; }

    public MatchPickerWindow(
        MediaPreviewItem item,
        TmdbClient? client,
        string initialQuery,
        IEnumerable<TmdbCandidate> candidates,
        TvdbClient? tvdbClient = null,
        MetadataMatchService? matchService = null,
        int confidenceThreshold = 92)
    {
        InitializeComponent();
        _item = item;
        _client = client;
        _tvdbClient = tvdbClient;
        _matchService = matchService ?? new MetadataMatchService();
        _confidenceThreshold = confidenceThreshold;
        Candidates = new ObservableCollection<TmdbCandidate>(candidates);
        DataContext = this;
        SourceTextBlock.Text = item.SourcePath;
        SearchTextBox.Text = initialQuery;

        UpdateCandidateState();
        if (_client is null)
        {
            ConfigureLocalClassificationMode();
            SearchTextBox.IsEnabled = false;
            SearchButton.IsEnabled = false;
            SearchStatusTextBlock.Text = "TMDB search is unavailable. Choose Use as Movie or Use as TV to classify this file locally.";
        }
    }

    private void ConfigureLocalClassificationMode()
    {
        Title = "Classify Media";
        Width = 760;
        Height = 480;
        PickerContentBorder.MinWidth = 640;
        PickerContentBorder.MinHeight = 410;
        OnlineSearchPanel.Visibility = Visibility.Collapsed;
        ProviderResultsGrid.Visibility = Visibility.Collapsed;
        OnlineLocalChoicePanel.Visibility = Visibility.Collapsed;
        OnlineSelectionHint.Visibility = Visibility.Collapsed;
        UseSelectedButton.Visibility = Visibility.Collapsed;
        LocalClassificationPanel.Visibility = Visibility.Visible;
        LocalMovieButton.IsDefault = true;
    }

    private void UseSelected_Click(object sender, RoutedEventArgs e)
    {
        SelectedCandidate = CandidateList.SelectedItem as TmdbCandidate;
        DialogResult = SelectedCandidate is not null;
    }

    private void UseAsMovie_Click(object sender, RoutedEventArgs e)
    {
        LocalMediaType = "Movie";
        DialogResult = true;
    }

    private void UseAsTv_Click(object sender, RoutedEventArgs e)
    {
        LocalMediaType = "TV";
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void CandidateList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        UseSelected_Click(sender, e);
    }

    private void CandidateList_SelectionChanged(
        object sender,
        System.Windows.Controls.SelectionChangedEventArgs e)
    {
        UpdateSelectionEvidence();
    }

    private async void Search_Click(object sender, RoutedEventArgs e)
    {
        await SearchAsync();
    }

    private async void SearchTextBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            await SearchAsync();
        }
    }

    private async Task SearchAsync()
    {
        if (_client is null)
        {
            SearchStatusTextBlock.Text = "Add a TMDB API key in Settings to search for matches.";
            return;
        }

        var query = SearchTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            SearchStatusTextBlock.Text = "Enter a movie or show title.";
            return;
        }

        SearchStatusTextBlock.Text = _tvdbClient is null
            ? "Searching TMDB..."
            : "Searching TMDB and TVDB...";
        IReadOnlyList<TmdbCandidate> results;
        try
        {
            var search = await _matchService.SearchAsync(
                _item,
                _client,
                _tvdbClient,
                _confidenceThreshold,
                query);
            results = search.Candidates;
        }
        catch (MetadataLookupException ex)
        {
            SearchStatusTextBlock.Text = ex.Message;
            return;
        }

        Candidates.Clear();
        foreach (var candidate in results)
        {
            Candidates.Add(candidate);
        }

        UpdateCandidateState();
    }

    private void UpdateCandidateState()
    {
        if (Candidates.Count > 0)
        {
            CandidateList.SelectedIndex = 0;
            SearchStatusTextBlock.Text = "";
        }
        else
        {
            SearchStatusTextBlock.Text = "No matches found. Edit the search above and try a simpler title.";
        }

        UseSelectedButton.IsEnabled = CandidateList.SelectedItem is not null;
        UpdateSelectionEvidence();
    }

    private void UpdateSelectionEvidence()
    {
        if (CandidateList.SelectedItem is not TmdbCandidate candidate)
        {
            CandidateEvidenceTextBlock.Text = "Select a result to see why it matches.";
            if (UseSelectedButton is not null)
            {
                UseSelectedButton.IsEnabled = false;
            }
            return;
        }

        var score = TmdbClient.ScoreCandidate(
            _item,
            candidate,
            SearchTextBox.Text.Trim());
        var evidence = score.Evidence.Count == 0
            ? "No exact title or year evidence"
            : string.Join(" • ", score.Evidence);
        CandidateEvidenceTextBlock.Text =
            $"{score.ConfidencePercent}% confidence • {evidence}";
        UseSelectedButton.IsEnabled = true;
    }
}
