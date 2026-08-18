using PartyUp.Api.Contracts.Feedback;
using PartyUp.Api.ErrorHandling;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Feedback;
using PartyUp.Domain.Feedback;
using PartyUp.Domain.Kernel;

namespace PartyUp.Api.Endpoints;

/// <summary>
/// D-213's single write. Deliberately mapped on the plain <c>/api/v1</c> group with no
/// <c>RequireAuthorization</c>: any user or anonymous visitor may submit (D-212), and an
/// authenticated caller's id is recorded when one is present. The dedicated
/// <c>feedback-ip</c>/<c>feedback-user</c> rate-limit dimensions (RateLimitPolicies) are what makes
/// an account-less route safe to expose.
/// </summary>
public static class FeedbackEndpoints
{
    public static RouteGroupBuilder MapFeedbackEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/feedback", SubmitFeedbackAsync)
            .WithName(ApiOperationIds.SubmitFeedback)
            .Produces<SubmitFeedbackResponse>(StatusCodes.Status201Created)
            .ProducesPartyUpProblem(StatusCodes.Status400BadRequest)
            .ProducesPartyUpProblem(StatusCodes.Status415UnsupportedMediaType)
            .ProducesPartyUpProblem(StatusCodes.Status429TooManyRequests)
            .ProducesPartyUpProblem(StatusCodes.Status500InternalServerError);

        return api;
    }

    private static async Task<IResult> SubmitFeedbackAsync(
        SubmitFeedbackRequest request,
        HttpContext context,
        ICurrentUserAccessor currentUser,
        SubmitFeedbackUseCase useCase,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (FeedbackTypeNames.FromWire(request.Type) is not { } type)
        {
            return PartyUpProblemResults.Create(
                context,
                StatusCodes.Status400BadRequest,
                "invalid_request");
        }

        // D-213: an unauthenticated caller is the expected case, not an error — the accessor simply
        // yields null and the row is stored anonymous.
        UserId? submitterId = (await currentUser.GetAsync(cancellationToken))?.Id;

        FeedbackItemId id = await useCase.ExecuteAsync(
            new SubmitFeedbackCommand(
                submitterId,
                type,
                request.Message,
                request.Route,
                request.UserAgent,
                request.Locale,
                request.AppVersion),
            cancellationToken);

        // No Location header: D-214 gives a submission no submitter-facing read path to point at.
        return Results.Created((string?)null, new SubmitFeedbackResponse(id.Value));
    }
}
