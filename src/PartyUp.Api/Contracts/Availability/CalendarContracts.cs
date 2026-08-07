using System.Text.Json.Serialization;
using PartyUp.Api.Contracts.Sessions;

namespace PartyUp.Api.Contracts.Availability;

/// <summary>
/// A-080: Phase 6's poll layer, declared and always empty in Phase 5 (§1.2) — carries no members yet.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CalendarPollOptionView;

/// <summary>PHASE5_DESIGN.md A-080: <c>{myAvailability, friendAvailability, sessions, pollOptions}</c> for one range.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CalendarViewResponse(
    MyAvailabilityLayerResponse MyAvailability,
    IReadOnlyList<FriendAvailabilityEntryResponse> FriendAvailability,
    IReadOnlyList<SessionSummaryResponse> Sessions,
    IReadOnlyList<CalendarPollOptionView> PollOptions);
