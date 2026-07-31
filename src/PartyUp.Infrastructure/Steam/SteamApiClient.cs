using Microsoft.Extensions.Logging;

namespace PartyUp.Infrastructure.Steam;

public sealed partial class SteamApiClient(HttpClient httpClient, ILogger<SteamApiClient> logger)
{
    public async Task<string> GetStringAsync(Uri uri, CancellationToken cancellationToken)
    {
        try
        {
            return await httpClient.GetStringAsync(uri, cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            string sanitized = Sanitize(uri.ToString());
            LogRequestFailure(logger);
            throw new HttpRequestException(
                $"Steam API request failed for '{sanitized}'.",
                inner: null,
                exception.StatusCode);
        }
    }

    public static string Sanitize(string value)
    {
        int start = value.IndexOf("key=", StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return value;
        }

        int valueStart = start + 4;
        int valueEnd = value.IndexOf((char)38, valueStart);
        return valueEnd < 0
            ? value[..valueStart] + "[redacted]"
            : value[..valueStart] + "[redacted]" + value[valueEnd..];
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Steam API request failed.")]
    private static partial void LogRequestFailure(ILogger logger);
}
