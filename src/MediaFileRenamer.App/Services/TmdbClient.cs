using MediaFileRenamer.App.ViewModels;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MediaFileRenamer.App.Services;

public sealed class TmdbClient
{
    private readonly string _apiKey;
    private readonly HttpClient _httpClient = new()
    {
        BaseAddress = new Uri("https://api.themoviedb.org/3/")
    };
    private readonly Dictionary<int, IReadOnlyList<TmdbEpisode>> _episodeCache = [];

    public TmdbClient(string apiKey)
    {
        _apiKey = apiKey;
    }

    public async Task<IReadOnlyList<TmdbCandidate>> SearchCandidatesAsync(MediaPreviewItem item, string? queryOverride = null)
    {
        try
        {
            var query = string.IsNullOrWhiteSpace(queryOverride) ? item.TitleGuess : queryOverride.Trim();
            if (item.MediaType == "TV")
            {
                return await SearchTvCandidatesAsync(item, query);
            }

            if (item.MediaType == "Movie")
            {
                return await SearchMovieCandidatesAsync(item, query);
            }

            var searches = await Task.WhenAll(
                SearchTvCandidatesAsync(item, query),
                SearchMovieCandidatesAsync(item, query));
            return searches
                .SelectMany(results => results)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    public async Task<TmdbMatch> BuildMatchAsync(TmdbCandidate candidate, MediaPreviewItem item)
    {
        var episode = candidate.MediaType == "TV"
            ? await FindEpisodeAsync(candidate.Id, item)
            : null;

        return new TmdbMatch(candidate.Id, candidate.Title, candidate.Year, episode?.Name, episode?.Season, episode?.Number);
    }

    public async Task<TmdbCandidate?> FindTvByTvdbIdAsync(int tvdbId)
    {
        try
        {
            var url = $"find/{tvdbId}?api_key={Uri.EscapeDataString(_apiKey)}&external_source=tvdb_id";
            using var response = await _httpClient.GetAsync(url);
            response.EnsureSuccessStatusCode();
            using var stream = await response.Content.ReadAsStreamAsync();
            var result = await JsonSerializer.DeserializeAsync<TmdbFindResponse>(stream);
            var candidate = result?.TvResults?.FirstOrDefault();
            return candidate is null || string.IsNullOrWhiteSpace(candidate.Name)
                ? null
                : new TmdbCandidate(
                    candidate.Id,
                    "TV",
                    candidate.Name,
                    ParseYear(candidate.FirstAirDate),
                    candidate.FirstAirDate ?? "",
                    BuildPosterUrl(candidate.PosterPath),
                    candidate.Overview ?? "");
        }
        catch
        {
            return null;
        }
    }

    public static TmdbAutoMatch? FindAutoMatch(
        MediaPreviewItem item,
        IReadOnlyList<TmdbCandidate> candidates,
        int confidenceThreshold,
        string? titleOverride = null)
    {
        var scored = candidates
            .Select(candidate => new TmdbAutoMatch(candidate, CalculateConfidence(item, candidate, titleOverride)))
            .OrderByDescending(candidate => candidate.ConfidencePercent)
            .ToList();

        if (scored.Count == 0)
        {
            return null;
        }

        var best = scored[0];
        var runnerUp = scored.Count > 1 ? scored[1] : null;
        if (best.ConfidencePercent < confidenceThreshold)
        {
            return null;
        }

        if (runnerUp is not null && best.ConfidencePercent - runnerUp.ConfidencePercent < 4)
        {
            return null;
        }

        return best;
    }

    private async Task<IReadOnlyList<TmdbCandidate>> SearchMovieCandidatesAsync(MediaPreviewItem item, string query)
    {
        var url = $"search/movie?api_key={Uri.EscapeDataString(_apiKey)}&query={Uri.EscapeDataString(query)}";

        using var response = await _httpClient.GetAsync(url);
        response.EnsureSuccessStatusCode();
        using var stream = await response.Content.ReadAsStreamAsync();
        var result = await JsonSerializer.DeserializeAsync<TmdbSearchResponse>(stream);

        return result?.Results?
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.Title))
            .Take(8)
            .Select(candidate => new TmdbCandidate(
                candidate.Id,
                "Movie",
                candidate.Title ?? item.MatchedTitle,
                ParseYear(candidate.ReleaseDate),
                candidate.ReleaseDate ?? "",
                BuildPosterUrl(candidate.PosterPath),
                candidate.Overview ?? ""))
            .ToList() ?? [];
    }

    private async Task<IReadOnlyList<TmdbCandidate>> SearchTvCandidatesAsync(MediaPreviewItem item, string query)
    {
        var url = $"search/tv?api_key={Uri.EscapeDataString(_apiKey)}&query={Uri.EscapeDataString(query)}";
        using var response = await _httpClient.GetAsync(url);
        response.EnsureSuccessStatusCode();
        using var stream = await response.Content.ReadAsStreamAsync();
        var result = await JsonSerializer.DeserializeAsync<TmdbSearchResponse>(stream);

        return result?.Results?
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.Name))
            .Take(8)
            .Select(candidate => new TmdbCandidate(
                candidate.Id,
                "TV",
                candidate.Name ?? item.MatchedTitle,
                ParseYear(candidate.FirstAirDate),
                candidate.FirstAirDate ?? "",
                BuildPosterUrl(candidate.PosterPath),
                candidate.Overview ?? ""))
            .ToList() ?? [];
    }

    private async Task<TmdbEpisode?> FindEpisodeAsync(int id, MediaPreviewItem item)
    {
        var episodes = await GetEpisodesAsync(id);
        if (!string.IsNullOrWhiteSpace(item.EpisodeTitle))
        {
            var normalizedTitle = Normalize(item.EpisodeTitle);
            var byTitle = episodes.FirstOrDefault(episode => Normalize(episode.Name) == normalizedTitle);
            if (byTitle is not null)
            {
                return byTitle;
            }
        }

        return item.Season is null || item.Episode is null
            ? null
            : episodes.FirstOrDefault(episode => episode.Season == item.Season && episode.Number == item.Episode);
    }

    private async Task<IReadOnlyList<TmdbEpisode>> GetEpisodesAsync(int id)
    {
        if (_episodeCache.TryGetValue(id, out var cached))
        {
            return cached;
        }

        try
        {
            using var showResponse = await _httpClient.GetAsync($"tv/{id}?api_key={Uri.EscapeDataString(_apiKey)}");
            showResponse.EnsureSuccessStatusCode();
            using var showStream = await showResponse.Content.ReadAsStreamAsync();
            var show = await JsonSerializer.DeserializeAsync<TmdbTvDetail>(showStream);
            var episodes = new List<TmdbEpisode>();

            for (var season = 1; season <= (show?.NumberOfSeasons ?? 0); season++)
            {
                using var seasonResponse = await _httpClient.GetAsync($"tv/{id}/season/{season}?api_key={Uri.EscapeDataString(_apiKey)}");
                if (!seasonResponse.IsSuccessStatusCode)
                {
                    continue;
                }

                using var seasonStream = await seasonResponse.Content.ReadAsStreamAsync();
                var seasonDetail = await JsonSerializer.DeserializeAsync<TmdbSeasonDetail>(seasonStream);
                episodes.AddRange(seasonDetail?.Episodes?
                    .Where(episode => !string.IsNullOrWhiteSpace(episode.Name))
                    .Select(episode => new TmdbEpisode(episode.Name!, episode.SeasonNumber, episode.EpisodeNumber)) ?? []);
            }

            _episodeCache[id] = episodes;
            return episodes;
        }
        catch
        {
            return [];
        }
    }

    private static int? ParseYear(string? value)
    {
        return DateTime.TryParse(value, out var date) ? date.Year : null;
    }

    private static string BuildPosterUrl(string? path)
    {
        return string.IsNullOrWhiteSpace(path)
            ? ""
            : $"https://image.tmdb.org/t/p/w185{path}";
    }

    private static string Normalize(string value)
    {
        return new string(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    }

    private static int CalculateConfidence(MediaPreviewItem item, TmdbCandidate candidate, string? titleOverride)
    {
        var sourceTitle = Normalize(string.IsNullOrWhiteSpace(titleOverride) ? item.TitleGuess : titleOverride);
        var candidateTitle = Normalize(candidate.Title);
        if (string.IsNullOrWhiteSpace(sourceTitle) || string.IsNullOrWhiteSpace(candidateTitle))
        {
            return 0;
        }

        var score = Similarity(sourceTitle, candidateTitle) * 100;
        if (sourceTitle == candidateTitle)
        {
            score = Math.Max(score, item.MediaType == "TV" ? 97 : 96);
        }

        if (item.Year is not null && candidate.Year is not null)
        {
            score += item.Year == candidate.Year ? 8 : -18;
        }
        else if (item.MediaType == "Movie" && item.Year is null)
        {
            var sameTitleOtherYears = sourceTitle == candidateTitle;
            score += sameTitleOtherYears ? -2 : 0;
        }

        else if (item.MediaType == "Unknown")
        {
            if (candidate.MediaType == "TV" && !string.IsNullOrWhiteSpace(item.EpisodeTitle))
            {
                score += 6;
            }
            else if (candidate.MediaType == "TV" && item.GroupFileCount >= 3)
            {
                score += 2;
            }
        }

        return Math.Clamp((int)Math.Round(score), 0, 100);
    }

    private static double Similarity(string left, string right)
    {
        if (left == right)
        {
            return 1;
        }

        var distance = LevenshteinDistance(left, right);
        var maxLength = Math.Max(left.Length, right.Length);
        return maxLength == 0 ? 1 : 1 - (double)distance / maxLength;
    }

    private static int LevenshteinDistance(string left, string right)
    {
        var previous = new int[right.Length + 1];
        var current = new int[right.Length + 1];

        for (var j = 0; j <= right.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= left.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= right.Length; j++)
            {
                var cost = left[i - 1] == right[j - 1] ? 0 : 1;
                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[right.Length];
    }
}

public sealed record TmdbMatch(int TmdbId, string Title, int? Year, string? EpisodeTitle, int? Season, int? Episode);
public sealed record TmdbCandidate(int Id, string MediaType, string Title, int? Year, string ReleaseDate, string PosterUrl, string Overview);
public sealed record TmdbAutoMatch(TmdbCandidate Candidate, int ConfidencePercent);
internal sealed record TmdbEpisode(string Name, int Season, int Number);

internal sealed class TmdbSearchResponse
{
    [JsonPropertyName("results")]
    public List<TmdbSearchItem>? Results { get; set; }
}

internal sealed class TmdbSearchItem
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("release_date")]
    public string? ReleaseDate { get; set; }

    [JsonPropertyName("first_air_date")]
    public string? FirstAirDate { get; set; }

    [JsonPropertyName("poster_path")]
    public string? PosterPath { get; set; }

    [JsonPropertyName("overview")]
    public string? Overview { get; set; }
}

internal sealed class TmdbEpisodeDetail
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

internal sealed class TmdbTvDetail
{
    [JsonPropertyName("number_of_seasons")]
    public int NumberOfSeasons { get; set; }
}

internal sealed class TmdbSeasonDetail
{
    [JsonPropertyName("episodes")]
    public List<TmdbSeasonEpisode>? Episodes { get; set; }
}

internal sealed class TmdbSeasonEpisode
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("season_number")]
    public int SeasonNumber { get; set; }

    [JsonPropertyName("episode_number")]
    public int EpisodeNumber { get; set; }
}

internal sealed class TmdbFindResponse
{
    [JsonPropertyName("tv_results")]
    public List<TmdbSearchItem>? TvResults { get; set; }
}
