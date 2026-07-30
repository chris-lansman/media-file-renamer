using MediaFileRenamer.App.ViewModels;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MediaFileRenamer.App.Services;

public sealed class TmdbClient
{
    private static readonly TimeSpan SearchCacheLifetime = TimeSpan.FromDays(1);
    private static readonly TimeSpan MetadataCacheLifetime = TimeSpan.FromDays(14);
    private readonly string _apiKey;
    private readonly HttpClient _httpClient;
    private readonly MetadataDiskCache? _diskCache;
    private readonly Dictionary<int, IReadOnlyList<TmdbEpisode>> _episodeCache = [];
    private readonly Dictionary<(int ShowId, int Season), IReadOnlyList<TmdbEpisode>> _seasonEpisodeCache = [];
    private readonly Dictionary<int, int?> _tvdbIdCache = [];

    public TmdbClient(
        string apiKey,
        HttpClient? httpClient = null,
        MetadataDiskCache? metadataCache = null)
    {
        _apiKey = apiKey;
        _httpClient = httpClient ?? new HttpClient();
        _diskCache = metadataCache ?? (httpClient is null ? MetadataDiskCache.Shared : null);
        _httpClient.BaseAddress ??= new Uri("https://api.themoviedb.org/3/");
        _httpClient.Timeout = TimeSpan.FromSeconds(30);
    }

    public async Task<IReadOnlyList<TmdbCandidate>> SearchCandidatesAsync(
        MediaPreviewItem item,
        string? queryOverride = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var query = string.IsNullOrWhiteSpace(queryOverride) ? item.TitleGuess : queryOverride.Trim();
            if (string.IsNullOrWhiteSpace(query))
            {
                return [];
            }
            if (item.MediaType == "TV")
            {
                return await SearchTvCandidatesAsync(item, query, cancellationToken);
            }

            if (item.MediaType == "Movie")
            {
                return await SearchMovieCandidatesAsync(item, query, cancellationToken);
            }

            var searches = await Task.WhenAll(
                SearchTvCandidatesAsync(item, query, cancellationToken),
                SearchMovieCandidatesAsync(item, query, cancellationToken));
            return searches
                .SelectMany(results => results)
                .ToList();
        }
        catch (MetadataLookupException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            throw new MetadataLookupException("TMDB lookup failed. Check the API key and internet connection.", ex);
        }
    }

    public async Task<TmdbMatch> BuildMatchAsync(
        TmdbCandidate candidate,
        MediaPreviewItem item,
        bool resolveEpisode = true,
        CancellationToken cancellationToken = default)
    {
        var episode = candidate.MediaType == "TV"
            && candidate.TmdbId is not null
            && resolveEpisode
            ? await FindEpisodeAsync(candidate.TmdbId.Value, item, cancellationToken)
            : null;

        return new TmdbMatch(
            candidate.TmdbId,
            candidate.Title,
            candidate.Year,
            episode?.Name,
            episode?.Season,
            episode?.Number)
        {
            TvdbId = candidate.TvdbId
        };
    }

    public async Task<TmdbCandidate?> FindTvByTvdbIdAsync(
        int tvdbId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var cacheKey = MetadataCacheKey.ResourceById("tmdb", "tv-by-tvdb-id", tvdbId);
        if (_diskCache?.TryGet<TmdbCandidate?>(cacheKey, out var cached) == true)
        {
            return cached;
        }

        try
        {
            var url = $"find/{tvdbId}?api_key={Uri.EscapeDataString(_apiKey)}&external_source=tvdb_id";
            using var response = await SendGetAsync(url, cancellationToken);
            response.EnsureSuccessStatusCode();
            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var result = await JsonSerializer.DeserializeAsync<TmdbFindResponse>(
                stream,
                cancellationToken: cancellationToken);
            var candidate = result?.TvResults?.FirstOrDefault();
            var match = candidate is null || string.IsNullOrWhiteSpace(candidate.Name)
                ? null
                : new TmdbCandidate(
                    candidate.Id,
                    "TV",
                    candidate.Name,
                    ParseYear(candidate.FirstAirDate),
                    candidate.FirstAirDate ?? "",
                    BuildPosterUrl(candidate.PosterPath),
                    candidate.Overview ?? "")
                {
                    TvdbId = tvdbId,
                    Provider = MetadataProvider.TmdbAndTvdb
                };
            _diskCache?.Set(cacheKey, match, MetadataCacheLifetime);
            return match;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
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
            .Select(candidate => ScoreCandidate(item, candidate, titleOverride))
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

    public static TmdbAutoMatch ScoreCandidate(
        MediaPreviewItem item,
        TmdbCandidate candidate,
        string? titleOverride = null)
    {
        var evidence = BuildConfidenceEvidence(item, candidate, titleOverride);
        return new TmdbAutoMatch(candidate, CalculateConfidence(item, candidate, titleOverride))
        {
            Evidence = evidence
        };
    }

    private async Task<IReadOnlyList<TmdbCandidate>> SearchMovieCandidatesAsync(
        MediaPreviewItem item,
        string query,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var cacheKey = MetadataCacheKey.Search("tmdb", "movie", query);
        if (_diskCache?.TryGet<List<TmdbCandidate>>(cacheKey, out var cached) == true
            && cached is not null)
        {
            return cached;
        }

        var url = $"search/movie?api_key={Uri.EscapeDataString(_apiKey)}&query={Uri.EscapeDataString(query)}";

        using var response = await SendGetAsync(url, cancellationToken);
        EnsureSuccess(response);
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var result = await JsonSerializer.DeserializeAsync<TmdbSearchResponse>(
            stream,
            cancellationToken: cancellationToken);

        var candidates = result?.Results?
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.Title))
            .Take(8)
            .Select(candidate => new TmdbCandidate(
                candidate.Id,
                "Movie",
                candidate.Title ?? item.MatchedTitle,
                ParseYear(candidate.ReleaseDate),
                candidate.ReleaseDate ?? "",
                BuildPosterUrl(candidate.PosterPath),
                candidate.Overview ?? "")
            {
                Aliases = BuildAliases(candidate.OriginalTitle),
                Popularity = candidate.Popularity,
                Provider = MetadataProvider.Tmdb
            })
            .ToList() ?? [];
        _diskCache?.Set(cacheKey, candidates, SearchCacheLifetime);
        return candidates;
    }

    private async Task<IReadOnlyList<TmdbCandidate>> SearchTvCandidatesAsync(
        MediaPreviewItem item,
        string query,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var cacheKey = MetadataCacheKey.Search("tmdb", "tv", query);
        if (_diskCache?.TryGet<List<TmdbCandidate>>(cacheKey, out var cached) == true
            && cached is not null)
        {
            return cached;
        }

        var url = $"search/tv?api_key={Uri.EscapeDataString(_apiKey)}&query={Uri.EscapeDataString(query)}";
        using var response = await SendGetAsync(url, cancellationToken);
        EnsureSuccess(response);
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var result = await JsonSerializer.DeserializeAsync<TmdbSearchResponse>(
            stream,
            cancellationToken: cancellationToken);

        var candidates = result?.Results?
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.Name))
            .Take(8)
            .Select(candidate => new TmdbCandidate(
                candidate.Id,
                "TV",
                candidate.Name ?? item.MatchedTitle,
                ParseYear(candidate.FirstAirDate),
                candidate.FirstAirDate ?? "",
                BuildPosterUrl(candidate.PosterPath),
                candidate.Overview ?? "")
            {
                Aliases = BuildAliases(candidate.OriginalName),
                Popularity = candidate.Popularity,
                Provider = MetadataProvider.Tmdb
            })
            .ToList() ?? [];
        _diskCache?.Set(cacheKey, candidates, SearchCacheLifetime);
        return candidates;
    }

    private async Task<TmdbEpisode?> FindEpisodeAsync(
        int id,
        MediaPreviewItem item,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(item.EpisodeTitle)
            && item.Season is not null
            && item.Episode is not null)
        {
            var seasonEpisodes = await GetSeasonEpisodesAsync(id, item.Season.Value, cancellationToken);
            return seasonEpisodes.FirstOrDefault(episode => episode.Number == item.Episode);
        }

        var episodes = await GetEpisodesAsync(id, cancellationToken);
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

    private async Task<IReadOnlyList<TmdbEpisode>> GetEpisodesAsync(
        int id,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_episodeCache.TryGetValue(id, out var cached))
        {
            return cached;
        }

        try
        {
            var showCacheKey = MetadataCacheKey.ResourceById(
                "tmdb",
                "series-season-count",
                id);
            int seasonCount;
            if (_diskCache?.TryGet<int>(showCacheKey, out var cachedSeasonCount) == true)
            {
                seasonCount = cachedSeasonCount;
            }
            else
            {
                using var showResponse = await SendGetAsync(
                    $"tv/{id}?api_key={Uri.EscapeDataString(_apiKey)}",
                    cancellationToken);
                if (showResponse.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    _diskCache?.Set(showCacheKey, -1, MetadataCacheLifetime);
                    _episodeCache[id] = [];
                    return [];
                }

                EnsureSuccess(showResponse);
                using var showStream = await showResponse.Content.ReadAsStreamAsync(cancellationToken);
                var show = await JsonSerializer.DeserializeAsync<TmdbTvDetail>(
                    showStream,
                    cancellationToken: cancellationToken);
                seasonCount = show?.NumberOfSeasons ?? 0;
                _diskCache?.Set(showCacheKey, seasonCount, MetadataCacheLifetime);
            }

            if (seasonCount < 0)
            {
                _episodeCache[id] = [];
                return [];
            }

            var episodes = new List<TmdbEpisode>();

            for (var season = 0; season <= seasonCount; season++)
            {
                episodes.AddRange(await GetSeasonEpisodesAsync(id, season, cancellationToken));
            }

            _episodeCache[id] = episodes;
            return episodes;
        }
        catch (MetadataLookupException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            throw new MetadataLookupException("TMDB episode lookup failed. Check the API key and internet connection.", ex);
        }
    }

    public async Task<int?> FindTvdbIdAsync(
        int tmdbId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_tvdbIdCache.TryGetValue(tmdbId, out var cached))
        {
            return cached;
        }

        var diskCacheKey = MetadataCacheKey.ResourceById(
            "tmdb",
            "tvdb-external-id",
            tmdbId);
        if (_diskCache?.TryGet<int?>(diskCacheKey, out var diskCached) == true)
        {
            _tvdbIdCache[tmdbId] = diskCached;
            return diskCached;
        }

        try
        {
            using var response = await SendGetAsync(
                $"tv/{tmdbId}/external_ids?api_key={Uri.EscapeDataString(_apiKey)}",
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _tvdbIdCache[tmdbId] = null;
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    _diskCache?.Set<int?>(diskCacheKey, null, MetadataCacheLifetime);
                }

                return null;
            }

            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var result = await JsonSerializer.DeserializeAsync<TmdbExternalIds>(
                stream,
                cancellationToken: cancellationToken);
            _tvdbIdCache[tmdbId] = result?.TvdbId;
            _diskCache?.Set(diskCacheKey, result?.TvdbId, MetadataCacheLifetime);
            return result?.TvdbId;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    private async Task<IReadOnlyList<TmdbEpisode>> GetSeasonEpisodesAsync(
        int id,
        int season,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var cacheKey = (id, season);
        if (_seasonEpisodeCache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        var diskCacheKey = MetadataCacheKey.ResourceById(
            "tmdb",
            "season-episodes",
            id,
            season);
        if (_diskCache?.TryGet<List<TmdbEpisode>>(diskCacheKey, out var diskCached) == true
            && diskCached is not null)
        {
            _seasonEpisodeCache[cacheKey] = diskCached;
            return diskCached;
        }

        try
        {
            using var response = await SendGetAsync(
                $"tv/{id}/season/{season}?api_key={Uri.EscapeDataString(_apiKey)}&language=en-US",
                cancellationToken);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                _seasonEpisodeCache[cacheKey] = [];
                _diskCache?.Set(diskCacheKey, new List<TmdbEpisode>(), MetadataCacheLifetime);
                return [];
            }
            EnsureSuccess(response);

            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var detail = await JsonSerializer.DeserializeAsync<TmdbSeasonDetail>(
                stream,
                cancellationToken: cancellationToken);
            var episodes = detail?.Episodes?
                .Where(episode => !string.IsNullOrWhiteSpace(episode.Name))
                .Select(episode => new TmdbEpisode(
                    episode.Name!,
                    episode.SeasonNumber,
                    episode.EpisodeNumber))
                .ToList() ?? [];
            _seasonEpisodeCache[cacheKey] = episodes;
            _diskCache?.Set(diskCacheKey, episodes, MetadataCacheLifetime);
            return episodes;
        }
        catch (MetadataLookupException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            throw new MetadataLookupException(
                "TMDB episode lookup failed. Check the API key and internet connection.",
                ex);
        }
    }

    private static void EnsureSuccess(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var message = response.StatusCode == System.Net.HttpStatusCode.Unauthorized
            ? "TMDB rejected the API key. Open File > Settings and verify it."
            : $"TMDB returned {(int)response.StatusCode} {response.ReasonPhrase}.";
        throw new MetadataLookupException(message);
    }

    private Task<HttpResponseMessage> SendGetAsync(string url, CancellationToken cancellationToken)
    {
        return MetadataHttpRetry.SendAsync(
            _httpClient,
            () => new HttpRequestMessage(HttpMethod.Get, url),
            cancellationToken);
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
        var candidateTitles = new[] { candidate.Title }
            .Concat(candidate.Aliases)
            .Select(Normalize)
            .Where(title => !string.IsNullOrWhiteSpace(title))
            .Distinct()
            .ToList();
        var candidateTitle = candidateTitles
            .OrderByDescending(title => Similarity(sourceTitle, title))
            .FirstOrDefault() ?? "";
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

        if (candidate.Provider == MetadataProvider.TmdbAndTvdb)
        {
            score += 2;
        }

        return Math.Clamp((int)Math.Round(score), 0, 100);
    }

    private static IReadOnlyList<string> BuildConfidenceEvidence(
        MediaPreviewItem item,
        TmdbCandidate candidate,
        string? titleOverride)
    {
        var sourceTitle = Normalize(string.IsNullOrWhiteSpace(titleOverride) ? item.TitleGuess : titleOverride);
        var matchedTitle = new[] { candidate.Title }
            .Concat(candidate.Aliases)
            .FirstOrDefault(title => Normalize(title) == sourceTitle);
        var evidence = new List<string>();
        if (!string.IsNullOrWhiteSpace(matchedTitle))
        {
            evidence.Add(matchedTitle == candidate.Title ? "Exact title" : $"Alias: {matchedTitle}");
        }

        if (item.Year is not null && candidate.Year is not null)
        {
            evidence.Add(item.Year == candidate.Year ? $"Year {item.Year}" : $"Year differs ({candidate.Year})");
        }

        evidence.Add(candidate.Provider switch
        {
            MetadataProvider.TmdbAndTvdb => "Confirmed by TMDB and TVDB",
            MetadataProvider.Tvdb => "TVDB result",
            _ => "TMDB result"
        });
        return evidence;
    }

    private static IReadOnlyList<string> BuildAliases(params string?[] aliases)
    {
        return aliases
            .Where(alias => !string.IsNullOrWhiteSpace(alias))
            .Select(alias => alias!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
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

public sealed record TmdbMatch(int? TmdbId, string Title, int? Year, string? EpisodeTitle, int? Season, int? Episode)
{
    public int? TvdbId { get; init; }
}
public sealed record TmdbCandidate(
    int Id,
    string MediaType,
    string Title,
    int? Year,
    string ReleaseDate,
    string PosterUrl,
    string Overview)
{
    public int? TmdbId { get; init; } = Id > 0 ? Id : null;
    public int? TvdbId { get; init; }
    public MetadataProvider Provider { get; init; } = MetadataProvider.Tmdb;
    public IReadOnlyList<string> Aliases { get; init; } = [];
    public double Popularity { get; init; }
    public MetadataIdentity Identity => new(TmdbId, TvdbId);
    public string ProviderLabel => Provider switch
    {
        MetadataProvider.TmdbAndTvdb => "TMDB + TVDB",
        MetadataProvider.Tvdb => "TVDB",
        _ => "TMDB"
    };
    public string ProviderIds => string.Join(
        "  ",
        new[]
        {
            TmdbId is null ? "" : $"TMDB {TmdbId}",
            TvdbId is null ? "" : $"TVDB {TvdbId}"
        }.Where(value => value.Length > 0));
}
public sealed record TmdbAutoMatch(TmdbCandidate Candidate, int ConfidencePercent)
{
    public IReadOnlyList<string> Evidence { get; init; } = [];
}
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

    [JsonPropertyName("original_title")]
    public string? OriginalTitle { get; set; }

    [JsonPropertyName("original_name")]
    public string? OriginalName { get; set; }

    [JsonPropertyName("release_date")]
    public string? ReleaseDate { get; set; }

    [JsonPropertyName("first_air_date")]
    public string? FirstAirDate { get; set; }

    [JsonPropertyName("poster_path")]
    public string? PosterPath { get; set; }

    [JsonPropertyName("overview")]
    public string? Overview { get; set; }

    [JsonPropertyName("popularity")]
    public double Popularity { get; set; }
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

internal sealed class TmdbExternalIds
{
    [JsonPropertyName("tvdb_id")]
    public int? TvdbId { get; set; }
}
