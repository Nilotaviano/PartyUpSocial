using PartyUp.Application.Profile;

namespace PartyUp.Api.Endpoints;

/// <summary>
/// D189_DESIGN.md §3: the unauthenticated unsubscribe-link confirm surface. Mirrors
/// <see cref="AccountDeletionEndpoints"/>'s public-token-confirm shape — a malformed body is the
/// only distinguishable failure (framework JSON-binding 400); every other outcome (dead token,
/// already-unsubscribed, tombstoned account) answers the same 204 (A-064 posture).
/// </summary>
public static class EmailNotificationsEndpoints
{
    public static RouteGroupBuilder MapEmailNotificationsEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/email-notifications/unsubscribe", UnsubscribeAsync)
            .WithName(ApiOperationIds.UnsubscribeEmailNotifications)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest);

        return api;
    }

    private static async Task<IResult> UnsubscribeAsync(
        UnsubscribeEmailNotificationsRequest request,
        UnsubscribeEmailNotificationsUseCase useCase,
        CancellationToken cancellationToken)
    {
        await useCase.ExecuteAsync(request.Token ?? string.Empty, cancellationToken);
        return Results.NoContent();
    }
}

public sealed record UnsubscribeEmailNotificationsRequest(string? Token);
