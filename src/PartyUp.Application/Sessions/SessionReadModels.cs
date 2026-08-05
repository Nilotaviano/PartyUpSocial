using NodaTime;
using PartyUp.Domain.Kernel;
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

/// <summary>The A-072 keyset cursor: the list's sort-key instant plus the tie-breaking id.</summary>
public readonly record struct SessionListCursor(Instant SortKey, Guid Id)
{
    public string Encode() =>
        System.Buffers.Text.Base64Url.EncodeToString(
            System.Text.Encoding.UTF8.GetBytes(
                FormattableString.Invariant($"{SortKey.ToUnixTimeTicks()}:{Id:N}")));

    public static SessionListCursor? TryDecode(string? cursor)
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
            if (parts.Length != 2
                || !long.TryParse(parts[0], out long ticks)
                || !Guid.TryParse(parts[1], out Guid id))
            {
                return null;
            }

            return new SessionListCursor(Instant.FromUnixTimeTicks(ticks), id);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}

/// <summary>The A-072 pagination envelope: <c>{ items, nextCursor }</c>, page size fixed by the caller.</summary>
public sealed record SessionPage<T>(IReadOnlyList<T> Items, string? NextCursor);
