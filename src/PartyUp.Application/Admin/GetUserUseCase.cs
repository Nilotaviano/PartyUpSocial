using PartyUp.Application.Abstractions;
using PartyUp.Domain.Kernel;

namespace PartyUp.Application.Admin;

public sealed class GetUserUseCase(IAdminUserReader users)
{
    public async Task<AdminUser?> ExecuteAsync(
        UserId id,
        CancellationToken cancellationToken)
    {
        AdminUserRecord? result = await users.GetAsync(id, cancellationToken);
        return result is null ? null : AdminUserMapping.FromRecord(result);
    }
}
