using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MediaFileRenamer.App.Services;

public sealed class TvdbClient
{
    private readonly string _apiKey;
    private readonly HttpClient _httpClient = new()
    {
        BaseAddress = new Uri("https://api4.thetvdb.com/v4/")
    };
    private string? _token;

    public TvdbClient(string apiKey)
    {
        _apiKey = apiKey;
    }

    public async Task<IReadOnlyList<int>> SearchSeriesIdsAsync(string query)
    {
        try
        {
            if (!await EnsureAuthenticatedAsync())
            {
                return [];
            }

            using var response = await _httpClient.GetAsync($"search?query={Uri.EscapeDataString(query)}&type=series");
            response.EnsureSuccessStatusCode();
            using var stream = await response.Content.ReadAsStreamAsync();
            var result = await JsonSerializer.DeserializeAsync<TvdbSearchResponse>(stream);
            return result?.Data?
                .Where(item => item.TvdbId is not null)
                .Select(item => item.TvdbId!.Value)
                .Distinct()
                .Take(8)
                .ToList() ?? [];
        }
        catch
        {
            return [];
        }
    }

    private async Task<bool> EnsureAuthenticatedAsync()
    {
        if (!string.IsNullOrWhiteSpace(_token))
        {
            return true;
        }

        var payload = JsonSerializer.Serialize(new { apikey = _apiKey });
        using var request = new HttpRequestMessage(HttpMethod.Post, "login")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        using var response = await _httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            return false;
        }

        using var stream = await response.Content.ReadAsStreamAsync();
        var result = await JsonSerializer.DeserializeAsync<TvdbLoginResponse>(stream);
        _token = result?.Data?.Token;
        if (string.IsNullOrWhiteSpace(_token))
        {
            return false;
        }

        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        return true;
    }
}

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
}
