using PartyUp.Application.Abstractions;

namespace PartyUp.Application.Admin;

public sealed class ListUsersUseCase(IAdminUserReader users)
{
    public async Task<AdminUsersResult> ExecuteAsync(
        string? query,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        AdminUserPage result = await users.ListAsync(
            string.IsNullOrWhiteSpace(query) ? null : query.Trim(),
            page,
            pageSize,
            cancellationToken);
        return new AdminUsersResult(
            result.Users.Select(AdminUserMapping.FromRecord).ToArray(),
            page,
            pageSize,
            result.Total);
    }
}

public sealed record AdminUsersResult(
    IReadOnlyList<AdminUser> Users,
    int Page,
    int PageSize,
    int Total);
