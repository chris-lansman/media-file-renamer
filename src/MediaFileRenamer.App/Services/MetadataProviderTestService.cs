using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace MediaFileRenamer.App.Services;

public sealed record ProviderConnectionResult(bool IsSuccess, string Message);

public sealed class MetadataProviderTestService
{
    private static readonly HttpClient SharedHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(20)
    };
    private readonly HttpClient _httpClient;

    public MetadataProviderTestService(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? SharedHttpClient;
    }

    public async Task<ProviderConnectionResult> TestTmdbAsync(
        string? apiKey,
        CancellationToken cancellationToken = default)
    {
        var key = apiKey?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(key))
        {
            return new(false, "Enter a TMDB API key before testing.");
        }

        try
        {
            using var response = await _httpClient.GetAsync(
                $"https://api.themoviedb.org/3/authentication?api_key={Uri.EscapeDataString(key)}",
                cancellationToken);
            return response.StatusCode switch
            {
                HttpStatusCode.OK => new(true, "Connected to TMDB successfully."),
                HttpStatusCode.Unauthorized => new(
                    false,
                    "TMDB rejected this key. Verify the API key in your TMDB account."),
                (HttpStatusCode)429 => new(
                    false,
                    "TMDB is rate limiting requests. Wait a moment, then test again."),
                _ => new(
                    false,
                    $"TMDB returned {(int)response.StatusCode} {response.ReasonPhrase}. Try again shortly.")
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(false, "TMDB did not respond in time. Check your internet connection.");
        }
        catch (HttpRequestException)
        {
            return new(false, "TMDB could not be reached. Check your internet connection.");
        }
    }

    public async Task<ProviderConnectionResult> TestTvdbAsync(
        string? apiKey,
        string? pin,
        CancellationToken cancellationToken = default)
    {
        var key = apiKey?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(key))
        {
            return new(false, "Enter a TVDB API key before testing.");
        }

        try
        {
            var credentials = new Dictionary<string, string>
            {
                ["apikey"] = key
            };
            if (!string.IsNullOrWhiteSpace(pin))
            {
                credentials["pin"] = pin.Trim();
            }

            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                "https://api4.thetvdb.com/v4/login")
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(credentials),
                    Encoding.UTF8,
                    "application/json")
            };
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            return response.StatusCode switch
            {
                HttpStatusCode.OK => new(true, "Connected to TVDB successfully."),
                HttpStatusCode.BadRequest
                    or HttpStatusCode.Unauthorized
                    or HttpStatusCode.Forbidden => new(
                    false,
                    "TVDB rejected these credentials. Verify the API key and subscriber PIN."),
                (HttpStatusCode)429 => new(
                    false,
                    "TVDB is rate limiting requests. Wait a moment, then test again."),
                _ => new(
                    false,
                    $"TVDB returned {(int)response.StatusCode} {response.ReasonPhrase}. Try again shortly.")
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(false, "TVDB did not respond in time. Check your internet connection.");
        }
        catch (HttpRequestException)
        {
            return new(false, "TVDB could not be reached. Check your internet connection.");
        }
    }
}
