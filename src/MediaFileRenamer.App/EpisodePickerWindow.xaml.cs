using MediaFileRenamer.App.Services;
using MediaFileRenamer.App.ViewModels;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace MediaFileRenamer.App;

public partial class EpisodePickerWindow : Window
{
    private readonly IReadOnlyList<TmdbEpisodeChoice> _allChoices;

    public ObservableCollection<TmdbEpisodeChoice> EpisodeChoices { get; } = [];
    public TmdbEpisodeChoice? SelectedChoice { get; private set; }

    public EpisodePickerWindow(
        MediaPreviewItem item,
        IReadOnlyList<TmdbEpisodeChoice> choices)
    {
        InitializeComponent();
        _allChoices = RankChoices(item, choices);
        DataContext = this;
        ShowTextBlock.Text = $"{item.MatchedTitle} — {item.SourceFileName}";
        EpisodeSearchTextBox.Text = item.EpisodeTitle;
        ApplyFilter();
    }

    internal static IReadOnlyList<TmdbEpisodeChoice> RankChoices(
        MediaPreviewItem item,
        IReadOnlyList<TmdbEpisodeChoice> choices)
    {
        return choices
            .OrderByDescending(choice => ScoreChoice(item, choice))
            .ThenBy(choice => choice.Season)
            .ThenBy(choice => choice.Episode)
            .ToList();
    }

    private static int ScoreChoice(
        MediaPreviewItem item,
        TmdbEpisodeChoice choice)
    {
        var score = 0;
        if (item.Season == choice.Season)
        {
            score += 300;
        }

        if (item.Episode == choice.Episode)
        {
            score += 250;
        }

        var query = Normalize(item.EpisodeTitle);
        var candidate = Normalize(choice.Title);
        if (query.Length > 0)
        {
            if (query == candidate)
            {
                score += 1000;
            }
            else if (candidate.Contains(query, StringComparison.Ordinal)
                     || query.Contains(candidate, StringComparison.Ordinal))
            {
                score += 700;
            }

            var queryTokens = Tokenize(item.EpisodeTitle);
            var candidateTokens = Tokenize(choice.Title);
            score += queryTokens.Count == 0
                ? 0
                : 500 * queryTokens.Count(candidateTokens.Contains) / queryTokens.Count;
        }

        return score;
    }

    private void EpisodeSearchTextBox_TextChanged(
        object sender,
        TextChangedEventArgs e)
    {
        if (EpisodeChoices is not null)
        {
            ApplyFilter();
        }
    }

    private void ClearSearch_Click(object sender, RoutedEventArgs e)
    {
        EpisodeSearchTextBox.Clear();
        EpisodeSearchTextBox.Focus();
    }

    private void ApplyFilter()
    {
        var query = EpisodeSearchTextBox.Text.Trim();
        var queryTokens = Tokenize(query);
        var normalizedQuery = Normalize(query);
        var filtered = _allChoices.Where(choice =>
        {
            if (queryTokens.Count == 0)
            {
                return true;
            }

            var searchable = Tokenize($"{choice.Coordinate} {choice.Title}");
            return queryTokens.All(searchable.Contains)
                || Normalize(choice.DisplayLabel).Contains(
                    normalizedQuery,
                    StringComparison.Ordinal);
        }).ToList();

        EpisodeChoices.Clear();
        foreach (var choice in filtered)
        {
            EpisodeChoices.Add(choice);
        }

        NoEpisodesTextBlock.Visibility = filtered.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        ResultCountTextBlock.Text = filtered.Count == 1
            ? "1 episode"
            : $"{filtered.Count} episodes";
        EpisodeList.SelectedIndex = filtered.Count > 0 ? 0 : -1;
    }

    private void EpisodeList_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        UseEpisodeButton.IsEnabled = EpisodeList.SelectedItem is TmdbEpisodeChoice;
    }

    private void EpisodeList_MouseDoubleClick(
        object sender,
        MouseButtonEventArgs e)
    {
        if (EpisodeList.SelectedItem is TmdbEpisodeChoice)
        {
            AcceptSelection();
        }
    }

    private void UseEpisode_Click(object sender, RoutedEventArgs e)
    {
        AcceptSelection();
    }

    private void AcceptSelection()
    {
        SelectedChoice = EpisodeList.SelectedItem as TmdbEpisodeChoice;
        DialogResult = SelectedChoice is not null;
    }

    private static HashSet<string> Tokenize(string value)
    {
        return value
            .Split([' ', '-', '_', '.', ',', ':', ';', '!', '?'],
                StringSplitOptions.RemoveEmptyEntries)
            .Select(Normalize)
            .Where(token => token.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static string Normalize(string value)
    {
        return new string(value
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());
    }
}
