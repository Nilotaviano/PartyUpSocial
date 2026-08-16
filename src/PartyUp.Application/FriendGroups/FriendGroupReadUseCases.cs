using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Domain.FriendGroups;
using PartyUp.Domain.Kernel;

namespace PartyUp.Application.FriendGroups;

/// <summary>One roster row (D-199): visible only to the group's owner or an Accepted member.</summary>
public sealed record FriendGroupMemberView(UserId UserId, string Status, Instant InvitedAt, Instant? AcceptedAt)
{
    internal static FriendGroupMemberView From(FriendGroupMember member) =>
        new(member.UserId, StatusText(member.Status), member.InvitedAt, member.AcceptedAt);

    private static string StatusText(FriendGroupMemberStatus status) => status switch
    {
        FriendGroupMemberStatus.Invited => "invited",
        FriendGroupMemberStatus.Accepted => "accepted",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown friend group member status."),
    };
}

/// <summary>A pending invite the actor holds in someone else's group — name + owner only (D-199).</summary>
public sealed record FriendGroupInviteSummary(FriendGroupId Id, string Name, UserId OwnerId, Instant InvitedAt);

public sealed record FriendGroupListResult(
    IReadOnlyList<FriendGroupSummary> Owned,
    IReadOnlyList<FriendGroupSummary> MemberOf,
    IReadOnlyList<FriendGroupInviteSummary> Invites);

/// <summary>
/// <see cref="Members"/> is null for an <c>Invited</c>-only viewer (name + owner is all they may read,
/// D-199) and populated for the owner or an Accepted member.
/// </summary>
public sealed record FriendGroupDetail(
    FriendGroupId Id,
    string Name,
    UserId OwnerId,
    Instant CreatedAt,
    IReadOnlyList<FriendGroupMemberView>? Members);

/// <summary>
/// The FriendGroup read surface (D-199): every group the actor <see cref="FriendGroup.Involves"/>,
/// split by the actor's own relationship to it, and one group's detail — fail-closed to <c>null</c>
/// for anyone not involved, identical to a nonexistent group (D-198/D-199).
/// </summary>
public sealed class FriendGroupReadUseCases(IFriendGroupRepository groups)
{
    public async Task<FriendGroupListResult> ListMineAsync(UserId actorId, CancellationToken cancellationToken)
    {
        IReadOnlyCollection<FriendGroup> involved = await groups.ListInvolvingAsync(actorId, cancellationToken);

        List<FriendGroupSummary> owned = [];
        List<FriendGroupSummary> memberOf = [];
        List<FriendGroupInviteSummary> invites = [];

        foreach (FriendGroup group in involved)
        {
            if (group.OwnerId == actorId)
            {
                owned.Add(FriendGroupSummary.From(group));
                continue;
            }

            FriendGroupMember? row = group.Members.FirstOrDefault(member => member.UserId == actorId);
            if (row is null)
            {
                continue;
            }

            if (row.Status == FriendGroupMemberStatus.Accepted)
            {
                memberOf.Add(FriendGroupSummary.From(group));
            }
            else
            {
                invites.Add(new FriendGroupInviteSummary(group.Id, group.Name.Value, group.OwnerId, row.InvitedAt));
            }
        }

        return new FriendGroupListResult(owned, memberOf, invites);
    }

    public async Task<FriendGroupDetail?> GetAsync(
        UserId actorId,
        FriendGroupId groupId,
        CancellationToken cancellationToken)
    {
        FriendGroup? group = await groups.FindNoTrackingAsync(groupId, cancellationToken);
        if (group is null || !group.Involves(actorId))
        {
            return null;
        }

        IReadOnlyList<FriendGroupMemberView>? members = group.HasAcceptedMember(actorId)
            ? [.. group.Members.Select(FriendGroupMemberView.From)]
            : null;

        return new FriendGroupDetail(group.Id, group.Name.Value, group.OwnerId, group.CreatedAt, members);
    }
}
