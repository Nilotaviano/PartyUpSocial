using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.Google;
using PartyUp.Api.Auth;

namespace PartyUp.Api.IntegrationTests.Auth;

/// <summary>AVATARS_DESIGN.md §4: the profile scope's picture claim is mapped explicitly.</summary>
public sealed class GoogleAuthSetupTests
{
    [Fact]
    public void ConfigureOptions_MapsThePictureClaim()
    {
        var options = new GoogleOptions();

        GoogleAuthSetup.ConfigureOptions(options, "client-id", "client-secret", isDevelopment: true);

        using JsonDocument userData = JsonDocument.Parse(
            """{"sub":"123","picture":"https://lh3.googleusercontent.com/a/pic.png"}""");
        var identity = new ClaimsIdentity();
        foreach (var action in options.ClaimActions)
        {
            action.Run(userData.RootElement, identity, issuer: "Google");
        }

        Assert.Equal(
            "https://lh3.googleusercontent.com/a/pic.png",
            identity.FindFirst(GoogleAuthSetup.PictureClaimType)?.Value);
    }

    [Fact]
    public void ConfigureOptions_RequestsTheProfileScope()
    {
        var options = new GoogleOptions();

        GoogleAuthSetup.ConfigureOptions(options, "client-id", "client-secret", isDevelopment: true);

        Assert.Contains("profile", options.Scope);
    }
}
