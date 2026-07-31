using PartyUp.Api.Auth;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Onboarding;
using PartyUp.Domain.Users;

namespace PartyUp.Api.Endpoints;

public static class OnboardingEndpoints
{
    public static RouteGroupBuilder MapOnboardingEndpoints(this RouteGroupBuilder api)
    {
        RouteGroupBuilder onboarding = api.MapGroup("/onboarding")
            .RequireAuthorization(AuthorizationPolicies.RequireAuthenticated);

        onboarding.MapGet("/state", async (
            GetOnboardingStateUseCase useCase,
            CancellationToken cancellationToken) =>
        {
            OnboardingState? state = await useCase.ExecuteAsync(cancellationToken);
            return state is null ? Results.Unauthorized() : Results.Ok(state);
        }).WithName("GetOnboardingState");

        onboarding.MapGet("/username/availability", async (
            string username,
            CheckUsernameAvailabilityUseCase useCase,
            CancellationToken cancellationToken) =>
            Results.Ok(await useCase.ExecuteAsync(username, cancellationToken)))
            .WithName(ApiOperationIds.CheckUsernameAvailability);

        onboarding.MapPost("/validate", (
            ProfileDraft request,
            ValidateProfileDraftUseCase useCase) =>
            Results.Ok(useCase.Execute(request)))
            .WithName("ValidateOnboardingProfile");

        onboarding.MapPost("/complete", CompleteAsync)
            .WithName("CompleteOnboarding")
            .Produces<SessionResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return api;
    }

    private static async Task<IResult> CompleteAsync(
        ProfileDraft request,
        CompleteOnboardingUseCase useCase,
        IUserRepository users,
        ICurrentUserProjectionReader projections,
        ProviderCapabilities providers,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        OnboardingCompletionResult result = await useCase.ExecuteAsync(
            request,
            cancellationToken);
        if (!result.Succeeded)
        {
            int status = result.Code == "validation_failed"
                ? StatusCodes.Status400BadRequest
                : StatusCodes.Status409Conflict;
            return Results.Json(
                new OnboardingProblem(
                    "about:blank",
                    status == StatusCodes.Status400BadRequest
                        ? "Bad Request"
                        : "Conflict",
                    status,
                    result.Code!,
                    context.TraceIdentifier,
                    result.Errors),
                statusCode: status,
                contentType: "application/problem+json");
        }

        User? user = await users.FindAsync(result.UserId!.Value, cancellationToken);
        CurrentUserProjection? projection = await projections.ReadAsync(
            result.UserId.Value,
            cancellationToken);
        return user is null
            ? Results.NotFound()
            : Results.Ok(SessionResponse.ForAuthenticatedUser(
                user,
                projection?.IsAdmin == true,
                providers.Values));
    }
}

public sealed record OnboardingProblem(
    string Type,
    string Title,
    int Status,
    string Code,
    string TraceId,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Errors);
