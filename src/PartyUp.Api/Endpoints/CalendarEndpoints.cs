using NodaTime;
using PartyUp.Api.Auth;
using PartyUp.Api.Contracts.Availability;
using PartyUp.Api.Contracts.Sessions;
using PartyUp.Api.ErrorHandling;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Availability;
using PartyUp.Domain.Kernel;

namespace PartyUp.Api.Endpoints;

/// <summary>PHASE5_DESIGN.md §8/A-080: the single atomic calendar read, <c>GET /calendar</c>.</summary>
public static class CalendarEndpoints
{
    public static RouteGroupBuilder MapCalendarEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/calendar", GetCalendarViewAsync)
            .WithName(ApiOperationIds.GetCalendarView)
            .RequireAuthorization(AuthorizationPolicies.RequireOnboarded)
            .Produces<CalendarViewResponse>()
            .ProducesPartyUpProblem(StatusCodes.Status400BadRequest);

        return api;
    }

    private static async Task<IResult> GetCalendarViewAsync(
        string? from,
        string? to,
        ICurrentUserAccessor currentUser,
        GetCalendarViewUseCase useCase,
        IFriendshipRepository friendships,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        UserId? actorId = await ActorIdAsync(currentUser, cancellationToken);
        if (actorId is null)
        {
            return Results.Unauthorized();
        }

        if (!AvailabilityWireFormat.TryParseInstant(from, out Instant start) ||
            !AvailabilityWireFormat.TryParseInstant(to, out Instant end))
        {
            return Problem(context, StatusCodes.Status400BadRequest, "invalid_range");
        }

        TimeRange range;
        try
        {
            range = new TimeRange(start, end);
        }
        catch (DomainException)
        {
            return Problem(context, StatusCodes.Status400BadRequest, "invalid_range");
        }

        CalendarView view;
        try
        {
            view = await useCase.ExecuteAsync(actorId.Value, range, cancellationToken);
        }
        catch (DomainException ex) when (ex.Error == DomainError.RangeTooLarge)
        {
            return Problem(context, StatusCodes.Status400BadRequest, "range_too_large");
        }

        IReadOnlyList<AcceptedFriendProjection> accepted =
            await friendships.ListAcceptedAsync(actorId.Value, cancellationToken);
        Dictionary<UserId, DisplayName?> names = accepted.ToDictionary(
            friend => friend.Counterpart.Id, friend => friend.Counterpart.DisplayName);
        var ordinals = new MaskedParticipantAssigner(names);

        // One shared assigner for the whole response — a friend-availability owner who is also a
        // visible session's organizer gets one ordinal, not two.
        var response = new CalendarViewResponse(
            MyAvailabilityLayerResponse.From(view.MyAvailability, includeRange: false),
            [.. view.FriendAvailability.Select(entry => FriendAvailabilityEntryResponse.From(entry, ordinals))],
            [.. view.Sessions.Select(s => SessionSummaryResponse.From(s, ordinals))],
            [.. view.PollOptions.Select(CalendarPollOptionView.From)]);
        return Results.Ok(response);
    }

    private static async Task<UserId?> ActorIdAsync(
        ICurrentUserAccessor currentUser,
        CancellationToken cancellationToken) =>
        (await currentUser.GetAsync(cancellationToken))?.Id;

    private static IResult Problem(HttpContext context, int status, string code) =>
        PartyUpProblemResults.Create(context, status, code);
}
