namespace PartyUp.Api.Auth;

public sealed class ExternalAuthOriginOptions
{
    private static readonly Action<ILogger, string, string, Exception?> LogRealmMismatch =
        LoggerMessage.Define<string, string>(
            LogLevel.Warning,
            new EventId(1, nameof(LogRealmMismatch)),
            "Steam:Realm origin {SteamRealmOrigin} does not match PublicBaseUrl origin {PublicOrigin}; Steam return_to will use PublicBaseUrl");

    public ExternalAuthOriginOptions(
        IConfiguration configuration,
        ILogger<ExternalAuthOriginOptions> logger)
    {
        PublicOrigin = ReadOrigin(
            configuration["PublicBaseUrl"] ?? "http://localhost:5173",
            "PublicBaseUrl");

        string? configuredRealm = configuration["Steam:Realm"];
        if (string.IsNullOrWhiteSpace(configuredRealm))
        {
            SteamRealm = PublicOrigin;
            SteamRealmOrigin = PublicOrigin;
            return;
        }

        SteamRealm = configuredRealm.TrimEnd('/');
        SteamRealmOrigin = ReadOrigin(SteamRealm, "Steam:Realm");
        if (!string.Equals(PublicOrigin, SteamRealmOrigin, StringComparison.OrdinalIgnoreCase))
        {
            LogRealmMismatch(logger, SteamRealmOrigin, PublicOrigin, null);
        }
    }

    public string PublicOrigin { get; }

    public string SteamRealm { get; }

    public string SteamRealmOrigin { get; }

    private static string ReadOrigin(string configured, string key)
    {
        if (!Uri.TryCreate(configured, UriKind.Absolute, out Uri? uri) ||
            uri.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException($"{key} must be an absolute HTTP(S) URL.");
        }

        return uri.GetLeftPart(UriPartial.Authority);
    }
}
