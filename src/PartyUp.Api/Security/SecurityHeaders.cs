namespace PartyUp.Api.Security;

/// <summary>
/// PHASE8_DESIGN.md §3 S9 (Q7): the app-wide hardening header values. Registered as middleware near
/// Program.cs's existing Referrer-Policy block.
/// </summary>
public static class SecurityHeaders
{
    public const string ContentTypeOptions = "nosniff";

    /// <summary>Belt-and-suspenders for agents that predate <c>frame-ancestors</c>.</summary>
    public const string FrameOptions = "DENY";

    /// <summary>Minimal deny-list per Q7's default -- no feature this app uses needs any of these.</summary>
    public const string PermissionsPolicy = "camera=(), microphone=(), geolocation=(), payment=()";

    /// <summary>
    /// Builds the app-wide CSP. <paramref name="scriptHashTokens"/> is empty when
    /// <c>wwwroot/index.html</c> was not found at startup (the SPA document isn't served by the API
    /// in that topology, so there is no inline script to authorize).
    /// </summary>
    public static string BuildContentSecurityPolicy(IReadOnlyList<string> scriptHashTokens)
    {
        string scriptSrc = scriptHashTokens.Count == 0
            ? "'self'"
            : "'self' " + string.Join(' ', scriptHashTokens);

        return string.Join(
            "; ",
            $"default-src 'self'",
            $"script-src {scriptSrc}",
            "style-src 'self' 'unsafe-inline'",
            "img-src 'self' data:",
            "connect-src 'self'",
            "frame-ancestors 'none'",
            "base-uri 'self'",
            "form-action 'self'");
    }
}
