using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;

namespace PartyUp.Application.Admin;

/// <summary>
/// D-179: the per-user admin projection — identity, ban state, and the four stats PRODUCT_SPEC §10
/// names (registered date, last activity, sessions organized and joined) plus the friend count. The
/// email is always masked; nothing here widens what an admin can see beyond counts.
/// </summary>
public sealed record AdminUser(
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

internal static class AdminUserMapping
{
    internal static AdminUser FromRecord(AdminUserRecord user) =>
        new(
            user.Id,
            user.Username,
            user.DisplayName,
            user.Email is null ? null : MaskEmail(user.Email),
            user.CreatedAt,
            user.Lifecycle,
            user.BannedAt,
            user.LastSeenAt,
            user.SessionsOrganized,
            user.SessionsJoined,
            user.FriendCount);

    private static string MaskEmail(string address)
    {
        int separator = address.IndexOf('@', StringComparison.Ordinal);
        return separator <= 0
            ? "***"
            : string.Concat(address.AsSpan(0, 1), "***", address.AsSpan(separator));
    }
}
