using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;

namespace PartyUp.Application.Abstractions;

public interface IPublicLinkBuilder
{
    string Profile(Username username);

    string Add(FriendCode friendCode);
}
