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

public sealed record AdminUserRecord(
    UserId Id,
    string? Username,
    string? DisplayName,
    string? Email,
    Instant CreatedAt,
    UserLifecycle Lifecycle,
    Instant? BannedAt);

public sealed record AdminUserPage(
    IReadOnlyList<AdminUserRecord> Users,
    int Total);
