using System.Net;
using System.Net.Http;

namespace MediaFileRenamer.App.Services;

internal static class MetadataHttpRetry
{
    private const int MaximumAttempts = 3;

    public static async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        Func<HttpRequestMessage> requestFactory,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            var request = requestFactory();
            HttpResponseMessage response;
            try
            {
                response = await client.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);
            }
            finally
            {
                request.Dispose();
            }

            if (!ShouldRetry(response.StatusCode) || attempt >= MaximumAttempts)
            {
                return response;
            }

            var delay = GetRetryDelay(response, attempt);
            response.Dispose();
            await Task.Delay(delay, cancellationToken);
        }
    }

    private static bool ShouldRetry(HttpStatusCode statusCode)
    {
        return statusCode == HttpStatusCode.TooManyRequests
            || (int)statusCode is >= 500 and <= 599;
    }

    private static TimeSpan GetRetryDelay(HttpResponseMessage response, int attempt)
    {
        var retryAfter = response.Headers.RetryAfter;
        var requestedDelay = retryAfter?.Delta
            ?? (retryAfter?.Date is { } retryDate
                ? retryDate - DateTimeOffset.UtcNow
                : TimeSpan.FromMilliseconds(150 * Math.Pow(2, attempt - 1)));

        return requestedDelay <= TimeSpan.Zero
            ? TimeSpan.Zero
            : requestedDelay > TimeSpan.FromSeconds(2)
                ? TimeSpan.FromSeconds(2)
                : requestedDelay;
    }
}
