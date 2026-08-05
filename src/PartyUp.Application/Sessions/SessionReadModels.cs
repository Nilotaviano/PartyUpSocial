using NodaTime;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Notifications;
using PartyUp.Domain.Sessions;

namespace PartyUp.Application.Sessions;

/// <summary>
/// The read shapes <c>ISessionReadRepository</c> returns (PHASE4_DESIGN.md §5.1). Every read is
/// rendered through visibility plus identity masking before it reaches an endpoint; that masking is
/// an Application/API concern layered on top of these DTOs, not performed by the repository itself.
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
    bool IsPrivate,
    int? MaxCapacity,
    int ActiveOccupancy);

/// <summary>One roster row as a detail/roster read renders it — never the bearer token or link id.</summary>
public sealed record RosterEntry(
    ParticipationId ParticipationId,
    ParticipantRef Participant,
    DisplayName? DisplayName,
    ParticipationStatus Status,
    bool IsWaitlisted,
    ParticipationOrigin Origin,
    ReminderOffset? ReminderChoice);

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
    bool IsPrivate,
    SessionStatus Status,
    Instant CreatedAt,
    IReadOnlyList<RosterEntry> Roster,
    uint ConcurrencyToken);

public sealed record JoinRequestSummary(
    JoinRequestId Id,
    UserId RequesterId,
    JoinRequestStatus Status,
    Instant CreatedAt);

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
/// zero value used only for the cursor's transient internal (pre-rebind) representation and is
/// never a value a client-presented cursor may declare itself as.
/// </summary>
public enum SessionListScope
{
    Unbound = 0,
    Upcoming = 1,
    History = 2,
    JoinRequests = 3,
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
