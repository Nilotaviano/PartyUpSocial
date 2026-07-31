namespace PartyUp.Api.Auth;

public static class ReturnToValidator
{
    private static readonly HashSet<string> AllowedPaths = new(StringComparer.Ordinal)
    {
        "/app",
        "/onboarding",
        "/settings/profile",
        "/settings/account",
    };

    public static string Sanitize(string? returnTo, string defaultPath = "/app") =>
        returnTo is not null && AllowedPaths.Contains(returnTo)
            ? returnTo
            : defaultPath;
}
