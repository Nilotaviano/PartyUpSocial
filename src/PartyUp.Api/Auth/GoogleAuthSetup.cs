using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Authentication.OAuth.Claims;
using Microsoft.AspNetCore.Identity;
using System.Text.Json;

namespace PartyUp.Api.Auth;

public static class GoogleAuthSetup
{
    public static IServiceCollection AddPartyUpGoogleAuthentication(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        string? clientId = configuration["Google:ClientId"];
        if (string.IsNullOrWhiteSpace(clientId))
        {
            return services;
        }

        services.AddAuthentication()
            .AddGoogle(
                GoogleDefaults.AuthenticationScheme,
                options => ConfigureOptions(
                    options,
                    clientId,
                    configuration["Google:ClientSecret"]
                        ?? throw new InvalidOperationException(
                            "Google:ClientSecret is required when Google:ClientId is configured."),
                    environment.IsDevelopment()));

        return services;
    }

    public static void ConfigureOptions(
        GoogleOptions options,
        string clientId,
        string clientSecret,
        bool isDevelopment)
    {
        options.ClientId = clientId;
        options.ClientSecret = clientSecret;
        options.SignInScheme = IdentityConstants.ExternalScheme;
        options.CallbackPath = "/api/v1/auth/external/google/callback";
        options.Scope.Clear();
        options.Scope.Add("openid");
        options.Scope.Add("email");
        options.Scope.Add("profile");
        options.ClaimActions.MapCustomJson(
            "email_verified",
            user => user.TryGetProperty("email_verified", out JsonElement value) &&
                    value.ValueKind == JsonValueKind.True
                ? "true"
                : "false");
        options.CorrelationCookie.SameSite = SameSiteMode.Lax;
        options.CorrelationCookie.SecurePolicy = isDevelopment
            ? CookieSecurePolicy.SameAsRequest
            : CookieSecurePolicy.Always;
    }
}
