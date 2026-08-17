using System.Text.Json.Serialization;
using PartyUp.Api.Contracts.Sessions;
using PartyUp.Application.Polls;
using PartyUp.Domain.Sessions;

namespace PartyUp.Api.Contracts.Polls;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ClaimGuestPollRequest(string? DisplayName);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RenameGuestPollIdentityRequest(string? DisplayName);

/// <summary>
/// The poll guest bearer's own flat response (PHASE6_DESIGN.md §4, ticket-P6-S4b), parallel to
/// <see cref="GuestSessionResponse"/> but participant-identical rather than roster-less (D-030/D-114):
/// the live shape carries the same masked per-voter grid the registered <c>PollDetailResponse</c>
/// does, flattened onto this record instead of nested, matching the shipped guest-session convention.
/// The <c>finalized</c> state is this arm's own addition — <see cref="GuestSessionResponse"/> has no
/// analogue because a session-owned link is never re-parented — carrying no live poll fields, only the
/// created session id. It carries no re-exchange URL: the server never stores the bearer's raw token
/// (A-015), so it cannot reconstruct one — the web client renders its own "re-open your invitation
/// link" copy from <c>state:"finalized"</c> alone.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GuestPollResponse(
    string State,
    Guid PollId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? SessionId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Title,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Platform,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] GameResponse? Game,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Notes,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Deadline,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SelectionWindowEndsAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Status,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? GuestIdentityId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DisplayName,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<PollOptionResponse>? Options,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<PollRosterEntryResponse>? Roster)
{
    public const string LiveState = "live";
    public const string FinalizedState = "finalized";

    public static GuestPollResponse Live(GuestPollDetail guestDetail)
    {
        PollDetail detail = guestDetail.Detail;
        return new GuestPollResponse(
            LiveState,
            detail.Id.Value,
            null,
            detail.Title.Value,
            SessionWireFormat.Platform(detail.Platform),
            detail.Game is { } game ? GameResponse.From(game) : null,
            detail.Notes.Value,
            SessionWireFormat.Format(detail.Deadline),
            SessionWireFormat.Format(detail.SelectionWindowEndsAt),
            PollWireFormat.Status(detail.Status),
            guestDetail.OwnGuestIdentityId?.Value,
            guestDetail.OwnDisplayName?.Value,
            detail.Options.Select(PollOptionResponse.From).ToArray(),
            detail.Roster.Select(PollRosterEntryResponse.From).ToArray());
    }

    public static GuestPollResponse Terminal(Guid pollId, GuestLinkView notice) =>
        new(NoticeState(notice), pollId, null, null, null, null, null, null, null, null, null, null, null, null);

    public static GuestPollResponse Finalized(Guid pollId, Guid sessionId) =>
        new(FinalizedState, pollId, sessionId, null, null, null, null, null, null, null, null, null, null, null);

    private static string NoticeState(GuestLinkView notice) => notice switch
    {
        GuestLinkView.CanceledNotice => "canceled",
        GuestLinkView.RevokedNotice => "revoked",
        GuestLinkView.EndedNotice => "ended",
        GuestLinkView.PollExpiredNotice => "pollExpired",
        _ => throw new ArgumentOutOfRangeException(nameof(notice)),
    };
}
