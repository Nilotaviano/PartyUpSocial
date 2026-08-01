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

public sealed record LockedFriendPair(User First, User Second);
