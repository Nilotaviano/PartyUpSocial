using PartyUp.Application.Auth;

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

    public static string Sanitize(string? returnTo, string defaultPath = "/app")
    {
        if (returnTo is not null && AllowedPaths.Contains(returnTo))
        {
            return returnTo;
        }

        return FriendContinuation.Validate(returnTo) ?? defaultPath;
    }
}
