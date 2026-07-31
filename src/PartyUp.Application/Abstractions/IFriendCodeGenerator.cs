using PartyUp.Domain.Users;

namespace PartyUp.Application.Abstractions;

public interface IFriendCodeGenerator
{
    Task<FriendCode> NextAsync(CancellationToken cancellationToken);
}
