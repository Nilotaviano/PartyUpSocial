using Serilog.Events;

namespace PartyUp.Api.Logging;

public static class FriendPathLogSanitizer
{
    private static readonly IReadOnlyList<(string Prefix, string Redacted)> Families =
    [
        ("/u/", "/u/REDACTED"),
        ("/add/", "/add/REDACTED"),
        ("/api/v1/users/by-username/", "/api/v1/users/by-username/REDACTED"),
        ("/api/v1/friend-codes/", "/api/v1/friend-codes/REDACTED"),
        ("/g/", "/g/REDACTED"),
        ("/a/", "/a/REDACTED"),
    ];

    public static string Sanitize(PathString path)
    {
        string value = path.Value ?? string.Empty;
        foreach ((string prefix, string redacted) in Families)
        {
            if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return redacted;
            }
        }

        return value;
    }

    public static IEnumerable<LogEventProperty> MessageTemplateProperties(
        HttpContext context,
        string _,
        double elapsedMilliseconds,
        int statusCode) =>
    [
        new("RequestMethod", new ScalarValue(context.Request.Method)),
        new("RequestPath", new ScalarValue(Sanitize(context.Request.Path))),
        new("StatusCode", new ScalarValue(statusCode)),
        new("Elapsed", new ScalarValue(elapsedMilliseconds)),
    ];
}
