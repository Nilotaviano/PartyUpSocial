using Microsoft.EntityFrameworkCore;
using PartyUp.Application.Abstractions;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;

namespace PartyUp.Infrastructure.Persistence.Repositories;

/// <summary>
/// The poll surface's one read-repository dependency (PHASE6_DESIGN.md §4): bulk display-name
/// resolution, mirroring <c>SessionReadRepository.FetchDisplayNamesAsync</c>.
/// </summary>
public sealed class PollReadRepository(AppDbContext context) : IPollReadRepository
{
    public async Task<IReadOnlyDictionary<Guid, DisplayName?>> FindDisplayNamesAsync(
        IReadOnlyCollection<UserId> userIds,
        CancellationToken cancellationToken)
    {
        if (userIds.Count == 0)
        {
            return new Dictionary<Guid, DisplayName?>();
        }

        List<UserId> ids = [.. userIds.Distinct()];
        var rows = await context.DomainUsers
            .AsNoTracking()
            .Where(u => ids.Contains(u.Id))
            .Select(u => new { u.Id, u.DisplayName })
            .ToListAsync(cancellationToken);

        Dictionary<Guid, DisplayName?> result = [];
        foreach (var row in rows)
        {
            result[row.Id.Value] = row.DisplayName;
        }

        return result;
    }
}
