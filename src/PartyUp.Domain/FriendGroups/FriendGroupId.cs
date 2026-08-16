using PartyUp.Domain.Kernel;

namespace PartyUp.Domain.FriendGroups;

public readonly record struct FriendGroupId(Guid Value) : ITypedId<FriendGroupId>
{
    public static FriendGroupId New() => new(Guid.CreateVersion7());

    public static FriendGroupId From(Guid value) => new(value);

    public override string ToString() => Value.ToString();
}
