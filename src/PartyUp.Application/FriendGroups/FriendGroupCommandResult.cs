using NodaTime;
using PartyUp.Domain.FriendGroups;
using PartyUp.Domain.Kernel;

namespace PartyUp.Application.FriendGroups;

public sealed record FriendGroupCommandResult(bool Succeeded, string? Code, FriendGroupSummary? Group)
{
    public static FriendGroupCommandResult Ok(FriendGroupSummary? group = null) => new(true, null, group);

    public static FriendGroupCommandResult Failed(string code) => new(false, code, null);
}

/// <summary>
/// The group-level fields every viewer with any visibility may see (D-199): id, name, owner,
/// creation time, and the humans-in-group count. <see cref="MemberCount"/> counts the owner (always
/// implicit, D-198) plus every <c>Accepted</c> row — <c>Invited</c> rows are not yet "in" the group,
/// so they do not count toward it. Computed off the already-loaded <see cref="FriendGroup.Members"/>
/// collection, never a separate query.
/// </summary>
public sealed record FriendGroupSummary(FriendGroupId Id, string Name, UserId OwnerId, Instant CreatedAt, int MemberCount)
{
    internal static FriendGroupSummary From(FriendGroup group) =>
        new(
            group.Id,
            group.Name.Value,
            group.OwnerId,
            group.CreatedAt,
            1 + group.Members.Count(member => member.Status == FriendGroupMemberStatus.Accepted));
}
