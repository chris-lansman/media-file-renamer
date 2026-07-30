using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MediaFileRenamer.App.Services;

public sealed class TvdbClient
{
    private static readonly TimeSpan SearchCacheLifetime = TimeSpan.FromDays(1);
    private static readonly TimeSpan EpisodeCacheLifetime = TimeSpan.FromDays(14);
    private readonly string _apiKey;
    private readonly string _pin;
    private readonly HttpClient _httpClient;
    private readonly MetadataDiskCache? _diskCache;
    private readonly Dictionary<string, TvdbEpisodeMatch?> _episodeCache = [];
    private string? _token;

    public TvdbClient(
        string apiKey,
        string? pin = null,
        HttpClient? httpClient = null,
        MetadataDiskCache? metadataCache = null)
    {
        _apiKey = apiKey;
        _pin = pin?.Trim() ?? "";
        _httpClient = httpClient ?? new HttpClient();
        _diskCache = metadataCache ?? (httpClient is null ? MetadataDiskCache.Shared : null);
        _httpClient.BaseAddress ??= new Uri("https://api4.thetvdb.com/v4/");
        _httpClient.Timeout = TimeSpan.FromSeconds(30);
    }

    public async Task<IReadOnlyList<int>> SearchSeriesIdsAsync(
        string query,
        CancellationToken cancellationToken = default)
    {
        return (await SearchSeriesAsync(query, cancellationToken))
            .Select(candidate => candidate.TvdbId)
            .ToList();
    }

    public async Task<IReadOnlyList<TvdbSeriesCandidate>> SearchSeriesAsync(
        string query,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        var cacheKey = MetadataCacheKey.Search("tvdb", "tv", query);
        if (_diskCache?.TryGet<List<TvdbSeriesCandidate>>(cacheKey, out var cached) == true
            && cached is not null)
        {
            return cached;
        }

        try
        {
            if (!await EnsureAuthenticatedAsync(cancellationToken))
            {
                return [];
            }

            using var response = await SendAsync(
                () => new HttpRequestMessage(
                    HttpMethod.Get,
                    $"search?query={Uri.EscapeDataString(query)}&type=series"),
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return [];
            }

            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var result = await JsonSerializer.DeserializeAsync<TvdbSearchResponse>(
                stream,
                cancellationToken: cancellationToken);
            var candidates = result?.Data?
                .Where(item => item.TvdbId is not null && !string.IsNullOrWhiteSpace(item.Name))
                .Select(item => new TvdbSeriesCandidate(
                    item.TvdbId!.Value,
                    item.Name!,
                    ParseYear(item.Year),
                    item.Aliases?.Where(alias => !string.IsNullOrWhiteSpace(alias)).ToList() ?? [],
                    item.ImageUrl ?? "",
                    item.Overview ?? ""))
                .DistinctBy(candidate => candidate.TvdbId)
                .Take(8)
                .ToList() ?? [];
            _diskCache?.Set(cacheKey, candidates, SearchCacheLifetime);
            return candidates;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return [];
        }
    }

    public Task<TvdbEpisodeMatch?> FindEpisodeAsync(
        int seriesId,
        int season,
        int episode,
        CancellationToken cancellationToken = default)
    {
        return FindEpisodeAsync(
            seriesId,
            season,
            episode,
            EpisodeOrder.Default,
            null,
            cancellationToken);
    }

    public async Task<TvdbEpisodeMatch?> FindEpisodeAsync(
        int seriesId,
        int? season,
        int? episode,
        EpisodeOrder order,
        DateOnly? airDate = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (episode is null && airDate is null)
        {
            return null;
        }

        var orderPath = ToOrderPath(order);
        var cacheKey = $"{seriesId}:{orderPath}:{season}:{episode}:{airDate:yyyy-MM-dd}";
        if (_episodeCache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        var diskCacheKey = MetadataCacheKey.Episode(
            "tvdb",
            seriesId,
            order,
            season,
            episode,
            airDate);
        if (_diskCache?.TryGet<TvdbEpisodeMatch?>(diskCacheKey, out var diskCached) == true)
        {
            _episodeCache[cacheKey] = diskCached;
            return diskCached;
        }

        try
        {
            if (!await EnsureAuthenticatedAsync(cancellationToken))
            {
                return null;
            }

            var query = new List<string> { "page=0" };
            if (season is not null && order != EpisodeOrder.Absolute)
            {
                query.Add($"season={season}");
            }

            if (episode is not null)
            {
                query.Add($"episodeNumber={episode}");
            }

            if (airDate is not null)
            {
                query.Add($"airDate={airDate:yyyy-MM-dd}");
            }

            using var response = await SendAsync(
                () => new HttpRequestMessage(
                    HttpMethod.Get,
                    $"series/{seriesId}/episodes/{orderPath}?{string.Join("&", query)}"),
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var result = await JsonSerializer.DeserializeAsync<TvdbSeriesEpisodesResponse>(
                stream,
                cancellationToken: cancellationToken);
            var matches = result?.Data?.Episodes?
                .Where(candidate => CoordinatesAgree(candidate, season, episode, order, airDate)
                    && !string.IsNullOrWhiteSpace(candidate.Name))
                .Select(candidate => new TvdbEpisodeMatch(
                    candidate.Id,
                    candidate.Name!,
                    candidate.SeasonNumber,
                    candidate.Number,
                    candidate.Aired is null ? null : ParseDate(candidate.Aired)))
                .DistinctBy(candidate => candidate.Id)
                .ToList() ?? [];

            var match = matches.Count == 1 ? matches[0] : null;
            _episodeCache[cacheKey] = match;
            _diskCache?.Set(diskCacheKey, match, EpisodeCacheLifetime);
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

    private async Task<bool> EnsureAuthenticatedAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_token))
        {
            return true;
        }

        var credentials = new Dictionary<string, string>
        {
            ["apikey"] = _apiKey
        };
        if (!string.IsNullOrWhiteSpace(_pin))
        {
            credentials["pin"] = _pin;
        }

        var payload = JsonSerializer.Serialize(credentials);
        using var response = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Post, "login")
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            },
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return false;
        }

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var result = await JsonSerializer.DeserializeAsync<TvdbLoginResponse>(
            stream,
            cancellationToken: cancellationToken);
        _token = result?.Data?.Token;
        if (string.IsNullOrWhiteSpace(_token))
        {
            return false;
        }

        _httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _token);
        return true;
    }

    private Task<HttpResponseMessage> SendAsync(
        Func<HttpRequestMessage> requestFactory,
        CancellationToken cancellationToken)
    {
        return MetadataHttpRetry.SendAsync(_httpClient, requestFactory, cancellationToken);
    }

    private static bool CoordinatesAgree(
        TvdbEpisodeRecord candidate,
        int? season,
        int? episode,
        EpisodeOrder order,
        DateOnly? airDate)
    {
        if (airDate is not null && ParseDate(candidate.Aired) != airDate)
        {
            return false;
        }

        if (episode is not null && candidate.Number != episode)
        {
            return false;
        }

        return season is null
            || order == EpisodeOrder.Absolute
            || candidate.SeasonNumber == season;
    }

    private static string ToOrderPath(EpisodeOrder order)
    {
        return order switch
        {
            EpisodeOrder.Default => "default",
            EpisodeOrder.Official => "official",
            EpisodeOrder.Dvd => "dvd",
            EpisodeOrder.Absolute => "absolute",
            EpisodeOrder.Alternate => "alternate",
            EpisodeOrder.Regional => "regional",
            _ => "default"
        };
    }

    private static int? ParseYear(string? value)
    {
        return int.TryParse(value, out var year)
            ? year
            : DateTime.TryParse(value, out var date) ? date.Year : null;
    }

    private static DateOnly? ParseDate(string? value)
    {
        return DateOnly.TryParse(value, out var date) ? date : null;
    }
}

public sealed record TvdbSeriesCandidate(
    int TvdbId,
    string Title,
    int? Year,
    IReadOnlyList<string> Aliases,
    string PosterUrl,
    string Overview);

public sealed record TvdbEpisodeMatch(
    int Id,
    string Name,
    int Season,
    int Episode,
    DateOnly? AirDate = null);

internal sealed class TvdbLoginResponse
{
    [JsonPropertyName("data")]
    public TvdbLoginData? Data { get; set; }
}

internal sealed class TvdbLoginData
{
    [JsonPropertyName("token")]
    public string? Token { get; set; }
}

internal sealed class TvdbSearchResponse
{
    [JsonPropertyName("data")]
    public List<TvdbSearchItem>? Data { get; set; }
}

internal sealed class TvdbSearchItem
{
    [JsonPropertyName("tvdb_id")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public int? TvdbId { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("year")]
    public string? Year { get; set; }

    [JsonPropertyName("aliases")]
    public List<string>? Aliases { get; set; }

    [JsonPropertyName("image_url")]
    public string? ImageUrl { get; set; }

    [JsonPropertyName("overview")]
    public string? Overview { get; set; }
}

internal sealed class TvdbSeriesEpisodesResponse
{
    [JsonPropertyName("data")]
    public TvdbSeriesEpisodesData? Data { get; set; }
}

internal sealed class TvdbSeriesEpisodesData
{
    [JsonPropertyName("episodes")]
    public List<TvdbEpisodeRecord>? Episodes { get; set; }
}

internal sealed class TvdbEpisodeRecord
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("seasonNumber")]
    public int SeasonNumber { get; set; }

    [JsonPropertyName("number")]
    public int Number { get; set; }

    [JsonPropertyName("aired")]
    public string? Aired { get; set; }
}
