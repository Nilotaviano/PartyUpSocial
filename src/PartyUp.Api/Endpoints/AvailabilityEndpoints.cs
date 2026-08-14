using NodaTime;
using PartyUp.Api.Auth;
using PartyUp.Api.Contracts.Availability;
using PartyUp.Api.ErrorHandling;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Availability;
using PartyUp.Domain.Kernel;

namespace PartyUp.Api.Endpoints;

/// <summary>
/// PHASE5_DESIGN.md §8: the friend-selection availability surface — <c>/availability/overlaps</c> and
/// <c>/availability/suggestions</c>. <c>/me/availability/**</c> and <c>/me/actionable</c> live in
/// <see cref="MeEndpoints"/>, the shipped owner of every <c>/me/**</c> route.
/// </summary>
public static class AvailabilityEndpoints
{
    public static RouteGroupBuilder MapAvailabilityEndpoints(this RouteGroupBuilder api)
    {
        RouteGroupBuilder availability = api.MapGroup("/availability")
            .RequireAuthorization(AuthorizationPolicies.RequireOnboarded);

        availability.MapGet("/overlaps", FindOverlapWindowsAsync)
            .WithName(ApiOperationIds.FindOverlapWindows)
            .Produces<OverlapSearchResponse>()
            .ProducesPartyUpProblem(StatusCodes.Status400BadRequest);

        availability.MapGet("/suggestions", GetHomeSuggestionsAsync)
            .WithName(ApiOperationIds.GetHomeSuggestions)
            .Produces<HomeSuggestionsResponse>();

        return api;
    }

    private static async Task<IResult> FindOverlapWindowsAsync(
        string? friendIds,
        string? from,
        string? to,
        ICurrentUserAccessor currentUser,
        FindOverlapWindowsUseCase useCase,
        IFriendshipRepository friendships,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        UserId? actorId = await ActorIdAsync(currentUser, cancellationToken);
        if (actorId is null)
        {
            return Results.Unauthorized();
        }

        IReadOnlyCollection<UserId>? selection;
        if (friendIds is null)
        {
            selection = null;
        }
        else
        {
            List<UserId> parsed = [];
            foreach (string raw in friendIds.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!Guid.TryParse(raw, out Guid id))
                {
                    return Problem(context, StatusCodes.Status400BadRequest, "availability_validation");
                }

                parsed.Add(UserId.From(id));
            }

            selection = parsed;
        }

        TimeRange? range = null;
        if (from is not null || to is not null)
        {
            if (!AvailabilityWireFormat.TryParseInstant(from, out Instant start) ||
                !AvailabilityWireFormat.TryParseInstant(to, out Instant end))
            {
                return Problem(context, StatusCodes.Status400BadRequest, "invalid_range");
            }

            try
            {
                range = new TimeRange(start, end);
            }
            catch (DomainException)
            {
                return Problem(context, StatusCodes.Status400BadRequest, "invalid_range");
            }
        }

        OverlapSearchResult result;
        try
        {
            result = await useCase.ExecuteAsync(actorId.Value, selection, range, cancellationToken);
        }
        catch (DomainException ex) when (ex.Error == DomainError.RangeTooLarge)
        {
            return Problem(context, StatusCodes.Status400BadRequest, "range_too_large");
        }

        MaskedParticipantAssigner ordinals = await BuildOrdinalsAsync(friendships, actorId.Value, cancellationToken);
        var response = new OverlapSearchResponse(
            [.. result.Windows.Select(window => OverlapWindowView.From(window, ordinals))],
            TimeRangeView.From(result.Range),
            result.SelectionSource == SelectionSource.AllFriends ? "allFriends" : "explicit",
            result.ConsideredFriendIds is { } considered ? ordinals.For(considered) : null);
        return Results.Ok(response);
    }

    private static async Task<IResult> GetHomeSuggestionsAsync(
        ICurrentUserAccessor currentUser,
        GetHomeSuggestionsUseCase useCase,
        IFriendshipRepository friendships,
        CancellationToken cancellationToken)
    {
        UserId? actorId = await ActorIdAsync(currentUser, cancellationToken);
        if (actorId is null)
        {
            return Results.Unauthorized();
        }

        HomeSuggestionsResult result = await useCase.ExecuteAsync(actorId.Value, cancellationToken);
        MaskedParticipantAssigner ordinals = await BuildOrdinalsAsync(friendships, actorId.Value, cancellationToken);
        return Results.Ok(new HomeSuggestionsResponse(
            [.. result.Suggestions.Select(window => OverlapWindowView.From(window, ordinals))],
            result.HasMore));
    }

    /// <summary>
    /// Every id an overlap search or Home suggestions response carries (available/maybe/consideredFriends)
    /// is drawn from the viewer's accepted friends (A-078's ReadableOwners is a subset of that set), so
    /// one bulk read resolves every display name a response needs.
    /// </summary>
    private static async Task<MaskedParticipantAssigner> BuildOrdinalsAsync(
        IFriendshipRepository friendships, UserId viewerId, CancellationToken cancellationToken)
    {
        IReadOnlyList<AcceptedFriendProjection> accepted =
            await friendships.ListAcceptedAsync(viewerId, cancellationToken);
        Dictionary<UserId, KnownParticipant> known = accepted.ToDictionary(
            friend => friend.Counterpart.Id,
            friend => new KnownParticipant(friend.Counterpart.DisplayName, friend.Counterpart.Avatar));
        return new MaskedParticipantAssigner(known);
    }

    private static async Task<UserId?> ActorIdAsync(
        ICurrentUserAccessor currentUser,
        CancellationToken cancellationToken) =>
        (await currentUser.GetAsync(cancellationToken))?.Id;

    private static IResult Problem(HttpContext context, int status, string code) =>
        PartyUpProblemResults.Create(context, status, code);
}
