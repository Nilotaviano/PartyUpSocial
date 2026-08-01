using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;

namespace PartyUp.Application.Admin;

public sealed record AdminUser(
    UserId Id,
    string? Username,
    string? DisplayName,
    string? Email,
    Instant CreatedAt,
    UserLifecycle Lifecycle,
    Instant? BannedAt);

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
            user.BannedAt);

    private static string MaskEmail(string address)
    {
        int separator = address.IndexOf('@', StringComparison.Ordinal);
        return separator <= 0
            ? "***"
            : string.Concat(address.AsSpan(0, 1), "***", address.AsSpan(separator));
    }
}
