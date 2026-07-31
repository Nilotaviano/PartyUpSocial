using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using PartyUp.Application.Abstractions;
using PartyUp.Domain.Kernel;

namespace PartyUp.Api.Auth;

public static class AuthorizationPolicies
{
    public const string RequireAuthenticated = nameof(RequireAuthenticated);
    public const string RequireOnboarded = nameof(RequireOnboarded);
    public const string RequireAdmin = nameof(RequireAdmin);

    public static IServiceCollection AddPartyUpAuthorization(this IServiceCollection services)
    {
        services.AddAuthorizationBuilder()
            .AddPolicy(RequireAuthenticated, policy =>
            {
                policy.RequireAuthenticatedUser();
                policy.AddRequirements(new CurrentUserRequirement(CurrentUserAccess.Authenticated));
            })
            .AddPolicy(RequireOnboarded, policy =>
            {
                policy.RequireAuthenticatedUser();
                policy.AddRequirements(new CurrentUserRequirement(CurrentUserAccess.Onboarded));
            })
            .AddPolicy(RequireAdmin, policy =>
            {
                policy.RequireAuthenticatedUser();
                policy.AddRequirements(new CurrentUserRequirement(CurrentUserAccess.Admin));
            });

        services.AddScoped<IAuthorizationHandler, CurrentUserAuthorizationHandler>();
        services.AddSingleton<IAuthorizationMiddlewareResultHandler, PartyUpAuthorizationResultHandler>();
        return services;
    }
}

public enum CurrentUserAccess
{
    Authenticated,
    Onboarded,
    Admin,
}

public sealed record CurrentUserRequirement(CurrentUserAccess Access) : IAuthorizationRequirement;

public sealed class CurrentUserAuthorizationHandler(ICurrentUserAccessor currentUserAccessor)
    : AuthorizationHandler<CurrentUserRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        CurrentUserRequirement requirement)
    {
        CurrentUser? user = await currentUserAccessor.GetAsync(CancellationToken.None);
        if (user is null)
        {
            return;
        }

        bool onboarded =
            user.Lifecycle is UserLifecycle.Active or UserLifecycle.PendingDeletion &&
            user.IsProfileComplete;

        bool allowed = requirement.Access switch
        {
            CurrentUserAccess.Authenticated => true,
            CurrentUserAccess.Onboarded => onboarded,
            CurrentUserAccess.Admin => onboarded && user.IsAdmin,
            _ => false,
        };

        if (allowed)
        {
            context.Succeed(requirement);
        }
    }
}

public sealed class PartyUpAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _defaultHandler = new();

    public async Task HandleAsync(
        RequestDelegate next,
        HttpContext context,
        AuthorizationPolicy policy,
        PolicyAuthorizationResult authorizeResult)
    {
        CurrentUserRequirement? partyUpRequirement = policy.Requirements
            .OfType<CurrentUserRequirement>()
            .SingleOrDefault();

        if (!authorizeResult.Succeeded && partyUpRequirement?.Access == CurrentUserAccess.Admin)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        if (authorizeResult.Forbidden && partyUpRequirement?.Access == CurrentUserAccess.Onboarded)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { code = "onboarding_required" });
            return;
        }

        if (authorizeResult.Forbidden && partyUpRequirement?.Access == CurrentUserAccess.Authenticated)
        {
            CurrentUserProjection? projection = await context.RequestServices
                .GetRequiredService<CurrentUserAccessor>()
                .GetProjectionAsync(context.RequestAborted);
            if (projection?.IsBanned == true)
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new { code = "account_banned" });
                return;
            }
        }

        await _defaultHandler.HandleAsync(next, context, policy, authorizeResult);
    }
}
