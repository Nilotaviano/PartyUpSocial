using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using NodaTime.Text;
using PartyUp.Api.Auth;
using PartyUp.Application.Abstractions;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;

namespace PartyUp.Api.Endpoints;

public static class AuthEndpoints
{
    public static RouteGroupBuilder MapAuthEndpoints(this RouteGroupBuilder api)
    {
        RouteGroupBuilder auth = api.MapGroup("/auth");

        auth.MapGet("/antiforgery", (HttpContext context, IAntiforgery antiforgery) =>
            {
                AntiforgeryTokenSet tokens = antiforgery.GetAndStoreTokens(context);
                return TypedResults.Ok(new AntiforgeryTokenResponse(tokens.RequestToken!));
            })
            .WithName(ApiOperationIds.GetAntiforgery)
            .Produces<AntiforgeryTokenResponse>()
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        auth.MapGet("/session", GetSessionAsync)
            .WithName(ApiOperationIds.GetSession)
            .Produces<SessionResponse>()
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        auth.MapPost("/logout", async (HttpContext context) =>
            {
                await context.SignOutAsync(IdentityConstants.ApplicationScheme);
                return TypedResults.NoContent();
            })
            .WithName(ApiOperationIds.Logout)
            .RequireAuthorization(AuthorizationPolicies.RequireAuthenticated)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        return api;
    }

    private static async Task<Ok<SessionResponse>> GetSessionAsync(
        HttpContext context,
        ICurrentUserAccessor currentUserAccessor,
        IUserRepository users,
        ProviderCapabilities providers,
        CancellationToken cancellationToken)
    {
        if (context.User.Identity?.IsAuthenticated != true)
        {
            return TypedResults.Ok(SessionResponse.Anonymous(providers.Values));
        }

        CurrentUser? currentUser = await currentUserAccessor.GetAsync(cancellationToken);
        if (currentUser is null)
        {
            return TypedResults.Ok(SessionResponse.Anonymous(providers.Values));
        }

        User? user = await users.FindAsync(currentUser.Id, cancellationToken);
        return TypedResults.Ok(user is null
            ? SessionResponse.Anonymous(providers.Values)
            : SessionResponse.ForAuthenticatedUser(user, currentUser.IsAdmin, providers.Values));
    }
}

public sealed record AntiforgeryTokenResponse(string Token);

public sealed class ProviderCapabilities
{
    public ProviderCapabilities(IConfiguration configuration)
    {
        var values = new List<string> { "password", "magicLink" };
        if (!string.IsNullOrWhiteSpace(configuration["Google:ClientId"]))
        {
            values.Add("google");
        }

        if (configuration.GetValue("Steam:Enabled", true))
        {
            values.Add("steam");
        }

        Values = values.AsReadOnly();
    }

    public IReadOnlyList<string> Values { get; }
}

public sealed record SessionResponse(
    bool Authenticated,
    Guid? UserId,
    IReadOnlyList<string> Providers,
    string? State,
    bool IsAdmin,
    OnboardingResponse? Onboarding,
    PendingDeletionResponse? PendingDeletion,
    ProfileResponse? Profile)
{
    public static SessionResponse Anonymous(IReadOnlyList<string> providers) =>
        new(false, null, providers, null, false, null, null, null);

    public static SessionResponse ForAuthenticatedUser(
        User user,
        bool isAdmin,
        IReadOnlyList<string> providers)
    {
        OnboardingResponse? onboarding = user.Lifecycle == UserLifecycle.Onboarding
            ? new OnboardingResponse(["ageGate", "auth"], "username")
            : null;
        PendingDeletionResponse? pendingDeletion =
            user.Lifecycle == UserLifecycle.PendingDeletion && user.GraceEndsAt is { } graceEndsAt
                ? new PendingDeletionResponse(InstantPattern.ExtendedIso.Format(graceEndsAt))
                : null;
        ProfileResponse? profile = user.IsProfileComplete
            ? ProfileResponse.From(user)
            : null;

        return new SessionResponse(
            true,
            user.Id.Value,
            providers,
            StateName(user.Lifecycle),
            isAdmin,
            onboarding,
            pendingDeletion,
            profile);
    }

    private static string StateName(UserLifecycle lifecycle) => lifecycle switch
    {
        UserLifecycle.Onboarding => "onboarding",
        UserLifecycle.Active => "active",
        UserLifecycle.PendingDeletion => "pending_deletion",
        UserLifecycle.Deleted => "deleted",
        _ => throw new ArgumentOutOfRangeException(nameof(lifecycle), lifecycle, null),
    };
}

public sealed record OnboardingResponse(
    IReadOnlyList<string> CompletedSteps,
    string NextStep);

public sealed record PendingDeletionResponse(string GraceEndsAt);

public sealed record ProfileResponse(
    string Username,
    string DisplayName,
    AvatarResponse Avatar,
    string TimeZone,
    string Locale,
    string FriendCode)
{
    public static ProfileResponse From(User user) =>
        new(
            user.Username!.Value.Value,
            user.DisplayName!.Value.Value,
            AvatarResponse.From(user.Avatar!.Value),
            user.TimeZone!.Id,
            user.Locale!.Value.Value,
            user.FriendCode!.Value.Value);
}

public sealed record AvatarResponse(
    string Kind,
    string? Emoji,
    Guid? SourcePlatformLinkId)
{
    public static AvatarResponse From(Avatar avatar) => avatar.IsPlatform
        ? new("platform", null, avatar.SourcePlatformLinkId!.Value.Value)
        : new("emoji", avatar.Emoji, null);
}
