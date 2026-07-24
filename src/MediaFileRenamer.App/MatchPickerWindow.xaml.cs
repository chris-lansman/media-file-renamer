using MediaFileRenamer.App.Services;
using MediaFileRenamer.App.ViewModels;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;

namespace MediaFileRenamer.App;

public partial class MatchPickerWindow : Window
{
    private readonly MediaPreviewItem _item;
    private readonly TmdbClient _client;

    public ObservableCollection<TmdbCandidate> Candidates { get; }
    public TmdbCandidate? SelectedCandidate { get; private set; }
    public bool UseLocalGuess { get; private set; }

    public MatchPickerWindow(
        MediaPreviewItem item,
        TmdbClient client,
        string initialQuery,
        IEnumerable<TmdbCandidate> candidates)
    {
        InitializeComponent();
        _item = item;
        _client = client;
        Candidates = new ObservableCollection<TmdbCandidate>(candidates);
        DataContext = this;
        SourceTextBlock.Text = item.SourcePath;
        SearchTextBox.Text = initialQuery;

        UpdateCandidateState();
    }

    private void UseSelected_Click(object sender, RoutedEventArgs e)
    {
        SelectedCandidate = CandidateList.SelectedItem as TmdbCandidate;
        DialogResult = SelectedCandidate is not null;
    }

    private void UseLocal_Click(object sender, RoutedEventArgs e)
    {
        UseLocalGuess = true;
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
        var query = SearchTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            SearchStatusTextBlock.Text = "Enter a movie or show title.";
            return;
        }

        SearchStatusTextBlock.Text = "Searching TMDB...";
        var results = await _client.SearchCandidatesAsync(_item, query);

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
    }
}
