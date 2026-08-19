using System.Linq;
using Microsoft.EntityFrameworkCore;
using PartyUp.Application.FriendGroups;
using PartyUp.Domain.FriendGroups;
using PartyUp.Domain.Kernel;

namespace PartyUp.Infrastructure.Persistence.Repositories;

/// <summary>
/// D-206 provenance for a page of list rows: which targeted group grants THIS viewer rule-2g access,
/// resolved for the whole page in one query over the union of audiences — never per row. Rows whose
/// viewer is not in any targeted group get no entry, which is exactly the neutral "shared with a
/// group" case; group names never leave here for a non-member.
/// </summary>
internal static class GroupProvenanceReader
{
    public static async Task<IReadOnlyDictionary<Guid, GroupProvenance>> ResolveAsync(
        AppDbContext context,
        IReadOnlyList<(Guid TargetId, IReadOnlyCollection<FriendGroupId> Audience)> targets,
        UserId viewerId,
        CancellationToken cancellationToken)
    {
        HashSet<FriendGroupId> union = [];
        foreach ((_, IReadOnlyCollection<FriendGroupId> audience) in targets)
        {
            union.UnionWith(audience);
        }

        if (union.Count == 0)
        {
            return new Dictionary<Guid, GroupProvenance>();
        }

        FriendGroupId[] candidates = [.. union];
        var rows = await context.FriendGroups
            .AsNoTracking()
            .Where(group =>
                candidates.Contains(group.Id) &&
                (group.OwnerId == viewerId ||
                 group.Members.Any(member =>
                     member.UserId == viewerId && member.Status == FriendGroupMemberStatus.Accepted)))
            .Select(group => new { group.Id, group.Name })
            .ToListAsync(cancellationToken);

        Dictionary<Guid, GroupProvenance> provenance = [];
        if (rows.Count == 0)
        {
            return provenance;
        }

        Dictionary<FriendGroupId, GroupName> granting = [];
        foreach (var row in rows)
        {
            granting[row.Id] = row.Name;
        }

        foreach ((Guid targetId, IReadOnlyCollection<FriendGroupId> audience) in targets)
        {
            // The owned audience-group collection each target's Poll/Session aggregate loads from
            // (session_audience_groups / poll_audience_groups) carries no ORDER BY, so its
            // materialization order is not guaranteed stable across query shapes — a page query
            // batching many aggregates and a single-row detail query can return the same aggregate's
            // rows in different orders. Sorting by group id here, ascending, before the "first match
            // wins" tie-break keeps that tie-break deterministic regardless of load order.
            foreach (FriendGroupId groupId in audience.OrderBy(id => id.Value))
            {
                if (granting.TryGetValue(groupId, out GroupName? name))
                {
                    provenance[targetId] = new GroupProvenance(groupId, name);
                    break;
                }
            }
        }

        return provenance;
    }
}
