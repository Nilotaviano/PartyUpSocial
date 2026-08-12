using NodaTime;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;

namespace PartyUp.Application.Abstractions;

public interface IAdminUserReader
{
    Task<AdminUserPage> ListAsync(
        string? query,
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    Task<AdminUserRecord?> GetAsync(
        UserId id,
        CancellationToken cancellationToken);
}

/// <summary>
/// D-179's per-user admin row. <see cref="LastSeenAt"/> is the ratified <c>users.last_seen_at</c>
/// column (PHASE8_DESIGN.md §2 Q1), null for an account that has made no authenticated request since
/// the column shipped and for a tombstone (deletion nulls it with the rest of the PII).
/// <see cref="SessionsJoined"/> counts the sessions the user takes part in and has not been removed
/// from — sessions they organize are <see cref="SessionsOrganized"/>, never both.
/// <see cref="FriendCount"/> counts accepted friendships from both canonical sides.
/// </summary>
public sealed record AdminUserRecord(
    UserId Id,
    string? Username,
    string? DisplayName,
    string? Email,
    Instant CreatedAt,
    UserLifecycle Lifecycle,
    Instant? BannedAt,
    Instant? LastSeenAt,
    int SessionsOrganized,
    int SessionsJoined,
    int FriendCount);

public sealed record AdminUserPage(
    IReadOnlyList<AdminUserRecord> Users,
    int Total);
