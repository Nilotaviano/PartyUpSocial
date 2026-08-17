using NodaTime;
using PartyUp.Application.FriendGroups;
using PartyUp.Domain.FriendGroups;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Notifications;
using PartyUp.Domain.Sessions;
using PartyUp.Domain.Users;

namespace PartyUp.Application.Sessions;

/// <summary>
/// PHASE5_DESIGN.md §5.1: the viewer's relation to a session, evaluated in exactly this precedence
/// order (first match wins, so the value is total and single-valued) over the shipped participation
/// and join-request enums. <c>Invitee</c> is not cosmetic — it is what lets D-103's first Home region
/// distinguish a Pending invitation awaiting a response from a <c>Going</c> RSVP. <c>Inherited</c>
/// covers every unattached viewer, including a <c>Removed</c> participation with an unblocked
/// inherited path (A-077 (d)).
/// </summary>
public enum ViewerRelation
{
    Organizer,
    Invitee,
    Participant,
    Requester,
    Inherited,
}

/// <summary>
/// The read shapes <c>ISessionReadRepository</c> returns (PHASE4_DESIGN.md §5.1). Every read is
/// rendered through visibility plus identity masking before it reaches an endpoint; that masking is
/// an Application/API concern layered on top of these DTOs, not performed by the repository itself.
/// One session as a list/summary read renders it, extended by PHASE5_DESIGN.md §5.1: its
/// <c>OrganizerDisplayName</c> is null for a tombstoned organizer (D-092), and its
/// <c>ViewerRelation</c> is the precedence value the enum above states. Both default to the
/// pre-Phase-5 value so existing positional-constructor call sites keep compiling.
/// </summary>
public sealed record SessionSummary(
    SessionId Id,
    UserId OrganizerId,
    SessionTitle Title,
    Instant StartTime,
    Duration15 Duration,
    Platform Platform,
    SessionGame? Game,
    SessionStatus Status,
    int? MaxCapacity,
    int ActiveOccupancy,
    DisplayName? OrganizerDisplayName = null,
    ViewerRelation ViewerRelation = ViewerRelation.Inherited,
    ContentVisibility Visibility = ContentVisibility.Public,
    GroupProvenance? ViaGroup = null);

/// <summary>
/// One roster row as a detail/roster read renders it — never the bearer token or link id.
/// <see cref="MaskedForViewer"/> is the §5.1 blocked-pair signal (D-076): set to <c>true</c> only by
/// the use case applying rule 2 (<c>SessionReadUseCases</c>), never by the repository. It is a
/// distinct signal from <see cref="DisplayName"/> being null for a tombstoned owner (D-092) — both
/// render as the shipped Anonymous User string client-side, but only a masked entry also drops its
/// <see cref="ParticipantRef"/>'s id on the wire (S5).
/// </summary>
public sealed record RosterEntry(
    ParticipationId ParticipationId,
    ParticipantRef Participant,
    DisplayName? DisplayName,
    ParticipationStatus Status,
    bool IsWaitlisted,
    ParticipationOrigin Origin,
    ReminderOffset? ReminderChoice,
    bool MaskedForViewer = false,
    Avatar? Avatar = null);

/// <summary>
/// One entry of the organizer-only edit-prefill audience (D-206): the id is the organizer's own edit
/// data and always present; <see cref="Name"/> is the live group name, populated only when the
/// organizer currently owns or holds an <c>Accepted</c> row in that group — the same membership guard
/// <c>ViaGroup</c> uses, applied per-entry instead of to a single resolved group. A left-and-retargeted
/// group keeps its id with a null name (D-201/D-203 retained targeting).
/// </summary>
public sealed record AudienceGroupEntry(FriendGroupId GroupId, GroupName? Name);

/// <summary>
/// The full session read (§8: <c>SessionDetail</c>). <see cref="ConcurrencyToken"/> is the raw
/// Postgres <c>xmin</c>; A-071's opaque HMAC <c>version</c> string is computed at the API layer from
/// it and is never itself stored or exposed here.
/// </summary>
public sealed record SessionDetail(
    SessionId Id,
    UserId OrganizerId,
    SessionTitle Title,
    Instant StartTime,
    Duration15 Duration,
    Platform Platform,
    SessionGame? Game,
    SessionNotes Notes,
    int MinAttendance,
    int? MaxCapacity,
    SessionStatus Status,
    Instant CreatedAt,
    IReadOnlyList<RosterEntry> Roster,
    uint ConcurrencyToken,
    bool Muted = false,
    ContentVisibility Visibility = ContentVisibility.Public,
    GroupProvenance? ViaGroup = null,
    IReadOnlyList<AudienceGroupEntry>? AudienceGroups = null);

public sealed record JoinRequestSummary(
    JoinRequestId Id,
    UserId RequesterId,
    JoinRequestStatus Status,
    Instant CreatedAt,
    DisplayName? RequesterDisplayName = null);

/// <summary>
/// PHASE5_DESIGN.md §5.1: the D-103 Home region 1 read model. Both lists are scoped to future,
/// non-<c>Canceled</c>/non-<c>Expired</c> sessions (§12-14, materialized status only), ordered by
/// session start ascending, and capped at 50 each with a display-bound <c>HasMore</c> flag — never a
/// second cursor contract (§8's "See all" links to the shipped list surfaces instead).
/// </summary>
public sealed record ActionableInvitation(SessionSummary Session, Instant InvitedAt);

public sealed record ActionableJoinRequest(
    JoinRequestId Id,
    SessionSummary Session,
    UserId RequesterId,
    DisplayName? RequesterDisplayName,
    Instant RequestedAt);

public sealed record ActionableInbox(
    IReadOnlyList<ActionableInvitation> Invitations,
    IReadOnlyList<ActionableJoinRequest> JoinRequests,
    bool HasMoreInvitations,
    bool HasMoreJoinRequests);

/// <summary>What a claimed or unclaimed guest bearer currently sees through their link (§5.1, §8.1).</summary>
public sealed record GuestSessionView(
    SessionId SessionId,
    SessionTitle Title,
    Instant StartTime,
    Duration15 Duration,
    Platform Platform,
    SessionGame? Game,
    SessionNotes Notes,
    GuestIdentityId? GuestIdentityId,
    DisplayName? DisplayName,
    ParticipationStatus? Response);

/// <summary>The constrained terminal notice A-074 renders for a revoked, canceled, or ended link.</summary>
public sealed record GuestTerminalView(SessionId SessionId, GuestLinkView Notice);

/// <summary>
/// The list a <see cref="SessionListCursor"/> was issued for (A-072). A cursor decoded for
/// presentation to any list other than the one that issued it is rejected — <c>Unbound</c> is the
/// zero value used only for a freshly-minted cursor's transient internal (pre-rebind) representation
/// and is never a value a client-presented cursor may declare itself as. PHASE5_DESIGN.md A-077 (f)
/// (round-1 fix F5): <c>Discover</c> is a named scope like every other list — <c>ListDiscoverAsync</c>
/// mints its page's cursor with this scope directly (<c>SessionReadRepository.cs</c>), so a discover
/// cursor is a client-presented value exactly like an upcoming/history/join-requests one, never left
/// at <c>Unbound</c> as a scope-check sentinel.
/// </summary>
public enum SessionListScope
{
    Unbound = 0,
    Upcoming = 1,
    History = 2,
    JoinRequests = 3,
    Discover = 4,

    /// <summary>
    /// <c>GET /polls/{id}/join-requests</c> reuses this shared A-072 cursor idiom rather than
    /// duplicating it -- despite the "Session" name, the cursor and page types carry no
    /// session-specific state, so a poll-owned scope is a new enum member, not a new mechanism.
    /// </summary>
    PollJoinRequests = 5,

    /// <summary>PHASE6_DESIGN.md §4.1: <c>GET /polls</c>'s involved band (§7 rule 1).</summary>
    PollsInvolved = 6,

    /// <summary>PHASE6_DESIGN.md §4.1: <c>GET /polls</c>'s discoverable band (§7 rule 2, the D-118 entry point).</summary>
    PollsDiscoverable = 7,

    /// <summary>
    /// PHASE7_DESIGN.md §4: <c>GET /notifications?scope=all</c> — named <c>notifications-all</c> on
    /// the wire per A-072's two-distinct-scope-names requirement, reusing this shared cursor
    /// mechanism exactly like <see cref="PollJoinRequests"/> above despite the "Session" name.
    /// </summary>
    NotificationsAll = 8,

    /// <summary>PHASE7_DESIGN.md §4: <c>GET /notifications?scope=unread</c> — <c>notifications-unread</c> on the wire.</summary>
    NotificationsUnread = 9,
}

/// <summary>The A-072 keyset cursor: the list's sort-key instant, the tie-breaking id, and the
/// issuing list's <see cref="SessionListScope"/>.</summary>
public readonly record struct SessionListCursor(Instant SortKey, Guid Id, SessionListScope Scope = SessionListScope.Unbound)
{
    public string Encode() =>
        System.Buffers.Text.Base64Url.EncodeToString(
            System.Text.Encoding.UTF8.GetBytes(
                FormattableString.Invariant($"{(int)Scope}:{SortKey.ToUnixTimeTicks()}:{Id:N}")));

    /// <summary>Decodes without checking the scope — for internal rebinding of a freshly-issued,
    /// not-yet-scoped cursor. Never use this overload to validate a client-presented cursor.</summary>
    public static SessionListCursor? TryDecode(string? cursor) => TryDecodeCore(cursor, expectedScope: null);

    /// <summary>Decodes a client-presented cursor, rejecting one issued for a different list.</summary>
    public static SessionListCursor? TryDecode(string? cursor, SessionListScope expectedScope) =>
        TryDecodeCore(cursor, expectedScope);

    private static SessionListCursor? TryDecodeCore(string? cursor, SessionListScope? expectedScope)
    {
        if (string.IsNullOrEmpty(cursor))
        {
            return null;
        }

        try
        {
            string decoded = System.Text.Encoding.UTF8.GetString(
                System.Buffers.Text.Base64Url.DecodeFromChars(cursor));
            string[] parts = decoded.Split(':');
            if (parts.Length != 3
                || !int.TryParse(parts[0], out int scopeValue)
                || !Enum.IsDefined(typeof(SessionListScope), scopeValue)
                || !long.TryParse(parts[1], out long ticks)
                || !Guid.TryParse(parts[2], out Guid id))
            {
                return null;
            }

            var scope = (SessionListScope)scopeValue;
            if (expectedScope is { } expected && scope != expected)
            {
                return null;
            }

            return new SessionListCursor(Instant.FromUnixTimeTicks(ticks), id, scope);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}

/// <summary>The A-072 pagination envelope: <c>{ items, nextCursor }</c>, page size fixed by the caller.</summary>
public sealed record SessionPage<T>(IReadOnlyList<T> Items, string? NextCursor);

/// <summary>
/// §4.2's locked handoff projection. <c>session_reminder_handoffs</c> is a persistence-only entity
/// (`src/PartyUp.Infrastructure/Persistence/SessionReminderHandoff.cs`) that Application may not
/// reference, so <see cref="ISessionRepository.LoadForUpdateAsync"/> exposes only the fields a
/// planning use case needs; <see cref="State"/> mirrors the Infrastructure
/// <c>SessionReminderHandoffState</c> enum's numeric values one-for-one.
/// </summary>
public sealed record SessionReminderHandoffLock(
    Guid Id,
    UserId RecipientUserId,
    Instant DueAt,
    int State,
    int Version,
    int Attempts);

/// <summary>
/// §4.2: what <c>LoadForUpdateAsync</c> returns once the session parent is locked — the aggregate
/// itself plus the two sibling tables (mutes, handoffs) that are written only under that same lock
/// but are not part of the Session aggregate's own child collections.
/// </summary>
public sealed record SessionForUpdate(
    Session Session,
    IReadOnlyList<NotificationMute> Mutes,
    IReadOnlyList<SessionReminderHandoffLock> Handoffs);
