using NodaTime;
using PartyUp.Domain.FriendGroups;
using PartyUp.Domain.Kernel;

namespace PartyUp.Application.FriendGroups;

public sealed record FriendGroupCommandResult(bool Succeeded, string? Code, FriendGroupSummary? Group)
{
    public static FriendGroupCommandResult Ok(FriendGroupSummary? group = null) => new(true, null, group);

    public static FriendGroupCommandResult Failed(string code) => new(false, code, null);
}

/// <summary>The group-level fields every viewer with any visibility may see (D-199): id, name, owner, creation time.</summary>
public sealed record FriendGroupSummary(FriendGroupId Id, string Name, UserId OwnerId, Instant CreatedAt)
{
    internal static FriendGroupSummary From(FriendGroup group) =>
        new(group.Id, group.Name.Value, group.OwnerId, group.CreatedAt);
}
