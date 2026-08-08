using System.Text.Json.Serialization;
using PartyUp.Api.Contracts.Polls;
using PartyUp.Api.Contracts.Sessions;
using PartyUp.Application.Availability;

namespace PartyUp.Api.Contracts.Availability;

/// <summary>A-080's poll layer, filled by S4c (§1.2 declared it empty in Phase 5): one option instant.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CalendarPollOptionInstantResponse(
    Guid OptionId,
    string StartTime,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? OwnVote)
{
    public static CalendarPollOptionInstantResponse From(CalendarPollOptionInstant instant) =>
        new(
            instant.OptionId.Value,
            SessionWireFormat.Format(instant.StartTime),
            instant.OwnVote is { } choice ? PollWireFormat.VoteChoice(choice) : null);
}

/// <summary>A-080: Phase 6's poll layer (PHASE6_DESIGN.md §5 calendar bullet) — one involved poll's options.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CalendarPollOptionView(
    Guid PollId,
    string Title,
    IReadOnlyList<CalendarPollOptionInstantResponse> Options)
{
    public static CalendarPollOptionView From(CalendarPollOption option) =>
        new(
            option.PollId.Value,
            option.Title.Value,
            option.Options.Select(CalendarPollOptionInstantResponse.From).ToArray());
}

/// <summary>PHASE5_DESIGN.md A-080: <c>{myAvailability, friendAvailability, sessions, pollOptions}</c> for one range.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CalendarViewResponse(
    MyAvailabilityLayerResponse MyAvailability,
    IReadOnlyList<FriendAvailabilityEntryResponse> FriendAvailability,
    IReadOnlyList<SessionSummaryResponse> Sessions,
    IReadOnlyList<CalendarPollOptionView> PollOptions);
