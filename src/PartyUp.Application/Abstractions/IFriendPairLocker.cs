using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;

namespace PartyUp.Application.Abstractions;

public interface IFriendPairLocker
{
    Task<LockedFriendPair?> LockAsync(
        UserId first,
        UserId second,
        CancellationToken cancellationToken);
}

public sealed record LockedFriendPair
{
    public LockedFriendPair(User first, User second)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);

        if (first.Id.Value.CompareTo(second.Id.Value) < 0)
        {
            First = first;
            Second = second;
        }
        else
        {
            First = second;
            Second = first;
        }
    }

    public User First { get; }

    public User Second { get; }
}
