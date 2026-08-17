using System.Text.Json.Serialization;
using NodaTime;
using NodaTime.Text;
using PartyUp.Api.Contracts.Sessions;
using PartyUp.Api.Endpoints;
using PartyUp.Application.Polls;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Polls;
using PartyUp.Domain.Sessions;

namespace PartyUp.Api.Contracts.Polls;

/// <summary>
/// Wire-shape converters for the poll HTTP surface (PHASE6_DESIGN.md §4), mirroring
/// <see cref="SessionWireFormat"/>. Instant formatting is delegated to it rather than duplicated.
/// </summary>
public static class PollWireFormat
{
    public static string Status(PollStatus status) => status switch
    {
        PollStatus.Polling => "polling",
        PollStatus.Closed => "closed",
        PollStatus.Finalized => "finalized",
        PollStatus.Expired => "expired",
        PollStatus.Canceled => "canceled",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    public static string VoteChoice(VoteChoice choice) => choice switch
    {
        Domain.Kernel.VoteChoice.Available => "available",
        Domain.Kernel.VoteChoice.Maybe => "maybe",
        Domain.Kernel.VoteChoice.Unavailable => "unavailable",
        _ => throw new ArgumentOutOfRangeException(nameof(choice)),
    };

    public static Domain.Kernel.VoteChoice? ParseVoteChoice(string? value) => value switch
    {
        "available" => Domain.Kernel.VoteChoice.Available,
        "maybe" => Domain.Kernel.VoteChoice.Maybe,
        "unavailable" => Domain.Kernel.VoteChoice.Unavailable,
        _ => null,
    };

    public static string DeadlineChoice(PollDeadlineChoice choice) => choice switch
    {
        PollDeadlineChoice.Hours6 => "hours6",
        PollDeadlineChoice.Hours12 => "hours12",
        PollDeadlineChoice.Hours24 => "hours24",
        PollDeadlineChoice.Hours48 => "hours48",
        _ => throw new ArgumentOutOfRangeException(nameof(choice)),
    };

    public static PollDeadlineChoice? ParseDeadlineChoice(string? value) => value switch
    {
        null => null,
        "hours6" => PollDeadlineChoice.Hours6,
        "hours12" => PollDeadlineChoice.Hours12,
        "hours24" => PollDeadlineChoice.Hours24,
        "hours48" => PollDeadlineChoice.Hours48,
        _ => throw new FormatException($"Unknown poll deadline choice '{value}'."),
    };

    public static string JoinRequestStatus(JoinRequestStatus status) => status switch
    {
        Domain.Kernel.JoinRequestStatus.Pending => "pending",
        Domain.Kernel.JoinRequestStatus.Approved => "approved",
        Domain.Kernel.JoinRequestStatus.Rejected => "rejected",
        Domain.Kernel.JoinRequestStatus.Withdrawn => "withdrawn",
        Domain.Kernel.JoinRequestStatus.Lapsed => "lapsed",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    public static bool TryParseInstant(string? value, out Instant instant)
    {
        if (value is not null)
        {
            var parsed = InstantPattern.ExtendedIso.Parse(value);
            if (parsed.Success)
            {
                instant = parsed.Value;
                return true;
            }
        }

        instant = default;
        return false;
    }
}

/// <summary>
/// One candidate on the create-poll wire (D-211): its own start instant and its own length. The
/// length mirrors <c>CreateSessionRequest.DurationMinutes</c> — absent means the server default —
/// and the option's end stays derived from the pair, never sent, exactly as a session's does.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreatePollOptionRequest(string? StartTime, int? DurationMinutes);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreatePollRequest(
    string? Title,
    string? Platform,
    GameRequest? Game,
    string? Notes,
    int? MinAttendance,
    int? MaxCapacity,
    string? Visibility,
    IReadOnlyList<Guid>? AudienceGroupIds,
    IReadOnlyList<CreatePollOptionRequest>? Options,
    string? DeadlineChoice,
    IReadOnlyList<Guid>? InviteeUserIds,
    int? GuestLinkCount);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SuggestPollOptionRequest(string? StartTime, int? DurationMinutes);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record VoteOnPollOptionRequest(string? Choice);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record FinalizePollRequest(Guid OptionId);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreatePollGuestLinksRequest(int Count);

/// <summary>D-193: the poll analogue of <c>InviteSessionUsersRequest</c> (same wire shape).</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record InvitePollUsersRequest(IReadOnlyList<Guid> UserIds);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PollOptionResponse(
    Guid Id,
    string StartTime,
    // D-211: this option's own length; the poll carries none.
    int DurationMinutes,
    // D-076: a masked-for-viewer suggester's user id is absent, matching PollRosterEntryResponse.UserId's
    // own JsonIgnore mechanics.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? SuggestedByUserId,
    Guid? SuggestedByGuestIdentityId,
    int AvailableCount,
    int MaybeCount,
    int UnavailableCount,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ViewerVote,
    // Per-category voter lists, ordered by first-submitted-response ascending (D-119's allocation
    // vocabulary). Each entry is the SAME PollRosterEntryResponse.From conversion the roster uses,
    // so a masked voter's ParticipationId/DisplayName/Avatar agree exactly between the roster and
    // every option list -- there is no second masking pass here (D-076).
    IReadOnlyList<PollRosterEntryResponse> AvailableVoters,
    IReadOnlyList<PollRosterEntryResponse> MaybeVoters,
    IReadOnlyList<PollRosterEntryResponse> UnavailableVoters,
    // D-196: the resolved suggester, through the same PollRosterEntryResponse shape and masking the
    // roster/voter lists use — absent for an organizer-created option (no attribution).
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PollRosterEntryResponse? Suggester)
{
    public static PollOptionResponse From(PollOptionGridEntry entry) =>
        new(
            entry.Id.Value,
            SessionWireFormat.Format(entry.StartTime),
            entry.Duration.TotalMinutes,
            // The roster already masks a blocked-pair suggester's identity; this is a second wire
            // channel that must agree with it.
            entry.SuggestedByMasked ? null : entry.SuggestedBy.UserId?.Value,
            entry.SuggestedBy.GuestIdentityId?.Value,
            entry.AvailableCount,
            entry.MaybeCount,
            entry.UnavailableCount,
            entry.ViewerVote is { } choice ? PollWireFormat.VoteChoice(choice) : null,
            entry.AvailableVoters.Select(PollRosterEntryResponse.From).ToArray(),
            entry.MaybeVoters.Select(PollRosterEntryResponse.From).ToArray(),
            entry.UnavailableVoters.Select(PollRosterEntryResponse.From).ToArray(),
            entry.SuggestedByEntry is { } suggester ? PollRosterEntryResponse.From(suggester) : null);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PollRosterEntryResponse(
    // The stable row key (session precedent RosterEntryResponse.ParticipationId): the PollParticipant
    // id itself, never a user id, so a masked entry stays unkeyable-to-identity.
    Guid ParticipationId,
    // D-076: a masked entry's userId is absent, mirroring RosterEntryResponse.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? UserId,
    Guid? GuestIdentityId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DisplayName,
    bool IsActive,
    // D-188: absent for a masked row, and absent on every row of the guest arm (D-150), which shares
    // this type and builds its rows without an avatar.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AvatarResponse? Avatar)
{
    public static PollRosterEntryResponse From(PollRosterEntry entry) =>
        new(
            entry.ParticipationId.Value,
            entry.MaskedForViewer ? null : entry.Participant.UserId?.Value,
            entry.Participant.GuestIdentityId?.Value,
            entry.DisplayName?.Value,
            entry.IsActive,
            entry.MaskedForViewer || entry.Avatar is not { } avatar ? null : AvatarResponse.From(avatar));
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PollDetailResponse(
    Guid Id,
    Guid OrganizerId,
    string Title,
    string Platform,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] GameResponse? Game,
    string Notes,
    int MinAttendance,
    int? MaxCapacity,
    string Visibility,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] GroupProvenanceResponse? ViaGroup,
    bool SharedWithGroup,
    string DeadlineChoice,
    string Deadline,
    string SelectionWindowEndsAt,
    string Status,
    string CreatedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? SelectedOptionId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? CreatedSessionId,
    IReadOnlyList<PollOptionResponse> Options,
    IReadOnlyList<PollRosterEntryResponse> Roster,
    bool Muted)
{
    public static PollDetailResponse From(PollDetail detail) =>
        new(
            detail.Id.Value,
            detail.OrganizerId.Value,
            detail.Title.Value,
            SessionWireFormat.Platform(detail.Platform),
            detail.Game is { } game ? GameResponse.From(game) : null,
            detail.Notes.Value,
            detail.MinAttendance,
            detail.MaxCapacity,
            SessionWireFormat.Visibility(detail.Visibility),
            detail.ViaGroup is { } viaGroup ? GroupProvenanceResponse.From(viaGroup) : null,
            detail.Visibility == ContentVisibility.Groups && detail.ViaGroup is null,
            PollWireFormat.DeadlineChoice(detail.DeadlineChoice),
            SessionWireFormat.Format(detail.Deadline),
            SessionWireFormat.Format(detail.SelectionWindowEndsAt),
            PollWireFormat.Status(detail.Status),
            SessionWireFormat.Format(detail.CreatedAt),
            detail.SelectedOptionId?.Value,
            detail.CreatedSessionId?.Value,
            detail.Options.Select(PollOptionResponse.From).ToArray(),
            detail.Roster.Select(PollRosterEntryResponse.From).ToArray(),
            detail.Muted);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreatePollResponse(
    PollDetailResponse Poll,
    IReadOnlyList<GuestLinkInvitationResponse> GuestLinks);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record FinalizePollResponse(Guid SessionId, PollDetailResponse Poll);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PollJoinRequestViewResponse(
    Guid Id,
    Guid RequesterId,
    string Status,
    string CreatedAt)
{
    public static PollJoinRequestViewResponse From(JoinRequest request) =>
        new(
            request.Id.Value,
            request.RequesterId.Value,
            PollWireFormat.JoinRequestStatus(request.Status),
            SessionWireFormat.Format(request.CreatedAt));
}

// ---- PHASE6_DESIGN.md §4.1 "GET /polls" ----------------------------------------------------------

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PollSummaryResponse(
    Guid Id,
    Guid OrganizerId,
    string Title,
    string Status,
    string Deadline,
    string SelectionWindowEndsAt,
    string CreatedAt,
    int OptionCount,
    string Visibility,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] GroupProvenanceResponse? ViaGroup,
    bool SharedWithGroup)
{
    public static PollSummaryResponse From(PollSummary summary) =>
        new(
            summary.Id.Value,
            summary.OrganizerId.Value,
            summary.Title.Value,
            PollWireFormat.Status(summary.Status),
            SessionWireFormat.Format(summary.Deadline),
            SessionWireFormat.Format(summary.SelectionWindowEndsAt),
            SessionWireFormat.Format(summary.CreatedAt),
            summary.OptionCount,
            SessionWireFormat.Visibility(summary.Visibility),
            summary.ViaGroup is { } viaGroup ? GroupProvenanceResponse.From(viaGroup) : null,
            summary.Visibility == ContentVisibility.Groups && summary.ViaGroup is null);
}

// ---- PHASE6_DESIGN.md §4 "GET /{id}/finalization-preview" -----------------------------------------

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PollFinalizationPreviewRosterEntryResponse(
    // D-076: a masked entry's userId is absent, mirroring RosterEntryResponse/PollRosterEntryResponse.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? UserId,
    Guid? GuestIdentityId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DisplayName,
    string Status,
    bool IsWaitlisted,
    // D-076/D-188: a blocked counterpart is still a participant (D-086), so this preview masks like
    // every other surface -- absent for a masked row, and absent for a guest row or a tombstoned
    // participant.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AvatarResponse? Avatar)
{
    public static PollFinalizationPreviewRosterEntryResponse From(PollFinalizationPreviewRosterEntry entry) =>
        new(
            entry.MaskedForViewer ? null : entry.Participant.UserId?.Value,
            entry.Participant.GuestIdentityId?.Value,
            entry.DisplayName?.Value,
            SessionWireFormat.ParticipationStatus(entry.Status),
            entry.IsWaitlisted,
            entry.MaskedForViewer || entry.Avatar is not { } avatar ? null : AvatarResponse.From(avatar));
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PollFinalizationPreviewOptionResponse(
    Guid OptionId,
    string StartTime,
    bool MeetsMinimum,
    IReadOnlyList<PollFinalizationPreviewRosterEntryResponse> Roster)
{
    public static PollFinalizationPreviewOptionResponse From(PollFinalizationPreviewOption option) =>
        new(
            option.OptionId.Value,
            SessionWireFormat.Format(option.StartTime),
            option.MeetsMinimum,
            option.Roster.Select(PollFinalizationPreviewRosterEntryResponse.From).ToArray());
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PollFinalizationPreviewResponse(IReadOnlyList<PollFinalizationPreviewOptionResponse> Options)
{
    public static PollFinalizationPreviewResponse From(IReadOnlyList<PollFinalizationPreviewOption> options) =>
        new(options.Select(PollFinalizationPreviewOptionResponse.From).ToArray());
}
